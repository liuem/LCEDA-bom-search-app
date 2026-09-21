using System.Text.RegularExpressions;

namespace lceda_bom_search_AndroidApp;

/// <summary>袋子标签解析结果。嘉立创元件袋：
/// 一维码 = X+商品详情ID(pdi)，不是 C 编号（需经 pdi→C 映射才能定位物料）；
/// 二维码 = 形如 {on:SO25102416466,pc:C32843,pm:W5500,qty:10,...} 的非标准 JSON（键值不带引号）。</summary>
public sealed class BagTag
{
    public string Lcsc { get; init; } = "";
    public string? PartNo { get; init; }
    public int? Qty { get; init; }
    public string? OrderNo { get; init; }
    public string? Pdi { get; init; }          // 商品详情 ID（二维码 pdi 字段 / 一维码 X 前缀后的数字）
    public string Raw { get; init; } = "";
    /// <summary>true=二维码（信息全）；false=一维码。</summary>
    public bool FromQr { get; init; }
    /// <summary>这袋实际计入的数量（默认=标称 Qty；用户修正后更新）。null = 数量未知。</summary>
    public int? Counted { get; set; }
}

public static partial class TagParser
{
    /// <summary>纯 C 编号。</summary>
    [GeneratedRegex(@"^C\d{4,}$")]
    private static partial Regex PlainLcscRegex();

    /// <summary>一维物流码：X+商品详情ID，如 X228043539。</summary>
    [GeneratedRegex(@"^X(\d{6,})$")]
    private static partial Regex XCodeRegex();

    private static string? Field(string raw, string key)
    {
        // 宽容匹配 pc: 与 "pc":、C32843 与 "C32843" 两种形态；值止于逗号/引号/右花括号
        var pattern = "\"?" + key + "\"?\\s*:\\s*\"?([^,\"}\\s][^,\"}]*)";
        var m = Regex.Match(raw, pattern);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    /// <summary>解析袋子码；无法识别时返回 null。</summary>
    public static BagTag? Parse(string raw)
    {
        // 清洗输入法可能混入的控制符/零宽字符（肉眼看不出但会让正则失配）
        var s = new string(raw.Where(ch =>
            !char.IsControl(ch)
            && !(ch >= '\u200b' && ch <= '\u200f')
            && ch != '\ufeff').ToArray()).Trim();
        if (s.Length == 0) return null;

        if (PlainLcscRegex().IsMatch(s))
            return new BagTag { Lcsc = s, FromQr = false, Raw = s };

        if (XCodeRegex().IsMatch(s))
            return new BagTag { Pdi = XCodeRegex().Match(s).Groups[1].Value, FromQr = false, Raw = s };

        var pc = Field(s, "pc");
        if (pc is null || !pc.StartsWith('C'))
            return null;

        int? qty = null;
        if (int.TryParse(Field(s, "qty"), out var q) && q > 0)
            qty = q;

        return new BagTag
        {
            Lcsc = pc,
            FromQr = true,
            Raw = s,
            PartNo = Field(s, "pm"),
            OrderNo = Field(s, "on"),
            Qty = qty,
            Pdi = Field(s, "pdi"),
        };
    }
}
