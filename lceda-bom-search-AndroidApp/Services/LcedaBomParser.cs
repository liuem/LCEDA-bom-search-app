using System.Text;
using MiniExcelLibs;
using MiniExcelLibs.OpenXml;

namespace lceda_bom_search_AndroidApp;

/// <summary>立创 EDA 导出 BOM 的解析结果。</summary>
public sealed class LcedaBomParseResult
{
    public required BomDoc Doc { get; init; }
    public int TotalRows { get; init; }    // 数据总行数
    public int SkippedRows { get; init; }  // 无 C 编号被忽略的行
}

/// <summary>直接读取立创 EDA 专业版导出的 BOM（.csv / .xlsx）。
/// 列头（英文版）：No. Quantity Comment Designator Footprint Value Manufacturer Part Manufacturer Supplier Part Supplier。
/// 其中 Supplier Part = 立创 C 编号（与袋子二维码 pc 字段匹配的键）；中文版表头同步兼容。</summary>
public static class LcedaBomParser
{
    public static LcedaBomParseResult Parse(Stream stream, string fileName)
    {
        // 部分机型的文件选择器返回不可定位（CanSeek=false）的流，
        // 下面 stream.Position = 0 会直接抛 NotSupportedException——先完整拷进内存
        if (!stream.CanSeek)
        {
            var ms = new MemoryStream();
            stream.CopyTo(ms);
            stream = ms;
        }

        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var isZip = IsZip(stream);
        stream.Position = 0;

        List<Dictionary<string, string>> rows;
        if (ext == ".xlsx" || isZip)
            rows = ReadXlsx(stream);
        else
            rows = ReadCsv(stream);

        return BuildDoc(rows);
    }

    static bool IsZip(Stream s)
    {
        if (s.Length < 4) return false;
        var head = new byte[2];
        int n = s.Read(head, 0, 2);
        return n == 2 && head[0] == (byte)'P' && head[1] == (byte)'K';
    }

    // ---------- xlsx ----------

    static List<Dictionary<string, string>> ReadXlsx(Stream stream)
    {
        var result = new List<Dictionary<string, string>>();
        var rows = stream.Query(useHeaderRow: true, excelType: ExcelType.XLSX);
        foreach (IDictionary<string, object?> r in rows)
        {
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in r)
            {
                if (!string.IsNullOrWhiteSpace(k))
                    row[k.Trim()] = v?.ToString() ?? "";
            }
            result.Add(row);
        }
        return result;
    }

    // ---------- csv ----------

    static List<Dictionary<string, string>> ReadCsv(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var text = DecodeText(ms.ToArray());
        var delim = DetectDelimiter(text);
        var table = ParseDelimited(text, delim);
        if (table.Count == 0) return [];

        var header = table[0].Select(h => h.Trim()).ToList();
        var rows = new List<Dictionary<string, string>>();
        foreach (var cols in table.Skip(1))
        {
            if (cols.All(string.IsNullOrWhiteSpace)) continue;
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < header.Count; i++)
                row[header[i]] = i < cols.Count ? cols[i] : "";
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>BOM 编码自适应：Excel 中文环境导出常为 UTF-16LE，也有 UTF-8 BOM / 无 BOM / GBK。</summary>
    static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            try { return Encoding.GetEncoding("GB18030").GetString(bytes); }
            catch { return Encoding.Latin1.GetString(bytes); }
        }
    }

    static char DetectDelimiter(string text)
    {
        var firstLine = text.Split('\n').FirstOrDefault() ?? "";
        int tabs = firstLine.Count(c => c == '\t');
        int commas = firstLine.Count(c => c == ',');
        return commas > tabs ? ',' : '\t';
    }

    /// <summary>带引号转义的字段级解析（处理 "含,逗号" 与 "" 转义）。</summary>
    static List<List<string>> ParseDelimited(string text, char delim)
    {
        var rows = new List<List<string>>();
        var field = new StringBuilder();
        var row = new List<string>();
        bool inQuotes = false;

        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == delim) { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\n') { row.Add(field.ToString()); field.Clear(); rows.Add(row); row = []; }
            else if (c != '\r') field.Append(c);
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }

    // ---------- 表头映射与行构造 ----------

    static readonly string[] LcscCols = ["supplierpart", "supplierpart#", "lcscpart#", "lcscpart", "商品编号", "物料编号", "扩展编号"];
    static readonly string[] QtyCols = ["quantity", "数量", "贴装数量", "用量"];
    static readonly string[] DesCols = ["designator", "位号", "参考封装位号"];
    static readonly string[] FootprintCols = ["footprint", "封装", "封装名称"];
    static readonly string[] ValueCols = ["value", "型号", "comment", "名称"];
    static readonly string[] MpnCols = ["manufacturerpart", "制造商编号", "制造商型号"];

    static string? FindCol(Dictionary<string, string> row, string[] candidates)
    {
        foreach (var key in row.Keys)
        {
            var norm = Normalize(key);
            if (candidates.Contains(norm)) return key;
        }
        return null;
    }

    static string Normalize(string s) =>
        new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    static LcedaBomParseResult BuildDoc(List<Dictionary<string, string>> rows)
    {
        if (rows.Count == 0) throw new InvalidDataException("BOM 文件里没有数据行");

        var header = rows[0];
        var lcscCol = FindCol(header, LcscCols) ?? throw new InvalidDataException("找不到商品编号列（Supplier Part / 商品编号）");
        var qtyCol = FindCol(header, QtyCols);
        var desCol = FindCol(header, DesCols);
        var fpCol = FindCol(header, FootprintCols);
        var valCol = FindCol(header, ValueCols);
        var mpnCol = FindCol(header, MpnCols);

        var lines = new List<BomLine>();
        int skipped = 0;

        foreach (var r in rows)
        {
            var lcsc = r.GetValueOrDefault(lcscCol)?.Trim() ?? "";
            if (!System.Text.RegularExpressions.Regex.IsMatch(lcsc, @"^C\d{4,}$"))
            {
                skipped++;
                continue;
            }

            var qty = 1;
            if (qtyCol is not null && int.TryParse((r.GetValueOrDefault(qtyCol) ?? "").Trim(), out var q) && q > 0)
                qty = q;

            var value = FirstNonEmpty(valCol is null ? null : r.GetValueOrDefault(valCol),
                                      mpnCol is null ? null : r.GetValueOrDefault(mpnCol));

            List<string>? designators = null;
            if (desCol is not null)
            {
                var list = (r.GetValueOrDefault(desCol) ?? "")
                    .Split([',', '，', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
                if (list.Count > 0) designators = list;
            }

            lines.Add(new BomLine
            {
                Lcsc = lcsc,
                Value = value,
                Footprint = fpCol is null ? "" : (r.GetValueOrDefault(fpCol) ?? "").Trim(),
                Qty = qty,
                Designators = designators,
            });
        }

        var doc = new BomDoc
        {
            Project = "立创 BOM",
            ExportedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            Lines = lines,
        };
        return new LcedaBomParseResult { Doc = doc, TotalRows = rows.Count, SkippedRows = skipped };
    }

    static string FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        return "";
    }
}
