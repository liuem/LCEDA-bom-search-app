namespace lceda_bom_search_AndroidApp;

/// <summary>BOM 文件发现服务：扫描应用可直接访问的目录，列出可导入的 BOM 文件。</summary>
public interface IBomFiles
{
    /// <summary>返回候选 BOM 文件的完整路径（仅 .csv/.xlsx/.json）。</summary>
    List<string> ListBomFiles();
}
