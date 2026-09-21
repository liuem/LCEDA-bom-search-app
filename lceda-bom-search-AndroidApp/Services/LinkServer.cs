using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace lceda_bom_search_AndroidApp;

/// <summary>
/// 内置联动服务器：PC 上的立创 EDA 扩展与本机通信的入口。
/// 同一端口（默认 8266）同时支持：
/// - WebSocket（任意 Upgrade 请求）：插件主链路，扫描事件实时推送；
/// - HTTP：GET /info 状态、GET /events?since=N 事件补拉、POST /bom 下发 BOM（fetch 兜底）。
/// 仅监听局域网，无鉴权（家庭内网使用；勿在不可信网络开启）。
/// </summary>
public static class LinkServer
{
    public const int DefaultPort = 8266;

    /// <summary>
    /// WS 消息分片阈值（字符）。EDA 客户端的 WebSocket 包装层扛不住大消息
    /// （下发 BOM 后全量状态 ~10KB+ 会把连接打死），超限消息拆成多个小信封发送、对端重组。
    /// 信封格式：{"ck":1,"mid":"..","i":n,"n":total,"d":"片段"}
    /// </summary>
    public const int ChunkSize = 2048;

    static TcpListener? _listener;
    static readonly List<WsClient> _clients = [];
    static readonly object _gate = new();
    static readonly Queue<JsonElement> _events = new();
    const int EventCap = 400;
    static long _seq;
    static string _stateJson = "{}";   // 只在状态变化线程重建，服务器线程只读

    public static bool Running { get; private set; }
    public static int ClientCount { get; private set; }

    /// <summary>状态变化（启动/停止/客户端增减），可能在任意线程触发。</summary>
    public static event Action? StatusChanged;

    static readonly JsonSerializerOptions J = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // ---------------- 生命周期 ----------------

    public static void Start()
    {
        if (Running) return;
        try
        {
            _listener = new TcpListener(IPAddress.Any, DefaultPort);
            _listener.Start();
            Running = true;
            RebuildState();
            UpdateSessionPower(); // 服务一起来就持唤醒锁，不等第一个客户端
            _ = Task.Run(AcceptLoop);
            StatusChanged?.Invoke();
        }
        catch (Exception e)
        {
            Console.WriteLine($"[LinkServer] 启动失败: {e.Message}");
            _listener = null;
            Running = false;
        }
    }

    /// <summary>给 UI 展示的本机局域网地址（优先无线网卡 IPv4）。</summary>
    public static string? LanAddress()
    {
        // 首选：UDP 无包 connect 取默认路由出口地址（不真正发包，任何网络下都准确）
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect("223.5.5.5", 53);
            if ((s.LocalEndPoint as IPEndPoint)?.Address is { } ip && !IPAddress.IsLoopback(ip))
                return $"{ip}:{DefaultPort}";
        }
        catch { }
        // 兜底：遍历网卡
        try
        {
            string? best = null;
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.IsReceiveOnly) continue;
                var isWifi = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
                var isEther = ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet;
                // Android 的虚拟隧道/回环接口没有有效 IPv4 网关，靠单播地址过滤
                foreach (var ip in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ip.Address.AddressFamily != AddressFamily.InterNetwork
                        || IPAddress.IsLoopback(ip.Address)) continue;
                    if (!isWifi && !isEther) continue;
                    var s2 = $"{ip.Address}:{DefaultPort}";
                    if (isWifi) return s2;
                    best ??= s2;
                }
            }
            return best;
        }
        catch { return null; }
    }

    // ---------------- BomState → 事件 ----------------

    /// <summary>一条扫码/记账结果（UI 线程调用）。附带完整清单快照，插件面板直接可用。</summary>
    public static void EmitScan(ScanOutcome o)
    {
        if (!Running) return;
        try
        {
            var line = o.Line;
            var prog = line is null ? null
                : BomState.Instance.Progress.FirstOrDefault(p => ReferenceEquals(p.Line, line));
            var evt = new Dictionary<string, object?>
            {
                ["seq"] = Interlocked.Increment(ref _seq),
                ["at"] = DateTimeOffset.Now.ToString("HH:mm:ss"),
                ["kind"] = o.Kind switch
                {
                    ScanKind.Hit => "hit",
                    ScanKind.LineDone => "linedone",
                    ScanKind.AllDone => "alldone",
                    ScanKind.Miss => "miss",
                    ScanKind.Duplicate => "duplicate",
                    _ => "parsefail",
                },
                ["lcsc"] = o.Tag?.Lcsc ?? line?.Lcsc ?? "",
                ["title"] = o.Title,
                ["detail"] = o.Detail,
                ["scanned"] = prog?.Scanned ?? o.Scanned,
                ["needed"] = prog is null ? o.Needed : BomState.Instance.NeededOf(prog),
                ["done"] = prog is not null && BomState.Instance.IsDone(prog),
                ["flagged"] = prog?.Flagged == true,
                ["designators"] = line?.Designators ?? [],
                ["orderNo"] = o.Tag?.OrderNo ?? "",
            };
            var evtJson = JsonSerializer.Serialize(evt, J);
            lock (_gate)
            {
                _events.Enqueue(JsonSerializer.Deserialize<JsonElement>(evtJson));
                if (_events.Count > EventCap) _events.Dequeue();
            }
            Publish($"{{\"type\":\"scan\",\"evt\":{evtJson},\"lines\":{CompactLinesJson()}}}");
        }
        catch (Exception e) { Console.WriteLine($"[LinkServer] EmitScan 失败: {e.Message}"); }
    }

    /// <summary>清单级变化（换 BOM/套数/清空/修正），状态变化线程调用。</summary>
    public static void EmitState()
    {
        if (!Running) return;
        RebuildState();
        Publish(_stateJson);
    }

    static void RebuildState()
    {
        try
        {
            var st = BomState.Instance;
            _stateJson = $"{{\"type\":\"state\",\"project\":{Json(st.Doc?.Project ?? "")},\"boards\":{st.Boards},"
                + $"\"totalLines\":{st.Progress.Count},\"linesDone\":{st.LinesDone},\"totalBags\":{st.TotalBags},"
                + $"\"seq\":{Interlocked.Read(ref _seq)},\"lines\":{CompactLinesJson()}}}";
        }
        catch (Exception e) { Console.WriteLine($"[LinkServer] RebuildState 失败: {e.Message}"); }
    }

    static string Json(string s) => JsonSerializer.Serialize(s);

    /// <summary>清单紧凑视图：插件焊接助手面板与高亮映射共用。</summary>
    static string CompactLinesJson()
    {
        var st = BomState.Instance;
        var sb = new StringBuilder("[");
        var first = true;
        foreach (var p in st.Progress)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append($"{{\"lcsc\":{Json(p.Line.Lcsc)},\"v\":{Json(p.Line.Value)},\"fp\":{Json(p.Line.Footprint)},"
                + $"\"n\":{st.NeededOf(p)},\"s\":{p.Scanned},\"b\":{p.Bags.Count},"
                + $"\"d\":{(st.IsDone(p) ? 1 : 0)},\"f\":{(p.Flagged ? 1 : 0)},"
                + $"\"des\":{JsonSerializer.Serialize(p.Line.Designators ?? [])}}}");
        }
        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>向所有 WebSocket 客户端广播一条已序列化消息。</summary>
    public static void Publish(string json)
    {
        List<WsClient> snapshot;
        lock (_gate) snapshot = [.. _clients];
        foreach (var c in snapshot) c.Send(json);
    }

#if ANDROID
    static Android.OS.PowerManager.WakeLock? _wakeLock;
    static Android.Net.Wifi.WifiManager.WifiLock? _wifiLock;

    /// <summary>
    /// 服务运行期间常持 CPU/Wi-Fi 唤醒锁（不依赖是否有客户端连接——连接一旦因休眠断开，
    /// 再想从外部连进来叫醒手机就晚了）；EDA 已连接时额外让屏幕常亮（备料时本来就要亮着）。
    /// 灭屏/切后台后系统会暂停应用网络/冻结进程，BOM 下发就会失败。
    /// </summary>
    static void UpdateSessionPower()
    {
        var locksOn = Running;
        var screenOn = Running && ClientCount > 0;
        try
        {
            var ctx = Android.App.Application.Context;
            if (locksOn)
            {
                _wakeLock ??= ((Android.OS.PowerManager)ctx.GetSystemService(Android.Content.Context.PowerService)!)
                    .NewWakeLock(Android.OS.WakeLockFlags.Partial, "bomsearch:link");
                if (!_wakeLock.IsHeld)
                    _wakeLock.Acquire();
                _wifiLock ??= ((Android.Net.Wifi.WifiManager?)ctx.GetSystemService(Android.Content.Context.WifiService))
                    ?.CreateWifiLock(Android.Net.WifiMode.FullHighPerf, "bomsearch:link");
                if (_wifiLock is { IsHeld: false })
                    _wifiLock.Acquire();
            }
            else
            {
                if (_wakeLock?.IsHeld == true)
                    _wakeLock.Release();
                if (_wifiLock?.IsHeld == true)
                    _wifiLock.Release();
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"[LinkServer] 电源锁失败: {e.Message}");
        }

        try
        {
            var window = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Window;
            if (window is not null)
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (screenOn)
                        window.AddFlags(Android.Views.WindowManagerFlags.KeepScreenOn);
                    else
                        window.ClearFlags(Android.Views.WindowManagerFlags.KeepScreenOn);
                });
            }
        }
        catch { /* 窗口不可用时忽略 */ }
    }
#else
    static void UpdateSessionPower() { }
#endif

    // ---------------- 连接处理 ----------------

    static async Task AcceptLoop()
    {
        var listener = _listener!;
        while (Running)
        {
            TcpClient tcp;
            try { tcp = await listener.AcceptTcpClientAsync(); }
            catch { break; }
            _ = Task.Run(() => HandleConnection(tcp));
        }
    }

    static async Task HandleConnection(TcpClient tcp)
    {
        try
        {
            tcp.NoDelay = true;
            var stream = tcp.GetStream();
            var head = await ReadHttpHead(stream);
            if (head is null) return;

            var (method, path, headers) = head.Value;
            if (headers.TryGetValue("upgrade", out var upgrade)
                && upgrade.Contains("websocket", StringComparison.OrdinalIgnoreCase))
            {
                await ServeWebSocket(stream, headers);
                return;
            }

            await ServeHttp(stream, method, path, headers);
        }
        catch { /* 单个连接异常直接断开 */ }
        finally
        {
            try { tcp.Close(); } catch { }
        }
    }

    static async Task<(string, string, Dictionary<string, string>)?> ReadHttpHead(NetworkStream s)
    {
        var buf = new byte[8192];
        var acc = new StringBuilder();
        while (acc.Length < 65536)
        {
            var n = await s.ReadAsync(buf.AsMemory(0, buf.Length));
            if (n == 0) return null;
            acc.Append(Encoding.UTF8.GetString(buf, 0, n));
            if (acc.ToString().Contains("\r\n\r\n")) break;
        }
        var text = acc.ToString();
        var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var head = (end < 0 ? text : text[..end]).Split("\r\n");
        if (head.Length == 0) return null;

        var parts = head[0].Split(' ');
        if (parts.Length < 2) return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in head[1..])
        {
            var i = line.IndexOf(':');
            if (i > 0) headers[line[..i].Trim()] = line[(i + 1)..].Trim();
        }
        // 已经读进来的 body 前缀（Content-Length 可能小于一次读到的量）
        var bodyPrefix = end < 0 ? "" : text[(end + 4)..];
        headers["__bodyPrefix"] = bodyPrefix;
        return (parts[0], parts[1], headers);
    }

    // ---------------- HTTP ----------------

    static async Task ServeHttp(NetworkStream s, string method, string path, Dictionary<string, string> headers)
    {
        try
        {
            if (method == "POST" && path.StartsWith("/bom", StringComparison.OrdinalIgnoreCase))
            {
                var body = await ReadBody(s, headers);
                var reply = await ImportBom(body);
                await WriteHttp(s, 200, reply);
                return;
            }
            if (method == "GET")
            {
                if (path.StartsWith("/info", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteHttp(s, 200, _stateJson);
                    return;
                }
                if (path.StartsWith("/events", StringComparison.OrdinalIgnoreCase))
                {
                    long since = 0;
                    var i = path.IndexOf("since=", StringComparison.OrdinalIgnoreCase);
                    if (i >= 0 && long.TryParse(path[(i + 6)..].Split('&')[0], out var v)) since = v;
                    string json;
                    lock (_gate)
                    {
                        var sb = new StringBuilder("{\"events\":[");
                        var first = true;
                        foreach (var e in _events)
                        {
                            var seq = e.TryGetProperty("seq", out var sv) && sv.ValueKind == JsonValueKind.Number ? sv.GetInt64() : 0;
                            if (seq <= since) continue;
                            if (!first) sb.Append(',');
                            first = false;
                            sb.Append(e.GetRawText());
                        }
                        sb.Append($"],\"seq\":{Interlocked.Read(ref _seq)}}}");
                        json = sb.ToString();
                    }
                    await WriteHttp(s, 200, json);
                    return;
                }
                if (path == "/" || path.StartsWith("/index", StringComparison.OrdinalIgnoreCase))
                {
                    var addr = LanAddress() ?? $"{DefaultPort}";
                    var html = "<!doctype html><meta charset=utf-8><body style=\"font-family:system-ui;padding:24px\">"
                        + "<h3>📱 BOM 找料助手 · 联动服务运行中</h3>"
                        + $"<p>本机地址：<code>{addr}</code></p>"
                        + $"<p>插件设置里填：<code>ws://{addr}</code></p></body>";
                    await WriteHttp(s, 200, html, "text/html; charset=utf-8");
                    return;
                }
            }
            await WriteHttp(s, 404, "{\"error\":\"not found\"}");
        }
        catch { try { await WriteHttp(s, 500, "{\"error\":\"internal\"}"); } catch { } }
    }

    static async Task<string> ReadBody(NetworkStream s, Dictionary<string, string> headers)
    {
        var prefix = Encoding.UTF8.GetBytes(headers.GetValueOrDefault("__bodyPrefix", ""));
        if (!headers.TryGetValue("content-length", out var lenText) || !int.TryParse(lenText, out var len))
            return Encoding.UTF8.GetString(prefix);
        len = Math.Min(len, 20 * 1024 * 1024);
        var body = new byte[Math.Max(0, len)];
        var have = Math.Min(prefix.Length, body.Length);
        Array.Copy(prefix, body, have);
        while (have < body.Length)
        {
            var n = await s.ReadAsync(body.AsMemory(have, body.Length - have));
            if (n == 0) break;
            have += n;
        }
        return Encoding.UTF8.GetString(body, 0, have);
    }

    static async Task WriteHttp(NetworkStream s, int code, string body, string contentType = "application/json; charset=utf-8")
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {code} {(code == 200 ? "OK" : "ERR")}\r\nContent-Type: {contentType}\r\n"
            + $"Content-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
        await s.WriteAsync(head);
        await s.WriteAsync(bytes);
        await s.FlushAsync();
    }

    /// <summary>导入插件下发的 BOM（HTTP POST /bom 与 WebSocket bom 消息共用）。</summary>
    static async Task<string> ImportBom(string body)
    {
        try
        {
            var push = JsonSerializer.Deserialize<BomDoc>(body, BomModels.JsonOpts);
            if (push is null || push.Lines is not { Count: > 0 })
                return "{\"ok\":false,\"error\":\"empty lines\"}";
            var lines = push.Lines
                .Where(l => !string.IsNullOrWhiteSpace(l.Lcsc))
                .Select(l =>
                {
                    l.Lcsc = l.Lcsc.Trim().ToUpperInvariant();
                    if (l.Qty < 1) l.Qty = Math.Max(1, l.Designators?.Count ?? 1);
                    return l;
                })
                .ToList();
            if (lines.Count == 0) return "{\"ok\":false,\"error\":\"no lcsc lines\"}";

            await MainThread.InvokeOnMainThreadAsync(() =>
                BomState.Instance.Load(new BomDoc { Project = push.Project, ExportedAt = push.ExportedAt, Lines = lines }, 1));

            EmitState();
            return $"{{\"ok\":true,\"lines\":{lines.Count}}}";
        }
        catch (Exception e)
        {
            return $"{{\"ok\":false,\"error\":{Json(e.Message)}}}";
        }
    }

    // ---------------- WebSocket ----------------

    static async Task ServeWebSocket(NetworkStream s, Dictionary<string, string> headers)
    {
        if (!headers.TryGetValue("sec-websocket-key", out var key))
            return;
        var accept = Convert.ToBase64String(
            SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var handshake = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
            + $"Sec-WebSocket-Accept: {accept}\r\n\r\n");
        await s.WriteAsync(handshake);
        await s.FlushAsync();

        var client = new WsClient(s);
        lock (_gate)
        {
            _clients.Add(client);
            ClientCount = _clients.Count;
        }
        UpdateSessionPower();
        StatusChanged?.Invoke();

        var inbox = new Dictionary<string, string[]>();
        try
        {
            client.Send(_stateJson); // 连上即发当前全量状态（大消息自动分片）
            var buffer = new byte[64 * 1024];
            var acc = new List<byte>();
            while (Running)
            {
                var n = await s.ReadAsync(buffer.AsMemory());
                if (n == 0) break;
                acc.AddRange(buffer[..n]);
                if (acc.Count > 1024 * 1024) break;

                // 逐帧解出完整消息
                while (TryTakeFrame(acc, out var payload, out var opcode, out var consumed))
                {
                    acc.RemoveRange(0, consumed);
                    if (opcode == 0x8) { client.EnqueueFrame(0x8, []); return; }      // close
                    if (opcode == 0x9) { client.EnqueueFrame(0xA, payload); continue; } // ping→pong
                    if (opcode == 0x1)
                    {
                        var msg = Encoding.UTF8.GetString(payload);
                        // 分片信封：按 mid 重组，收齐再处理
                        if (msg.StartsWith("{\"ck\":1,", StringComparison.Ordinal))
                        {
                            var full = TakeChunk(inbox, msg);
                            if (full is null) continue;
                            msg = full;
                        }
                        Console.WriteLine($"[LinkServer] <- ws {msg.Length} 字符 {MsgTag(msg)}");
                        var reply = HandleWsMessage(msg);
                        if (reply is { } r) client.Send(r);
                    }
                }
            }
        }
        catch { }
        finally
        {
            lock (_gate)
            {
                _clients.Remove(client);
                ClientCount = _clients.Count;
            }
            client.Close();
            UpdateSessionPower();
            StatusChanged?.Invoke();
        }
    }

    /// <summary>重组入站分片；未收齐返回 null，收齐返回完整消息。</summary>
    static string? TakeChunk(Dictionary<string, string[]> inbox, string msg)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(msg);
            var root = doc.RootElement;
            if (root.TryGetProperty("mid", out var midEl) && root.TryGetProperty("i", out var iEl)
                && root.TryGetProperty("n", out var nEl) && root.TryGetProperty("d", out var dEl))
            {
                var mid = midEl.GetString() ?? "";
                var i = iEl.GetInt32();
                var n = nEl.GetInt32();
                var parts = inbox.TryGetValue(mid, out var got) ? got : new string[n];
                if (parts.Length != n) parts = new string[n];
                parts[i] = dEl.GetString() ?? "";
                inbox[mid] = parts;
                if (parts.All(x => x is not null))
                {
                    inbox.Remove(mid);
                    return string.Concat(parts);
                }
                return null;
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"[LinkServer] 分片信封解析失败: {e.Message}");
        }
        return null;
    }

    /// <summary>消息摘要（诊断用）：类型/前 40 字符。</summary>
    static string MsgTag(string msg)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(msg);
            if (doc.RootElement.TryGetProperty("type", out var t))
                return $"type={t.GetString()}";
        }
        catch { }
        return msg.Length <= 40 ? msg : msg[..40];
    }

    static string? HandleWsMessage(string msg)
    {
        try
        {
            using var doc = JsonDocument.Parse(msg);
            if (!doc.RootElement.TryGetProperty("type", out var typeEl)) return null;
            switch (typeEl.GetString())
            {
                case "hello":
                case "getstate":
                    return _stateJson;
                case "bom":
                    return ImportBom(msg).GetAwaiter().GetResult();
                case "ping":
                    return "{\"type\":\"pong\"}";
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"[LinkServer] WS 消息处理失败: {e.Message}");
        }
        return null;
    }

    /// <summary>从累计字节中解出一个完整帧（客户端→服务器必须掩码）。</summary>
    static bool TryTakeFrame(List<byte> data, out byte[] payload, out byte opcode, out int consumed)
    {
        payload = [];
        opcode = 0;
        consumed = 0;
        if (data.Count < 2) return false;

        var b0 = data[0];
        var b1 = data[1];
        opcode = (byte)(b0 & 0x0F);
        var masked = (b1 & 0x80) != 0;
        var len = (long)(b1 & 0x7F);
        var idx = 2;

        if (len == 126)
        {
            if (data.Count < 4) return false;
            len = (data[2] << 8) | data[3];
            idx = 4;
        }
        else if (len == 127)
        {
            if (data.Count < 10) return false;
            len = 0;
            for (var i = 0; i < 8; i++) len = (len << 8) | data[2 + i];
            idx = 10;
        }
        var maskLen = masked ? 4 : 0;
        if (data.Count < idx + maskLen + len) return false;

        var mask = masked ? data.GetRange(idx, 4).ToArray() : [];
        var start = idx + maskLen;
        payload = new byte[len];
        for (long i = 0; i < len; i++)
        {
            var b = data[start + (int)i];
            if (masked) b ^= mask[i % 4];
            payload[i] = b;
        }
        consumed = (int)(start + len);
        return (b0 & 0x80) != 0; // 只返回 FIN=1 的完整帧；分片消息按不完整丢弃（本协议消息都很小）
    }

    /// <summary>单个 WebSocket 客户端：发送走异步队列（不阻塞 UI 线程），保序。</summary>
    sealed class WsClient(NetworkStream stream)
    {
        readonly object _sendGate = new();
        readonly Queue<byte[]> _outbox = new();
        public volatile bool Alive = true;

        static long _midSeq;

        public void Send(string json)
        {
            // 大消息分片：EDA 客户端的 WS 包装层收不了大消息，按 ChunkSize 拆成信封序列（保序）
            if (json.Length <= ChunkSize)
            {
                EnqueueFrame(0x1, Encoding.UTF8.GetBytes(json));
                return;
            }
            var mid = $"m{Interlocked.Increment(ref _midSeq)}";
            var n = (json.Length + ChunkSize - 1) / ChunkSize;
            Console.WriteLine($"[LinkServer] -> ws 分片 {json.Length} 字符 → {n} 片（{MsgTag(json)}）");
            for (var i = 0; i < n; i++)
            {
                var env = $"{{\"ck\":1,\"mid\":\"{mid}\",\"i\":{i},\"n\":{n},\"d\":{System.Text.Json.JsonSerializer.Serialize(json.Substring(i * ChunkSize, Math.Min(ChunkSize, json.Length - i * ChunkSize)))}}}";
                EnqueueFrame(0x1, Encoding.UTF8.GetBytes(env));
            }
        }

        public void EnqueueFrame(byte opcode, byte[] payload)
        {
            if (!Alive) return;
            var frame = EncodeFrame(opcode, payload);
            lock (_sendGate)
            {
                _outbox.Enqueue(frame);
                if (_outbox.Count == 1) // 之前空闲 → 启动一次排水循环
                    _ = Task.Run(DrainAsync);
            }
        }

        static byte[] EncodeFrame(byte opcode, byte[] payload)
        {
            var header = new List<byte> { (byte)(0x80 | opcode) };
            if (payload.Length < 126) header.Add((byte)payload.Length);
            else if (payload.Length <= ushort.MaxValue)
            {
                header.Add(126);
                header.Add((byte)(payload.Length >> 8));
                header.Add((byte)(payload.Length & 0xFF));
            }
            else
            {
                header.Add(127);
                var l = (long)payload.Length;
                for (var i = 7; i >= 0; i--) header.Add((byte)((l >> (8 * i)) & 0xFF));
            }
            var frame = new byte[header.Count + payload.Length];
            header.CopyTo(frame, 0);
            payload.CopyTo(frame, header.Count);
            return frame;
        }

        async Task DrainAsync()
        {
            while (Alive)
            {
                byte[]? frame;
                lock (_sendGate)
                {
                    if (_outbox.Count == 0) return;
                    frame = _outbox.Dequeue();
                }
                try
                {
                    await stream.WriteAsync(frame);
                    await stream.FlushAsync();
                }
                catch
                {
                    Alive = false;
                    return;
                }
            }
        }

        public void Close()
        {
            Alive = false;
            lock (_sendGate) _outbox.Clear();
            try { stream.Close(); } catch { }
        }
    }
}
