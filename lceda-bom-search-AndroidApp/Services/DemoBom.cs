using System.Text.Json;

namespace lceda_bom_search_AndroidApp;

/// <summary>演示 BOM：C32843/W5500 是真实袋子（用户手边），其余行为虚构演示数据。</summary>
public static class DemoBom
{
    public const string Json = """
    {
      "project": "演示工程（内含手边的 W5500 袋）",
      "exportedAt": "2026-09-20T11:40:00+08:00",
      "lines": [
        { "lcsc": "C32843", "value": "W5500 以太网芯片", "footprint": "LQFP-48", "qty": 1, "designators": ["U7"] },
        { "lcsc": "C900001", "value": "10kΩ ±1%", "footprint": "0402", "qty": 8, "designators": ["R1","R2","R3","R4","R5","R6","R7","R8"] },
        { "lcsc": "C900002", "value": "100nF 50V X7R", "footprint": "0402", "qty": 24, "designators": null },
        { "lcsc": "C900003", "value": "AMS1117-3.3", "footprint": "SOT-223", "qty": 2, "designators": ["U1","U2"] },
        { "lcsc": "C900004", "value": "SS8050 三极管", "footprint": "SOT-23", "qty": 5, "designators": null },
        { "lcsc": "C900005", "value": "TYPE-C 16P 母座", "footprint": "TYPE-C-31-M-12", "qty": 1, "designators": ["J1"] }
      ]
    }
    """;

    public static BomDoc Load() => JsonSerializer.Deserialize<BomDoc>(Json, BomModels.JsonOpts) ?? new BomDoc();
}
