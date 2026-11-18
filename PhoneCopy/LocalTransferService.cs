namespace PhoneCopy;

internal static class LocalTransferService
{
    public static TransferReport Copy(IReadOnlyList<BrowserItem> items, string destination, bool numberOnConflict,
        CancellationToken token, IProgress<TransferProgress> progress) =>
        Run(items, destination, false, numberOnConflict, null, token, progress);

    public static TransferReport Move(IReadOnlyList<BrowserItem> items, string destination,
        Func<string, string, ConflictChoice> resolve, CancellationToken token, IProgress<TransferProgress> progress) =>
        Run(items, destination, true, false, resolve, token, progress);

    private static TransferReport Run(IReadOnlyList<BrowserItem> items, string destination, bool move, bool number,
        Func<string, string, ConflictChoice>? resolve, CancellationToken token, IProgress<TransferProgress> progress)
    {
        if (!Directory.Exists(destination)) throw new DirectoryNotFoundException("Папка назначения недоступна: " + destination);
        var report = new TransferReport();
        foreach (BrowserItem item in items)
        {
            token.ThrowIfCancellationRequested();
            TryItem(() => ProcessItem(item.Path, destination, move, number, resolve, token, progress, report),
                item.Path, TransferCounter.IsPcFile(item.Path), report, progress);
        }
        return report;
    }

    private static void ProcessItem(string source, string destination, bool move, bool number,
        Func<string, string, ConflictChoice>? resolve, CancellationToken token, IProgress<TransferProgress> progress,
        TransferReport report)
    {
        token.ThrowIfCancellationRequested();
        FileAttributes attributes = File.GetAttributes(source);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Ссылка на другую папку или файл пропущена: " + source);
        string absoluteSource = Path.GetFullPath(source);
        if (absoluteSource.Equals(Path.GetPathRoot(absoluteSource), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Выберите файлы или папку внутри диска, а не весь диск: " + source);

        string target = Path.Combine(destination, Path.GetFileName(Path.TrimEndingDirectorySeparator(source)));
        string sourceFull = absoluteSource.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string targetFull = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (targetFull.Equals(sourceFull, StringComparison.OrdinalIgnoreCase) ||
            targetFull.StartsWith(sourceFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Папка назначения совпадает с источником или находится внутри него: " + target);

        if ((attributes & FileAttributes.Directory) != 0)
        {
            if (File.Exists(target)) throw new IOException("На месте папки уже есть файл: " + target);
            Directory.CreateDirectory(target);
            int skippedBefore = report.Skipped, errorsBefore = report.Errors.Count;
            foreach (string child in Directory.EnumerateFileSystemEntries(source))
            {
                token.ThrowIfCancellationRequested();
                TryItem(() => ProcessItem(child, target, move, number, resolve, token, progress, report),
                    child, TransferCounter.IsPcFile(child), report, progress);
            }
            if (move && report.Skipped == skippedBefore && report.Errors.Count == errorsBefore)
            {
                Directory.Delete(source, false);
                report.Copied++;
            }
            return;
        }

        if (Directory.Exists(target)) throw new IOException("На месте файла уже есть папка: " + target);
        bool replace = false;
        if (File.Exists(target))
        {
            if (!move)
            {
                if (!number) { report.Skipped++; return; }
                target = TransferService.NumberedPcPath(target);
            }
            else
            {
                switch (resolve!(source, target))
                {
                    case ConflictChoice.Skip: report.Skipped++; return;
                    case ConflictChoice.KeepBoth: target = TransferService.NumberedPcPath(target); break;
                    case ConflictChoice.Replace: replace = true; break;
                    default: throw new OperationCanceledException();
                }
            }
        }

        long expected = new FileInfo(source).Length;
        string temporary = target + ".phonecopy-" + Guid.NewGuid().ToString("N") + ".part";
        string? backup = null;
        bool committed = false;
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024))
            {
                var buffer = new byte[1024 * 1024];
                long done = 0;
                progress.Report(new TransferProgress(Path.GetFileName(source), 0, expected, report.Copied, report.Skipped));
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, read);
                    done += read;
                    progress.Report(new TransferProgress(Path.GetFileName(source), done, expected, report.Copied, report.Skipped));
                }
                output.Flush(true);
                if (done != expected) throw new IOException($"Размер не совпал: источник {expected} Б, получено {done} Б.");
            }
            token.ThrowIfCancellationRequested();
            try { File.SetLastWriteTime(temporary, File.GetLastWriteTime(source)); } catch { }
            if (replace)
            {
                backup = target + ".phonecopy-backup-" + Guid.NewGuid().ToString("N");
                File.Move(target, backup);
            }
            try { File.Move(temporary, target, false); committed = true; }
            catch
            {
                if (backup is not null && File.Exists(backup) && !File.Exists(target)) File.Move(backup, target);
                throw;
            }
            if (move)
            {
                try
                {
                    File.Delete(source);
                    if (File.Exists(source)) throw new IOException("Исходный файл всё ещё виден на устройстве.");
                }
                catch (Exception error)
                {
                    throw new IOException("Копия создана, но исходный файл не удалён. " +
                        (backup is null ? "" : "Старая версия назначения сохранена: " + backup + ". ") + error.Message, error);
                }
            }
            report.Copied++;
            progress.Report(new TransferProgress(Path.GetFileName(source), expected, expected, report.Copied, report.Skipped));
            if (backup is not null)
            {
                try { File.Delete(backup); }
                catch (Exception error) { report.Errors.Add("Не удалось удалить резервную копию " + backup + ": " + error.Message); }
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (!committed && backup is not null && File.Exists(backup) && !File.Exists(target)) File.Move(backup, target);
        }
    }

    private static void TryItem(Action action, string path, bool isFile, TransferReport report, IProgress<TransferProgress> progress)
    {
        int copiedBefore = report.Copied;
        try { action(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { report.Errors.Add(path + ": " + error.Message); }
        if (isFile) TransferService.ReportFileFinished(progress, path, report, report.Copied > copiedBefore);
    }
}
