using System.Text.Json;
using Microsoft.Maui.Storage;

namespace lceda_bom_search_AndroidApp;

public sealed class LineVM
{
    public string Lcsc { get; init; } = "";
    public string Sub { get; init; } = "";
    public string Badge { get; init; } = "";
    public Color BadgeColor { get; init; } = Colors.Transparent;
    public string Progress { get; init; } = "";
    public Color ProgressColor { get; init; } = Colors.Gray;
}

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();
        BomState.Instance.Changed += () => MainThread.BeginInvokeOnMainThread(Refresh);
        LinkServer.StatusChanged += () => MainThread.BeginInvokeOnMainThread(RefreshLink);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Refresh();
        RefreshLink();
    }

    void RefreshLink()
    {
        if (!LinkServer.Running)
        {
            LinkLabel.Text = "🔗 联动服务未启动";
            return;
        }
        var addr = LinkServer.LanAddress();
        var link = LinkServer.ClientCount > 0 ? "插件已连接" : "等待 EDA 插件连接";
        LinkLabel.Text = addr is null
            ? $"🔗 联动服务运行中（未找到局域网地址）· {link}"
            : $"🔗 {addr} · {link}";
    }

    async void OnLinkTapped(object? sender, TappedEventArgs e)
    {
        var addr = LinkServer.LanAddress();
        if (addr is null)
        {
            await DisplayAlertAsync("联动服务", "未找到局域网地址：请确认手机已连接 Wi-Fi。", "好");
            return;
        }
        var url = $"ws://{addr}";
        try { await Clipboard.Default.SetTextAsync(url); } catch { }
        await DisplayAlertAsync("EDA 插件连接地址（已复制）",
            $"在立创 EDA 的「焊接助手联动 → 设置」里填入：\n\n{url}\n\n手机与电脑需在同一局域网。", "好");
    }

    void Refresh()
    {
        var st = BomState.Instance;
        var hasDoc = st.Doc is not null;

        EmptyView.IsVisible = !hasDoc;
        LinesView.IsVisible = hasDoc;
        ScanBtn.IsVisible = hasDoc;
        ToolbarView.IsVisible = hasDoc;
        BoardsRow.IsVisible = hasDoc;

        if (!hasDoc) { RefreshFoundFiles(); return; }

        ProjectLabel.Text = st.Doc!.Project;
        BoardsLabel.Text = st.Boards.ToString();
        var flagged = st.Progress.Count(p => p.Flagged);
        var flagInfo = flagged > 0 ? $" · ⚑缺量 {flagged}" : "";
        SummaryLabel.Text = st.AllDone
            ? $"🎉 全部找齐！{st.LinesDone}/{st.Progress.Count} 种物料 · 已扫 {st.TotalBags} 袋{flagInfo}"
            : $"物料 {st.Progress.Count} 种 · 找齐 {st.LinesDone} · 已扫 {st.TotalBags} 袋 · 每板 {st.Boards} 套{flagInfo}";

        LinesView.ItemsSource = st.Progress.Select(p => BuildVM(p, st)).ToList();
    }

    static IBomFiles BomFiles =>
#if ANDROID
        new AndroidBomFiles();
#elif WINDOWS
        new WindowsBomFiles();
#else
        new NullBomFiles();
#endif

    /// <summary>空状态时列出应用目录里的候选 BOM 文件（一键导入，绕开系统文件选择器）。</summary>
    void RefreshFoundFiles()
    {
        FoundFilesView.Clear();
        var files = BomFiles.ListBomFiles();
        if (files.Count == 0) { FoundFilesView.IsVisible = false; return; }

        FoundFilesView.IsVisible = true;
        foreach (var f in files.Take(5))
        {
            var name = Path.GetFileName(f);
            var btn = new Button { Text = $"📄 {name}", FontSize = 13, Padding = new Thickness(10, 8) };
            var path = f;
            btn.Clicked += async (_, _) => await ImportFromPathAsync(path);
            FoundFilesView.Add(btn);
        }
    }

    async Task ImportFromPathAsync(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var (doc, report) = await ParseBomStreamAsync(stream, Path.GetFileName(path));
            await FinishImportAsync(doc, report, Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("导入失败", ex.Message, "好");
        }
    }

    static LineVM BuildVM(LineProgress p, BomState st)
    {
        var line = p.Line;
        var sub = line.Footprint.Length > 0 ? $"{line.Value} · {line.Footprint}" : line.Value;
        if (line.Designators is { Count: > 0 })
            sub += $" · {line.Designators.Count} 位号";

        var need = st.NeededOf(p);
        var done = st.IsDone(p);

        string badge = "", progress;
        Color badgeColor = Colors.Transparent, progressColor;

        if (p.Flagged)
        {
            badge = "⚑ 缺量";
            badgeColor = Color.FromArgb("#EF5350");
        }
        else if (done && p.Scanned > need)
        {
            badge = "超额";
            badgeColor = Color.FromArgb("#FFA726");
        }
        else if (done)
        {
            badge = "找齐";
            badgeColor = Color.FromArgb("#66BB6A");
        }

        if (done && !p.Flagged)
        {
            progress = $"{p.Scanned}/{need} ✓";
            progressColor = Color.FromArgb("#66BB6A");
        }
        else if (p.Scanned > 0)
        {
            progress = $"{p.Scanned}/{need}";
            progressColor = p.Flagged ? Color.FromArgb("#EF5350") : Color.FromArgb("#FFA726");
        }
        else if (p.Bags.Count > 0)
        {
            badge = "已找到";
            badgeColor = Color.FromArgb("#42A5F5");
            progress = "数量未知";
            progressColor = Color.FromArgb("#42A5F5");
        }
        else
        {
            progress = $"0/{need}";
            progressColor = Colors.Gray;
        }

        return new LineVM
        {
            Lcsc = line.Lcsc,
            Sub = sub,
            Badge = badge,
            BadgeColor = badgeColor,
            Progress = progress,
            ProgressColor = progressColor,
        };
    }

    async void OnImportBom(object? sender, EventArgs e)
    {
        try
        {
            // 不限扩展名：手机文件选择器对 .csv/.xlsx 的 MIME 识别不稳定
            var res = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "选择 BOM 文件（.csv / .xlsx / .json）" });
            if (res is null) return;

            using var stream = await res.OpenReadAsync();
            var (doc, report) = await ParseBomStreamAsync(stream, res.FileName);
            await FinishImportAsync(doc, report, res.FileName);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("导入失败", ex.Message, "好");
        }
    }

    static async Task<(BomDoc doc, string report)> ParseBomStreamAsync(Stream stream, string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();

        if (ext == ".json")
        {
            var doc = await JsonSerializer.DeserializeAsync<BomDoc>(stream, BomModels.JsonOpts)
                      ?? throw new InvalidDataException("JSON 解析为空");
            if (doc.Lines is not { Count: > 0 })
                throw new InvalidDataException("文件里没有物料行（lines 为空）");
            return (doc, $"JSON BOM：{doc.Lines.Count} 行");
        }

        // 立创 EDA 导出的 .csv / .xlsx（其他扩展名也按表格式尝试）
        var parsed = LcedaBomParser.Parse(stream, fileName);
        if (parsed.Doc.Lines.Count == 0)
            throw new InvalidDataException("没有一行带商品编号（C 编号）的物料");
        var report = parsed.SkippedRows > 0
            ? $"共 {parsed.TotalRows} 行 · 可扫码匹配 {parsed.Doc.Lines.Count} 行 · 忽略 {parsed.SkippedRows} 行（无 C 编号）"
            : $"共 {parsed.Doc.Lines.Count} 行物料";
        return (parsed.Doc, report);
    }

    async Task FinishImportAsync(BomDoc doc, string report, string fileName)
    {
        // 工程名用文件名（去掉扩展名和 BOM_ 前缀）
        var name = Path.GetFileNameWithoutExtension(fileName);
        if (name.StartsWith("BOM_", StringComparison.OrdinalIgnoreCase)) name = name[4..];
        doc.Project = name;

        // 导入后直接问套数（单板用量 × 套数 = 需要量）
        var boardsText = await DisplayPromptAsync(
            "导入成功", $"{report}\n\n本次要焊几套板子？",
            accept: "确定", cancel: "默认 1 套",
            placeholder: "1", maxLength: 3,
            keyboard: Keyboard.Numeric, initialValue: "1");
        int boards = 1;
        if (int.TryParse(boardsText, out var b) && b >= 1) boards = b;

        BomState.Instance.Load(doc, boards);
    }

    void OnBoardsDown(object? sender, EventArgs e)
        => BomState.Instance.SetBoards(BomState.Instance.Boards - 1);

    void OnBoardsUp(object? sender, EventArgs e)
        => BomState.Instance.SetBoards(BomState.Instance.Boards + 1);

    void OnLoadDemo(object? sender, EventArgs e)
        => BomState.Instance.Load(DemoBom.Load());

    async void OnResetScans(object? sender, EventArgs e)
    {
        if (BomState.Instance.Doc is null) return;
        if (await DisplayAlertAsync("清空扫描记录？", "已扫描的袋子记录和数量都会归零，BOM 本身保留。", "清空", "取消"))
            BomState.Instance.ResetScans();
    }

    async void OnTestSounds(object? sender, EventArgs e)
    {
        var choice = await DisplayActionSheetAsync(
            "播放测试音（听不到 = 需要调大媒体音量）",
            "关闭", null,
            "命中（叮咚上行）", "找齐（三音琶音）", "全部找齐（琶音长）",
            "不需要（下行双音+振动）", "重复袋（三连击+两短振）", "无法识别（单音）");
        if (choice is null or "关闭") return;

        SoundKind kind = choice[0] switch
        {
            '命' => SoundKind.Hit,
            '找' => SoundKind.LineDone,
            '全' => SoundKind.AllDone,
            '不' => SoundKind.Miss,
            '重' => SoundKind.Duplicate,
            _ => SoundKind.ParseFail,
        };
        AlertSound.Play(kind);

        if (kind is SoundKind.Miss) Vibration.Default.Vibrate(220);
        else if (kind is SoundKind.Hit or SoundKind.LineDone) Vibration.Default.Vibrate(160);
        else if (kind is SoundKind.AllDone) Vibration.Default.Vibrate(400);
        else if (kind is SoundKind.Duplicate) { Vibration.Default.Vibrate(80); await Task.Delay(160); Vibration.Default.Vibrate(80); }
    }

    async void OnExportResults(object? sender, EventArgs e)    {
        var st = BomState.Instance;
        if (st.Doc is null) return;

        var choice = await DisplayActionSheetAsync(
            "导出扫描结果",
            "取消", null,
            "完整导出（全部物料）",
            "只导出缺少的部分");
        if (choice is null || choice == "取消") return;

        try
        {
            var onlyMissing = choice.StartsWith("只导出", StringComparison.Ordinal);
            var csv = "\uFEFF" + BomExporter.BuildCsv(st, onlyMissing);
            var name = BomExporter.SafeFileName($"备料结果_{st.Doc.Project}_{DateTime.Now:yyyyMMdd_HHmm}.csv");
            var path = Path.Combine(FileSystem.CacheDirectory, name);
            File.WriteAllText(path, csv, new System.Text.UTF8Encoding(true));

            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = name,
                File = new ShareFile(path),
            });
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("导出失败", ex.Message, "好");
        }
    }

    async void OnScanClicked(object? sender, EventArgs e)
    {
        // 相机权限必须在实际创建扫码页之前授予：
        // 否则 ZXing 相机控件会在未授权状态下初始化失败（表现为首次进入黑屏，杀进程重开才恢复）
        try
        {
            var status = await Permissions.CheckStatusAsync<Permissions.Camera>();
            if (status != PermissionStatus.Granted)
                status = await Permissions.RequestAsync<Permissions.Camera>();
            if (status != PermissionStatus.Granted)
            {
                await DisplayAlertAsync("需要相机权限", "扫码需要使用相机，请允许。", "好");
                return;
            }
        }
        catch { }

        await Shell.Current.GoToAsync(nameof(ScannerPage));
    }
}

file sealed class NullBomFiles : IBomFiles
{
    public List<string> ListBomFiles() => [];
}
