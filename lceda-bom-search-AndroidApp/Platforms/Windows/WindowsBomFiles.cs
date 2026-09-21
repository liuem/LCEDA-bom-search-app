namespace lceda_bom_search_AndroidApp;

/// <summary>Windows（电脑模拟）：扫描“文档”目录。</summary>
public sealed class WindowsBomFiles : IBomFiles
{
    public List<string> ListBomFiles()
    {
        try
        {
            var dir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Directory.EnumerateFiles(dir, "*.*")
                .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".csv" or ".xlsx" or ".json")
                .OrderBy(p => p)
                .ToList();
        }
        catch { return []; }
    }
}
