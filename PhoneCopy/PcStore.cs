namespace PhoneCopy;

internal static class PcStore
{
    public static BrowserItem[] List(string? path)
    {
        if (path is null)
            return DriveInfo.GetDrives().Where(d => d.IsReady)
                .Select(d => new BrowserItem(d.Name, d.RootDirectory.FullName, true, null, null)).ToArray();

        return new DirectoryInfo(path).EnumerateFileSystemInfos().Select(item =>
        {
            bool directory = item is DirectoryInfo;
            long? length = null;
            DateTime? modified = null;
            if (item is FileInfo file) { try { length = file.Length; } catch { } }
            try { modified = item.LastWriteTime; } catch { }
            return new BrowserItem(item.Name, item.FullName, directory, length, modified);
        }).ToArray();
    }
}
