namespace lceda_bom_search_AndroidApp;

/// <summary>Android：应用外部私有目录（/sdcard/Android/data/&lt;pkg&gt;/files/）及其 Download 子目录。
/// 无需存储权限；USB / adb push / 文件管理器均可直接放入。</summary>
public sealed class AndroidBomFiles : IBomFiles
{
    public List<string> ListBomFiles()
    {
        var result = new List<string>();
        try
        {
            var root = Android.App.Application.Context.GetExternalFilesDir("")?.AbsolutePath;
            if (string.IsNullOrEmpty(root)) return result;

            foreach (var dir in new[] { root, System.IO.Path.Combine(root, "Download") })
            {
                if (!Directory.Exists(dir)) continue;
                result.AddRange(Directory.EnumerateFiles(dir, "*.*")
                    .Where(f => ExtensionOk(f)));
            }
        }
        catch { }
        return result.OrderBy(p => p).ToList();
    }

    static bool ExtensionOk(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".csv" or ".xlsx" or ".json";
    }
}
