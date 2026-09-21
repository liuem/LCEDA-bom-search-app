using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;
using SkiaSharp;
using ZXing.Net.Maui;
using ZXing.Net.Maui.Controls;
using SkiaReader = ZXing.SkiaSharp.BarcodeReader;

namespace lceda_bom_search_AndroidApp;

public partial class ScannerPage : ContentPage
{
    DateTime _lastHandleUtc = DateTime.MinValue;
    DateTime _lastDecodeUtc = DateTime.MinValue;
    string? _lastRaw;
    bool _focusTimerOn;
    bool _busy;
    CameraBarcodeReaderView? _scanner;
    BagTag? _pendingBag;      // 修正条当前对应的袋子
    BagTag? _pendingXTag;     // 绑定条当前对应的 X 码

    public ScannerPage()
    {
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (DeviceInfo.Platform == DevicePlatform.WinUI)
        {
            CameraHint.Text = "电脑模拟模式：相机在手机上运行。\n在下方输入框粘贴码内容即可测试完整流程。";
            return;
        }

        // 相机控件必须等权限授予后再动态挂载：
        // 未授权状态下初始化会失败且不会自动重试（表现为首次进入黑屏）
        PermissionStatus status;
        try { status = await Permissions.CheckStatusAsync<Permissions.Camera>(); }
        catch { status = PermissionStatus.Denied; }
        if (status != PermissionStatus.Granted)
        {
            try { status = await Permissions.RequestAsync<Permissions.Camera>(); }
            catch { }
        }
        if (status != PermissionStatus.Granted)
        {
            CameraHint.Text = "未授予相机权限\n可用下方输入框手动输入码内容";
            return;
        }

        AttachScanner();

        // 对焦策略：CameraX 已显式开启连续自动对焦，扫得出码时不去打断它；
        // 只有持续约 5 秒扫不出任何码（多半是没对上焦）才补一次中心对焦。
        // 踢得太勤反而会反复打断连续对焦的收敛，形成“越踢越糊”的恶性循环。
        _focusTimerOn = true;
        Device.StartTimer(TimeSpan.FromSeconds(1), () =>
        {
            if (!_focusTimerOn) return false;
            if ((DateTime.UtcNow - _lastDecodeUtc).TotalSeconds > 5)
            {
                _lastDecodeUtc = DateTime.UtcNow;
                try { _scanner?.AutoFocus(); } catch { }
            }
            return _focusTimerOn;
        });
    }

    protected override void OnDisappearing()
    {
        _focusTimerOn = false;
        base.OnDisappearing();
    }

    void AttachScanner()
    {
        if (_scanner is not null) return;

        // 只认二维码：一维码是袋子上的物流码（X 开头商品详情号），对找料没用，不响应；
        // 收窄格式同时显著加快每帧解码。
        // 预览/分析分辨率与连续对焦由 Platforms/Android/FastScanHandler 负责
        // （它覆盖了 ZXing.Net.Maui 默认处理器的 640×480 + 无连续对焦）
        _scanner = new CameraBarcodeReaderView
        {
            Options = new BarcodeReaderOptions
            {
                Formats = BarcodeFormat.QrCode,
                AutoRotate = true,
                Multiple = false,
            },
        };
        _scanner.BarcodesDetected += OnBarcodesDetected;
        CameraHost.Content = _scanner;
        CameraHint.IsVisible = false;
    }

    void OnBarcodesDetected(object? sender, BarcodeDetectionEventArgs e)
    {
        var raw = e.Results?.FirstOrDefault()?.Value;
        if (string.IsNullOrWhiteSpace(raw)) return;
        _lastDecodeUtc = DateTime.UtcNow;
        HandleRaw(raw);
    }

    void OnTorchToggled(object? sender, EventArgs e)
    {
        if (_scanner is not null)
            _scanner.IsTorchOn = !_scanner.IsTorchOn;
    }

    async void OnPickImage(object? sender, EventArgs e)
    {
        try
        {
            var photo = await MediaPicker.Default.PickPhotoAsync(new MediaPickerOptions { Title = "选择袋子照片" });
            if (photo is null) return;

            byte[] bytes;
            using (var s = await photo.OpenReadAsync())
            using (var ms = new MemoryStream())
            {
                await s.CopyToAsync(ms);
                bytes = ms.ToArray();
            }

            // 大图里二维码占比较小，多尺度解码显著提高识别率（实测 12MP 照片需缩到长边 ~1600）
            var text = await Task.Run(() => DecodePhotoBest(bytes));

            if (text is null)
            {
                StatusLabel.Text = "✗ 照片里没识别到条码";
                StatusLabel.TextColor = Microsoft.Maui.Graphics.Color.FromArgb("#FFA726");
                DetailLabel.Text = "试着拍近一点、避免反光，标签上的码要完整清晰";
                return;
            }
            HandleRaw(text);
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "✗ 图片识别失败";
            StatusLabel.TextColor = Microsoft.Maui.Graphics.Color.FromArgb("#FFA726");
            DetailLabel.Text = ex.Message;
        }
    }

    static readonly ZXing.BarcodeFormat[] PhotoFormats =
    [
        ZXing.BarcodeFormat.QR_CODE,
    ];

    static string? DecodePhotoBest(byte[] bytes)
    {
        using var origin = SKBitmap.Decode(bytes);
        if (origin is null) return null;

        foreach (var maxSide in new[] { 1600, 1200, 800, 2200 })
        {
            SKBitmap? scaled = null;
            var bmp = origin;
            var longSide = Math.Max(origin.Width, origin.Height);
            if (longSide > maxSide)
            {
                var scale = (float)maxSide / longSide;
                scaled = origin.Resize(
                    new SKImageInfo((int)(origin.Width * scale), (int)(origin.Height * scale)),
                    SKSamplingOptions.Default);
                bmp = scaled;
            }

            try
            {
                var reader = new SkiaReader
                {
                    AutoRotate = true,
                    Options = new ZXing.Common.DecodingOptions
                    {
                        TryHarder = true,
                        TryInverted = true,
                        PossibleFormats = PhotoFormats.ToList(),
                    },
                };
                var results = reader.DecodeMultiple(bmp) ?? [];
                // 优先信息最全的二维码（含 pc: 字段），其次最长的码
                var best = results
                    .Where(r => r is not null)
                    .OrderByDescending(r => (r.Text?.Contains("pc:") ?? false) ? 1 : 0)
                    .ThenByDescending(r => r.Text?.Length ?? 0)
                    .FirstOrDefault();
                if (best is not null) return best.Text;
            }
            catch { }
            finally { scaled?.Dispose(); }
        }
        return null;
    }

    void OnManualEntered(object? sender, EventArgs e)
    {
        var raw = ManualEntry.Text;
        if (string.IsNullOrWhiteSpace(raw)) return;
        ManualEntry.Text = "";
        HandleRaw(raw);
    }

    /// <summary>识别入口：可能在相机后台线程被调用——先去抖，再切换到 UI 线程执行。</summary>
    void HandleRaw(string raw)
    {
        if (_busy) return;
        var now = DateTime.UtcNow;
        if (_lastRaw == raw && (now - _lastHandleUtc).TotalMilliseconds < 2500) return;
        _lastRaw = raw;
        _lastHandleUtc = now;
        _busy = true;

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                await HandleRawUiAsync(raw);
            }
            catch (Exception ex)
            {
                // 兜底：任何未预期异常都不允许闪退
                try
                {
                    StatusLabel.Text = "✗ 处理出错";
                    StatusLabel.TextColor = Microsoft.Maui.Graphics.Color.FromArgb("#FF5252");
                    DetailLabel.Text = ex.Message;
                }
                catch { }
            }
            finally
            {
                _busy = false;
            }
        });
    }

    async Task HandleRawUiAsync(string raw)
    {
        var tag = TagParser.Parse(raw);
        ScanOutcome outcome;

        if (tag is null)
        {
            outcome = BomState.Instance.EvaluateRaw(raw);
            HidePanels();
            PlayOutcome(outcome);
            ShowOutcome(outcome, raw);
            return;
        }

        tag = BomState.Instance.ResolveTag(tag);

        // 一维物流码（X+pdi）还没绑定过 C 编号：非阻塞，显示绑定条等用户输入
        if (string.IsNullOrEmpty(tag.Lcsc) && tag.Pdi is not null)
        {
            _pendingXTag = tag;
            _pendingBag = null;
            BindContextLabel.Text = $"X{tag.Pdi} 是商品详情号，需要 C 编号定位物料（袋子标签上印着）。\n绑定后这袋按“数量未知”记账，可在上方修正条填实际数量。";
            BindLcscEntry.Text = "";
            BindPanel.IsVisible = true;
            CorrectionPanel.IsVisible = false;
            AlertSound.Play(SoundKind.ParseFail);
            StatusLabel.Text = "? 一维物流码待绑定";
            StatusLabel.TextColor = Microsoft.Maui.Graphics.Color.FromArgb("#FFA726");
            DetailLabel.Text = $"X{tag.Pdi}——在下方输入 C 编号";
            RawLabel.Text = raw;
            return;
        }

        var pre = BomState.Instance.Precheck(tag);
        if (pre is not null)
        {
            // 不在 BOM / 重复袋：直接出结果（响声 + 显示）
            HidePanels();
            PlayOutcome(pre);
            ShowOutcome(pre, raw);
            return;
        }

        // 命中：先响声，立即默认记账（二维码按袋标称、一维码数量未知），不弹窗阻塞；
        // 需要修正时用底部修正条（电阻电容大概率足够，多数时候不用管它）
        int? defaultQty = tag.FromQr ? tag.Qty : null;
        outcome = BomState.Instance.Apply(tag, defaultQty, flagged: false);

        PlayOutcome(outcome);
        ShowOutcome(outcome, raw);

        _pendingBag = tag;
        _pendingXTag = null;
        var nominalText = tag.FromQr && tag.Qty is { } n
            ? $"袋标称 {n} 个，已按标称计入；数量不对再改："
            : "条码无数量信息，已记为“数量未知”；知道实际数量可补填：";
        CorrContextLabel.Text = $"{outcome.Line?.Display ?? tag.Lcsc} · {nominalText}";
        CorrQtyEntry.Text = tag.FromQr && tag.Qty is { } nn ? nn.ToString() : "";
        CorrectionPanel.IsVisible = true;
        BindPanel.IsVisible = false;
    }

    void OnAdjustQty(object? sender, EventArgs e)
    {
        if (_pendingBag is null) return;
        int? n = int.TryParse(CorrQtyEntry.Text, out var v) && v >= 0 ? v : null;
        var r = BomState.Instance.AdjustBag(_pendingBag, n, flagged: false);
        if (r is { } adj)
        {
            AlertSound.Play(adj.Done ? SoundKind.LineDone : SoundKind.Hit);
            StatusLabel.Text = n is null
                ? $"✓ {adj.Line.Line.Display} 已改为数量未知"
                : $"✓ {adj.Line.Line.Display} 已改：这袋计 {n}，累计 {adj.Scanned}/{adj.Needed}{(adj.Done ? " ✓" : "")}";
            StatusLabel.TextColor = Microsoft.Maui.Graphics.Color.FromArgb("#66BB6A");
            DetailLabel.Text = "数量已修正";
        }
        CorrectionPanel.IsVisible = false;
    }

    void OnFlagMissing(object? sender, EventArgs e)
    {
        if (_pendingBag is null) return;
        var r = BomState.Instance.AdjustBag(_pendingBag, _pendingBag.Counted, flagged: true);
        if (r is { } adj)
        {
            AlertSound.Play(SoundKind.ParseFail);
            Vibration.Default.Vibrate(300);
            StatusLabel.Text = $"⚑ {adj.Line.Line.Display} 已标缺量";
            StatusLabel.TextColor = Microsoft.Maui.Graphics.Color.FromArgb("#EF5350");
            DetailLabel.Text = "清单里会用红色标记提醒补料";
        }
        CorrectionPanel.IsVisible = false;
    }

    async void OnBindX(object? sender, EventArgs e)
    {
        if (_pendingXTag?.Pdi is not { } pdi) return;
        var c = BindLcscEntry.Text.Trim().ToUpperInvariant();
        if (!c.StartsWith('C') || c.Length < 2)
        {
            StatusLabel.Text = "✗ C 编号格式不对";
            StatusLabel.TextColor = Microsoft.Maui.Graphics.Color.FromArgb("#FF5252");
            DetailLabel.Text = "应形如 C3151749（见袋子标签）";
            return;
        }

        BomState.Instance.LearnPdi(pdi, c);
        var tag = BomState.Instance.ResolveTag(_pendingXTag);
        BindPanel.IsVisible = false;

        if (string.IsNullOrEmpty(tag.Lcsc))
        {
            StatusLabel.Text = "✗ 绑定失败";
            return;
        }

        // 绑定完成：按数量未知记账，弹出修正条让用户按需补填
        var pre = BomState.Instance.Precheck(tag);
        if (pre is not null)
        {
            PlayOutcome(pre);
            ShowOutcome(pre, tag.Raw);
            return;
        }
        var outcome = BomState.Instance.Apply(tag, null, flagged: false);
        PlayOutcome(outcome);
        ShowOutcome(outcome, tag.Raw);
        _pendingBag = tag;
        CorrContextLabel.Text = $"{outcome.Line?.Display ?? tag.Lcsc} · 已绑定并按“数量未知”记账，可补填实际数量：";
        CorrQtyEntry.Text = "";
        CorrectionPanel.IsVisible = true;
    }

    void HidePanels()
    {
        CorrectionPanel.IsVisible = false;
        BindPanel.IsVisible = false;
        _pendingBag = null;
        _pendingXTag = null;
    }

    void PlayOutcome(ScanOutcome outcome)
    {
        AlertSound.Play(outcome.Kind switch
        {
            ScanKind.Hit => SoundKind.Hit,
            ScanKind.LineDone => SoundKind.LineDone,
            ScanKind.AllDone => SoundKind.AllDone,
            ScanKind.Miss => SoundKind.Miss,
            ScanKind.Duplicate => SoundKind.Duplicate,
            _ => SoundKind.ParseFail,
        });

        // 振动模式与音效对应：命中/找齐短振，全部找齐长振，
        // 不需要=单中振，重复袋=两短振（声音之外的第二反馈通道）
        switch (outcome.Kind)
        {
            case ScanKind.AllDone:
                Vibration.Default.Vibrate(400);
                break;
            case ScanKind.Hit:
            case ScanKind.LineDone:
                Vibration.Default.Vibrate(160);
                break;
            case ScanKind.Miss:
                Vibration.Default.Vibrate(220);
                break;
            case ScanKind.Duplicate:
                _ = VibratePatternAsync();
                break;
        }
    }

    static async Task VibratePatternAsync()
    {
        Vibration.Default.Vibrate(80);
        await Task.Delay(160);
        Vibration.Default.Vibrate(80);
    }

    void ShowOutcome(ScanOutcome outcome, string raw)
    {
        StatusLabel.Text = outcome.Title;
        StatusLabel.TextColor = outcome.UiColor;
        DetailLabel.Text = outcome.Detail;
        RawLabel.Text = raw.Length > 160 ? raw[..160] + "…" : raw;
    }
}
