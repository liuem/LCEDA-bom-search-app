using System.Text.Json;

namespace lceda_bom_search_AndroidApp;

/// <summary>
/// BOM 文件导入的统一入口：App 内文件选择器与 LinkServer 浏览器上传（POST /bomfile）共用。
/// 支持 .json（App 自己导出的格式）与立创 EDA 导出的 .csv / .xlsx（其它扩展名按表格式尝试）。
/// </summary>
public static class BomFileImport
{
    public static BomDoc Parse(Stream stream, string fileName, out string report)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();

        if (ext == ".json")
        {
            var doc = JsonSerializer.Deserialize<BomDoc>(stream, BomModels.JsonOpts)
                      ?? throw new InvalidDataException("JSON 解析为空");
            if (doc.Lines is not { Count: > 0 })
                throw new InvalidDataException("文件里没有物料行（lines 为空）");
            report = $"JSON BOM：{doc.Lines.Count} 行";
            return doc;
        }

        var parsed = LcedaBomParser.Parse(stream, fileName);
        if (parsed.Doc.Lines.Count == 0)
            throw new InvalidDataException("没有一行带商品编号（C 编号）的物料");
        report = parsed.SkippedRows > 0
            ? $"共 {parsed.TotalRows} 行 · 可扫码匹配 {parsed.Doc.Lines.Count} 行 · 忽略 {parsed.SkippedRows} 行（无 C 编号）"
            : $"共 {parsed.Doc.Lines.Count} 行物料";
        return parsed.Doc;
    }

    /// <summary>统一行数据规范化：C 编号大写、数量兜底（与插件下发路径同规则）。</summary>
    public static List<BomLine> NormalizeLines(IEnumerable<BomLine> lines) =>
        lines
            .Where(l => !string.IsNullOrWhiteSpace(l.Lcsc))
            .Select(l =>
            {
                l.Lcsc = l.Lcsc.Trim().ToUpperInvariant();
                if (l.Qty < 1) l.Qty = Math.Max(1, l.Designators?.Count ?? 1);
                return l;
            })
            .ToList();

    /// <summary>展示用工程名：去扩展名与 BOM_ 前缀。</summary>
    public static string ProjectNameFrom(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        if (name.StartsWith("BOM_", StringComparison.OrdinalIgnoreCase)) name = name[4..];
        return name;
    }
}
