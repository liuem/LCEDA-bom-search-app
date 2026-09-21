using System.Text.Json;
using System.Text.Json.Serialization;

namespace lceda_bom_search_AndroidApp;

/// <summary>BOM 文档：一次备料任务的物料总表（由立创 EDA 插件导出，或手工构造）。</summary>
public sealed class BomDoc
{
    [JsonPropertyName("project")]
    public string Project { get; set; } = "";

    [JsonPropertyName("exportedAt")]
    public string ExportedAt { get; set; } = "";

    [JsonPropertyName("lines")]
    public List<BomLine> Lines { get; set; } = [];
}

/// <summary>一行物料：同一 C 编号对应多位号。</summary>
public sealed class BomLine
{
    [JsonPropertyName("lcsc")]
    public string Lcsc { get; set; } = "";

    [JsonPropertyName("value")]
    public string Value { get; set; } = "";

    [JsonPropertyName("footprint")]
    public string Footprint { get; set; } = "";

    [JsonPropertyName("qty")]
    public int Qty { get; set; }

    [JsonPropertyName("designators")]
    public List<string>? Designators { get; set; }

    public string Display => string.IsNullOrWhiteSpace(Value) ? Lcsc : Value;
}

public static class BomModels
{
    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
