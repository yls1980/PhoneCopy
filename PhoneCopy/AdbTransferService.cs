namespace PhoneCopy;

internal static class AdbTransferService
{
    public static async Task<TransferReport> ToPcAsync(string serial, IReadOnlyList<BrowserItem> items, string destination,
        bool number, CancellationToken token, IProgress<TransferProgress> progress)
    {
        var report = new TransferReport();
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            await TryItemAsync(() => DownloadItemAsync(serial, item, destination, number, token, progress, report), item.Path, !item.IsDirectory, report, progress);
        }
        return report;
    }

    public static async Task<TransferReport> ToPhoneAsync(string serial, IReadOnlyList<BrowserItem> items, string destination,
        bool number, CancellationToken token, IProgress<TransferProgress> progress)
    {
        var report = new TransferReport();
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            await TryItemAsync(() => UploadItemAsync(serial, item.Path, destination, number, token, progress, report), item.Path, TransferCounter.IsPcFile(item.Path), report, progress);
        }
        return report;
    }

    private static async Task DownloadItemAsync(string serial, BrowserItem item, string destination, bool number,
        CancellationToken token, IProgress<TransferProgress> progress, TransferReport report)
    {
        token.ThrowIfCancellationRequested();
        string target = Path.Combine(destination, TransferService.SafeWindowsName(item.Name));
        if (item.IsDirectory)
        {
            if (File.Exists(target)) throw new IOException("На месте папки уже есть файл: " + target);
            Directory.CreateDirectory(target);
            foreach (var child in await AdbStore.ListAsync(serial, item.Path, token))
            {
                token.ThrowIfCancellationRequested();
                await TryItemAsync(() => DownloadItemAsync(serial, child, target, number, token, progress, report), child.Path, !child.IsDirectory, report, progress);
            }
            return;
        }
        if (Directory.Exists(target)) throw new IOException("На месте файла уже есть папка: " + target);
        if (File.Exists(target))
        {
            if (!number) { report.Skipped++; return; }
            target = TransferService.NumberedPcPath(target);
        }
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".phonecopy-part";
        try
        {
            progress.Report(new TransferProgress(item.Name, 0, item.Length, report.Copied, report.Skipped));
            await AdbStore.PullAsync(serial, item.Path, temporary, token);
            long downloaded = new FileInfo(temporary).Length;
            if (item.Length is long expected && downloaded != expected)
                throw new IOException($"Размер не совпал: телефон {expected} Б, компьютер {downloaded} Б.");
            if (item.Modified is { Year: > 1900 } date)
            {
                try { File.SetLastWriteTime(temporary, date); } catch { }
            }
            File.Move(temporary, target, false);
            report.Copied++;
            progress.Report(new TransferProgress(item.Name, downloaded, downloaded, report.Copied, report.Skipped));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task UploadItemAsync(string serial, string source, string destination, bool number,
        CancellationToken token, IProgress<TransferProgress> progress, TransferReport report)
    {
        token.ThrowIfCancellationRequested();
        FileAttributes attributes = File.GetAttributes(source);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Ссылка на другую папку или файл пропущена: " + source);
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
        string target = destination.TrimEnd('/') + "/" + name;
        if ((attributes & FileAttributes.Directory) != 0)
        {
            if (await AdbStore.ExistsAsync(serial, target, token) && !await AdbStore.IsDirectoryAsync(serial, target, token))
                throw new IOException("На месте папки уже есть файл: " + target);
            await AdbStore.MakeDirectoryAsync(serial, target, token);
            foreach (string child in Directory.EnumerateFileSystemEntries(source))
            {
                token.ThrowIfCancellationRequested();
                await TryItemAsync(() => UploadItemAsync(serial, child, target, number, token, progress, report), child, TransferCounter.IsPcFile(child), report, progress);
            }
            return;
        }
        if (await AdbStore.ExistsAsync(serial, target, token))
        {
            if (await AdbStore.IsDirectoryAsync(serial, target, token)) throw new IOException("На месте файла уже есть папка: " + target);
            if (!number) { report.Skipped++; return; }
            string stem = Path.GetFileNameWithoutExtension(name);
            string extension = Path.GetExtension(name);
            bool found = false;
            for (int i = 2; i < 100000; i++)
            {
                string candidate = destination.TrimEnd('/') + "/" + stem + $" ({i})" + extension;
                if (!await AdbStore.ExistsAsync(serial, candidate, token)) { target = candidate; found = true; break; }
            }
            if (!found) throw new IOException("Не удалось подобрать свободное имя: " + name);
        }
        long size = new FileInfo(source).Length;
        string temporary = target + ".phonecopy-" + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            progress.Report(new TransferProgress(name, 0, size, report.Copied, report.Skipped));
            await AdbStore.PushAsync(serial, source, temporary, token);
            long received = await AdbStore.LengthAsync(serial, temporary, token);
            if (received != size) throw new IOException($"Размер не совпал: компьютер {size} Б, телефон {received} Б.");
            await AdbStore.MoveAsync(serial, temporary, target, token);
            report.Copied++;
            progress.Report(new TransferProgress(name, size, size, report.Copied, report.Skipped));
        }
        finally { await AdbStore.RemoveOwnTemporaryAsync(serial, temporary); }
    }

    private static async Task TryItemAsync(Func<Task> action, string path, bool isFile, TransferReport report, IProgress<TransferProgress> progress)
    {
        try { await action(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { report.Errors.Add(path + ": " + error.Message); }
        if (isFile) TransferService.ReportFileFinished(progress, path, report);
    }
}
