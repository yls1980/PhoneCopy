using MediaDevices;

namespace PhoneCopy;

internal static class TransferCounter
{
    public static bool IsPcFile(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch { return !Directory.Exists(path); }
    }

    public static long CountPcFiles(IReadOnlyList<BrowserItem> selected, CancellationToken token)
    {
        long count = 0;
        var pending = new Stack<string>(selected.Select(item => item.Path));
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            string path = pending.Pop();
            if (IsPcFile(path)) { count++; continue; }
            foreach (string child in Directory.EnumerateFileSystemEntries(path)) pending.Push(child);
        }
        return count;
    }

    public static long CountMtpFiles(MediaDevice device, IReadOnlyList<BrowserItem> selected, CancellationToken token)
    {
        long count = 0;
        var pending = new Stack<BrowserItem>(selected);
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            BrowserItem item = pending.Pop();
            if (!item.IsDirectory) { count++; continue; }
            foreach (BrowserItem child in PhoneStore.Entries(device, item.Path)) pending.Push(child);
        }
        return count;
    }

    public static async Task<long> CountAdbFilesAsync(string serial, IReadOnlyList<BrowserItem> selected, CancellationToken token)
    {
        long count = 0;
        var pending = new Stack<BrowserItem>(selected);
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            BrowserItem item = pending.Pop();
            if (!item.IsDirectory) { count++; continue; }
            foreach (BrowserItem child in await AdbStore.ListAsync(serial, item.Path, token)) pending.Push(child);
        }
        return count;
    }
}
