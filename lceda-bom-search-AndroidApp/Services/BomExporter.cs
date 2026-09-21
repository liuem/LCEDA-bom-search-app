using System.Text;

namespace lceda_bom_search_AndroidApp;

/// <summary>扫描结果导出为 CSV（UTF-8 BOM，Excel 直接打开不乱码）。</summary>
public static class BomExporter
{
    public static string BuildCsv(BomState st, bool onlyMissing)
    {
        var sb = new StringBuilder();
        sb.AppendLine("C编号,型号/值,封装,需要量,已扫数量,是否有料,状态,扫过袋数,订单号,位号");

        foreach (var p in st.Progress)
        {
            var need = st.NeededOf(p);
            var done = st.IsDone(p);
            var qtyUnknown = p.Bags.Count > 0 && p.Scanned == 0;

            // 缺少 = 没找齐 / 标了缺量 / 数量未知
            var missing = !done || p.Flagged || qtyUnknown;
            if (onlyMissing && !missing) continue;

            var has = p.Bags.Count > 0 ? "有" : "无";
            string status = p.Flagged ? "⚑缺量"
                          : qtyUnknown ? "数量未知"
                          : done ? (p.Scanned > need ? "找齐(超额)" : "找齐")
                          : p.Scanned > 0 ? "部分"
                          : "未找到";

            var orders = string.Join(";", p.Bags
                .Select(b => b.OrderNo)
                .Where(o => !string.IsNullOrEmpty(o))
                .Distinct());
            var des = p.Line.Designators is { } d ? string.Join(" ", d) : "";

            sb.AppendLine($"{p.Line.Lcsc},{Esc(p.Line.Value)},{Esc(p.Line.Footprint)},{need},{p.Scanned},{has},{status},{p.Bags.Count},{orders},{Esc(des)}");
        }
        return sb.ToString();
    }

    static string Esc(string s)
        => s.Contains(',') || s.Contains('"')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;

    public static string SafeFileName(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name)
            sb.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
        return sb.ToString();
    }
}
