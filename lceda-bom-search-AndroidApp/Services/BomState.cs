using Microsoft.Maui.Graphics;
using Microsoft.Maui.Storage;

namespace lceda_bom_search_AndroidApp;

public enum ScanKind
{
    Hit,        // 命中且未满
    LineDone,   // 该物料刚好找齐
    AllDone,    // 全部物料找齐
    Miss,       // 不在此 BOM
    Duplicate,  // 同一袋重复扫
    ParseFail,  // 无法识别
}

public sealed class ScanOutcome
{
    public ScanKind Kind { get; init; }
    public BomLine? Line { get; init; }
    public BagTag? Tag { get; init; }
    public int Scanned { get; init; }
    public int Needed { get; init; }
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public Color UiColor { get; init; } = Colors.White;
}

public sealed class LineProgress
{
    public required BomLine Line { get; init; }
    public int Scanned { get; set; }          // 有数量来源的累计（二维码/用户确认）
    public List<BagTag> Bags { get; set; } = []; // 扫过的袋子
    public bool Flagged { get; set; }         // 用户标注“缺量/数量存疑”
}

/// <summary>持久化存储结构（BOM + 套数 + 扫描进度）。</summary>
public sealed class PersistedState
{
    public string Project { get; set; } = "";
    public string ExportedAt { get; set; } = "";
    public int Boards { get; set; } = 1;
    public List<PersistedLine> Lines { get; set; } = [];
}

public sealed class PersistedLine
{
    public BomLine Line { get; set; } = new();
    public int Scanned { get; set; }
    public List<BagTag> Bags { get; set; } = [];
    public bool Flagged { get; set; }
}

/// <summary>全局备料状态单例：BOM 加载、套数、扫码判定、进度汇总、持久化。</summary>
public sealed class BomState
{
    const string StoreKey = "bom_state_v1";

    public static readonly BomState Instance = new();
    private BomState() { }

    public BomDoc? Doc { get; private set; }
    public List<LineProgress> Progress { get; private set; } = [];
    public int Boards { get; private set; } = 1;

    public int TotalBags => Progress.Sum(p => p.Bags.Count);
    public int LinesDone => Progress.Count(p => IsDone(p));
    public bool AllDone => Progress.Count > 0 && Progress.All(IsDone);

    public int NeededOf(LineProgress p) => Math.Max(1, p.Line.Qty) * Boards;

    public bool IsDone(LineProgress p)
        => p.Line.Qty > 0 && p.Scanned >= p.Line.Qty * Boards;

    /// <summary>状态变化通知（可能在非 UI 线程触发）。</summary>
    public event Action? Changed;

    public void Load(BomDoc doc, int boards = 1)
    {
        Doc = doc;
        Boards = Math.Max(1, boards);
        Progress = doc.Lines
            .Where(l => !string.IsNullOrWhiteSpace(l.Lcsc))
            .Select(l => new LineProgress { Line = l })
            .ToList();
        Save();
        Changed?.Invoke();
        LinkServer.EmitState();
    }

    public void SetBoards(int boards)
    {
        if (boards < 1) boards = 1;
        if (boards == Boards) return;
        Boards = boards;
        Save();
        Changed?.Invoke();
        LinkServer.EmitState();
    }

    public void ResetScans()
    {
        if (Doc is null) return;
        foreach (var p in Progress)
        {
            p.Scanned = 0;
            p.Bags.Clear();
        }
        Save();
        Changed?.Invoke();
        LinkServer.EmitState();
    }

    public ScanOutcome EvaluateRaw(string raw)
    {
        var tag = TagParser.Parse(raw);
        if (tag is null)
        {
            return Record(new ScanOutcome
            {
                Kind = ScanKind.ParseFail,
                Tag = new BagTag { Lcsc = "", Raw = raw },
                Title = "✗ 无法识别",
                Detail = "这不是嘉立创物料码（既不是 C 编号，也不含 pc: 字段）",
                UiColor = Color.FromArgb("#B0B0B0"),
            });
        }
        throw new InvalidOperationException("内部错误：EvaluateRaw 收到可解析的码");
    }

    /// <summary>Lcsc 为空的一维物流码（X+pdi）：用已学映射补全 C 编号。</summary>
    public BagTag ResolveTag(BagTag tag)
    {
        if (string.IsNullOrEmpty(tag.Lcsc) && !string.IsNullOrWhiteSpace(tag.Pdi)
            && ResolvePdi(tag.Pdi) is { } mapped)
        {
            return new BagTag
            {
                Lcsc = mapped,
                PartNo = tag.PartNo,
                Qty = tag.Qty,
                OrderNo = tag.OrderNo,
                Pdi = tag.Pdi,
                Raw = tag.Raw,
                FromQr = tag.FromQr,
            };
        }
        return tag;
    }

    /// <summary>命中预检：返回 Miss（不在 BOM）/ Duplicate（同袋重复）的结果；需要确认的命中返回 null。</summary>
    public ScanOutcome? Precheck(BagTag tag)
    {
        tag = ResolveTag(tag);
        if (string.IsNullOrEmpty(tag.Lcsc)) return null; // 无法定位物料，交给上层处理绑定

        var line = Progress.FirstOrDefault(p =>
            string.Equals(p.Line.Lcsc, tag.Lcsc, StringComparison.OrdinalIgnoreCase));

        if (line is null)
        {
            var pm = string.IsNullOrWhiteSpace(tag.PartNo) ? "" : $"（{tag.PartNo}）";
            return Record(new ScanOutcome
            {
                Kind = ScanKind.Miss,
                Tag = tag,
                Title = $"✗ 当前板不需要 {tag.Lcsc}{pm}",
                Detail = "不在本次 BOM 里，放回去",
                UiColor = Color.FromArgb("#FF5252"),
            });
        }

        // 同一订单的同一袋：重复扫，不重复计数
        if (tag.OrderNo is { } orderNo && line.Bags.Any(b => b.OrderNo == orderNo))
        {
            return Record(new ScanOutcome
            {
                Kind = ScanKind.Duplicate,
                Line = line.Line,
                Tag = tag,
                Title = $"↺ {line.Line.Display} 这袋已经扫过了",
                Detail = $"订单 {orderNo}，未重复计数",
                UiColor = Color.FromArgb("#FFA726"),
            });
        }

        return null;
    }

    /// <summary>用户确认后记账：qty 为这袋计入的数量（null = 仅标记找到不计数），flagged = 标注缺量/存疑。</summary>
    public ScanOutcome Apply(BagTag tag, int? qty, bool flagged)
    {
        tag = ResolveTag(tag);
        LearnFromTag(tag);
        var line = Progress.FirstOrDefault(p =>
            string.Equals(p.Line.Lcsc, tag.Lcsc, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("物料已不在清单中");

        int before = line.Scanned;
        line.Bags.Add(tag);
        if (qty is { } q)
            line.Scanned += q;
        tag.Counted = qty;
        if (flagged)
            line.Flagged = true;

        var need = line.Line.Qty * Boards;
        ScanOutcome outcome;

        if (flagged)
        {
            outcome = new ScanOutcome
            {
                Kind = ScanKind.Hit,
                Line = line.Line,
                Tag = tag,
                Scanned = line.Scanned,
                Needed = need,
                Title = $"⚑ {line.Line.Display} 已标缺量",
                Detail = qty is null
                    ? "清单里会用红色标记提醒补料"
                    : $"按 {qty} 个计入并标缺（累计 {line.Scanned}/{need}），清单里会提醒补料",
                UiColor = Color.FromArgb("#EF5350"),
            };
        }
        else if (qty is null)
        {
            outcome = new ScanOutcome
            {
                Kind = ScanKind.Hit,
                Line = line.Line,
                Tag = tag,
                Scanned = line.Scanned,
                Needed = need,
                Title = $"✓ {line.Line.Display} 找到了",
                Detail = "数量未知——可在修正条填写这袋的实际数量",
                UiColor = Color.FromArgb("#42A5F5"),
            };
        }
        else if (AllDone)
        {
            outcome = new ScanOutcome
            {
                Kind = ScanKind.AllDone,
                Line = line.Line,
                Tag = tag,
                Scanned = line.Scanned,
                Needed = need,
                Title = "🎉 全部物料已找齐！",
                Detail = $"{line.Line.Display} 也齐了，可以开始焊接",
                UiColor = Color.FromArgb("#FFC107"),
            };
        }
        else if (line.Scanned >= need && before < need)
        {
            outcome = new ScanOutcome
            {
                Kind = ScanKind.LineDone,
                Line = line.Line,
                Tag = tag,
                Scanned = line.Scanned,
                Needed = need,
                Title = $"✓ {line.Line.Display} 找齐了（需 {need} 个）",
                Detail = DesignatorsSummary(line.Line),
                UiColor = Color.FromArgb("#66BB6A"),
            };
        }
        else if (line.Scanned >= need)
        {
            outcome = new ScanOutcome
            {
                Kind = ScanKind.Hit,
                Line = line.Line,
                Tag = tag,
                Scanned = line.Scanned,
                Needed = need,
                Title = $"✓ {line.Line.Display}（已超量：累计 {line.Scanned}，需要 {need}）",
                Detail = "多余的下次备料还能用",
                UiColor = Color.FromArgb("#66BB6A"),
            };
        }
        else
        {
            outcome = new ScanOutcome
            {
                Kind = ScanKind.Hit,
                Line = line.Line,
                Tag = tag,
                Scanned = line.Scanned,
                Needed = need,
                Title = $"✓ {line.Line.Display} 命中！还差 {need - line.Scanned} 个",
                Detail = DesignatorsSummary(line.Line),
                UiColor = Color.FromArgb("#66BB6A"),
            };
        }

        return Record(outcome);
    }

    /// <summary>非阻塞修正：调整某袋实际计入的数量（null = 数量未知），并可补标缺量。返回 null 表示袋子不存在。</summary>
    public (LineProgress Line, int Scanned, int Needed, bool Done)? AdjustBag(BagTag bag, int? newQty, bool flagged)
    {
        var line = Progress.FirstOrDefault(p => p.Bags.Contains(bag));
        if (line is null) return null;

        line.Scanned += (newQty ?? 0) - (bag.Counted ?? 0);
        bag.Counted = newQty;
        if (flagged)
            line.Flagged = true;

        Save();
        Changed?.Invoke();
        LinkServer.EmitState();
        return (line, line.Scanned, line.Line.Qty * Boards, IsDone(line));
    }

    /// <summary>记录一条提示型结果（不改库存状态）。</summary>
    public ScanOutcome RecordPrompt(ScanOutcome o) => Record(o);

    static string DesignatorsSummary(BomLine line)
    {
        if (line.Designators is not { Count: > 0 }) return "";
        var shown = string.Join(" ", line.Designators.Take(6));
        return line.Designators.Count > 6
            ? $"{shown} …（共 {line.Designators.Count} 个位号）"
            : $"{shown}";
    }

    ScanOutcome Record(ScanOutcome o)
    {
        Save();
        Changed?.Invoke();
        LinkServer.EmitScan(o);
        return o;
    }

    // ---------- pdi → C 编号映射（跨 BOM 持久化：一维物流码 X+pdi 的物料定位） ----------

    const string PdiMapKey = "pdi_map_v1";
    static Dictionary<string, string> LoadPdiMap()
    {
        try
        {
            var json = Preferences.Default.Get(PdiMapKey, null as string);
            return string.IsNullOrWhiteSpace(json)
                ? []
                : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json, BomModels.JsonOpts) ?? [];
        }
        catch { return []; }
    }

    static void SavePdiMap(Dictionary<string, string> map)
    {
        try { Preferences.Default.Set(PdiMapKey, System.Text.Json.JsonSerializer.Serialize(map)); }
        catch { }
    }

    /// <summary>用一维码的 pdi 查已知的 C 编号（扫码二维码时自动学习）。</summary>
    public string? ResolvePdi(string? pdi)
    {
        if (string.IsNullOrWhiteSpace(pdi)) return null;
        return LoadPdiMap().TryGetValue(pdi, out var lcsc) ? lcsc : null;
    }

    /// <summary>绑定 pdi → C 编号（扫二维码自动学，或用户对 X 码手动输入）。</summary>
    public void LearnPdi(string? pdi, string lcsc)
    {
        if (string.IsNullOrWhiteSpace(pdi) || string.IsNullOrWhiteSpace(lcsc)) return;
        var map = LoadPdiMap();
        if (map.TryGetValue(pdi, out var old) && old == lcsc) return;
        map[pdi] = lcsc;
        SavePdiMap(map);
    }

    /// <summary>二维码同时带 pc 与 pdi 时自动学习映射。</summary>
    static void LearnFromTag(BagTag tag)
    {
        if (tag.FromQr && !string.IsNullOrWhiteSpace(tag.Pdi) && tag.Lcsc.StartsWith('C'))
            Instance.LearnPdi(tag.Pdi, tag.Lcsc);
    }

    // ---------- 持久化 ----------

    public void Save()
    {
        if (Doc is null) return;
        try
        {
            var state = new PersistedState
            {
                Project = Doc.Project,
                ExportedAt = Doc.ExportedAt,
                Boards = Boards,
                Lines = Progress.Select(p => new PersistedLine
                {
                    Line = p.Line,
                    Scanned = p.Scanned,
                    Bags = p.Bags,
                    Flagged = p.Flagged,
                }).ToList(),
            };
            Preferences.Default.Set(StoreKey, System.Text.Json.JsonSerializer.Serialize(state));
        }
        catch { /* 存不进去也不影响当次使用 */ }
    }

    public bool TryRestore()
    {
        try
        {
            var json = Preferences.Default.Get(StoreKey, null as string);
            if (string.IsNullOrWhiteSpace(json)) return false;

            var state = System.Text.Json.JsonSerializer.Deserialize<PersistedState>(json, BomModels.JsonOpts);
            if (state is null || state.Lines is not { Count: > 0 }) return false;

            Doc = new BomDoc
            {
                Project = state.Project,
                ExportedAt = state.ExportedAt,
                Lines = state.Lines.Select(l => l.Line).ToList(),
            };
            Boards = Math.Max(1, state.Boards);
            Progress = state.Lines
                .Where(l => !string.IsNullOrWhiteSpace(l.Line.Lcsc))
                .Select(l => new LineProgress
                {
                    Line = l.Line,
                    Scanned = l.Scanned,
                    Bags = l.Bags,
                    Flagged = l.Flagged,
                })
                .ToList();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
