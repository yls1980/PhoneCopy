namespace PhoneCopy;

internal static class AdbMoveService
{
    internal static async Task<TransferReport> ToPcAsync(string serial, IReadOnlyList<BrowserItem> items, string destination,
        Func<string, string, ConflictChoice> resolve, CancellationToken token, IProgress<TransferProgress> progress)
    {
        var report = new TransferReport();
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            await TryMoveAsync(() => PhoneItemToPcAsync(serial, item, destination, resolve, token, progress, report), item.Path, !item.IsDirectory, report, progress);
        }
        return report;
    }

    internal static async Task<TransferReport> ToPhoneAsync(string serial, IReadOnlyList<BrowserItem> items, string destination,
        Func<string, string, ConflictChoice> resolve, CancellationToken token, IProgress<TransferProgress> progress)
    {
        var report = new TransferReport();
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            await TryMoveAsync(() => PcItemToPhoneAsync(serial, item.Path, destination, resolve, token, progress, report), item.Path, TransferCounter.IsPcFile(item.Path), report, progress);
        }
        return report;
    }

    private static async Task PhoneItemToPcAsync(string serial, BrowserItem item, string destination,
        Func<string, string, ConflictChoice> resolve, CancellationToken token,
        IProgress<TransferProgress> progress, TransferReport report)
    {
        token.ThrowIfCancellationRequested();
        string target = Path.Combine(destination, TransferService.SafeWindowsName(item.Name));
        if (item.IsDirectory)
        {
            if (File.Exists(target)) throw new IOException("На месте папки уже есть файл: " + target);
            Directory.CreateDirectory(target);
            int skippedBefore = report.Skipped, errorsBefore = report.Errors.Count;
            foreach (var child in await AdbStore.ListAsync(serial, item.Path, token))
            {
                token.ThrowIfCancellationRequested();
                await TryMoveAsync(() => PhoneItemToPcAsync(serial, child, target, resolve, token, progress, report), child.Path, !child.IsDirectory, report, progress);
            }
            if (report.Skipped == skippedBefore && report.Errors.Count == errorsBefore)
            {
                await AdbStore.RemoveEmptyDirectoryAsync(serial, item.Path);
                report.Copied++;
            }
            return;
        }
        if (Directory.Exists(target)) throw new IOException("На месте файла уже есть папка: " + target);
        bool replace = false;
        if (File.Exists(target))
        {
            switch (resolve(item.Path, target))
            {
                case ConflictChoice.Skip: report.Skipped++; return;
                case ConflictChoice.KeepBoth: target = TransferService.NumberedPcPath(target); break;
                case ConflictChoice.Replace: replace = true; break;
                default: throw new OperationCanceledException();
            }
        }
        long expected = item.Length ?? await AdbStore.LengthAsync(serial, item.Path, token);
        string temporary = target + ".phonecopy-" + Guid.NewGuid().ToString("N") + ".part";
        string? backup = null;
        bool committed = false;
        try
        {
            progress.Report(new TransferProgress(item.Name, 0, expected, report.Copied, report.Skipped));
            await AdbStore.PullAsync(serial, item.Path, temporary, token);
            long received = new FileInfo(temporary).Length;
            if (received != expected) throw new IOException($"Размер не совпал: телефон {expected} Б, компьютер {received} Б.");
            if (item.Modified is { Year: > 1900 } date)
                try { File.SetLastWriteTime(temporary, date); } catch { }
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
            try
            {
                await AdbStore.DeleteFileAsync(serial, item.Path);
                report.Copied++;
                progress.Report(new TransferProgress(item.Name, expected, expected, report.Copied, report.Skipped));
            }
            catch (Exception error)
            {
                throw new IOException("Копия создана, но исходный файл на телефоне не удалён. " +
                    (backup is null ? "" : "Старая версия назначения сохранена: " + backup + ". ") + error.Message, error);
            }
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

    private static async Task PcItemToPhoneAsync(string serial, string source, string destination,
        Func<string, string, ConflictChoice> resolve, CancellationToken token,
        IProgress<TransferProgress> progress, TransferReport report)
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
            int skippedBefore = report.Skipped, errorsBefore = report.Errors.Count;
            foreach (string child in Directory.EnumerateFileSystemEntries(source))
            {
                token.ThrowIfCancellationRequested();
                await TryMoveAsync(() => PcItemToPhoneAsync(serial, child, target, resolve, token, progress, report), child, TransferCounter.IsPcFile(child), report, progress);
            }
            if (report.Skipped == skippedBefore && report.Errors.Count == errorsBefore)
            {
                Directory.Delete(source, false);
                report.Copied++;
            }
            return;
        }
        bool replace = false;
        if (await AdbStore.ExistsAsync(serial, target, token))
        {
            if (await AdbStore.IsDirectoryAsync(serial, target, token)) throw new IOException("На месте файла уже есть папка: " + target);
            switch (resolve(source, target))
            {
                case ConflictChoice.Skip: report.Skipped++; return;
                case ConflictChoice.KeepBoth:
                    string stem = Path.GetFileNameWithoutExtension(name), extension = Path.GetExtension(name);
                    bool found = false;
                    for (int i = 2; i < 100000; i++)
                    {
                        string candidate = destination.TrimEnd('/') + "/" + stem + $" ({i})" + extension;
                        if (!await AdbStore.ExistsAsync(serial, candidate, token)) { target = candidate; found = true; break; }
                    }
                    if (!found) throw new IOException("Не удалось подобрать свободное имя: " + name);
                    break;
                case ConflictChoice.Replace: replace = true; break;
                default: throw new OperationCanceledException();
            }
        }
        long expected = new FileInfo(source).Length;
        string temporary = target + ".phonecopy-" + Guid.NewGuid().ToString("N") + ".part";
        string? backup = null;
        bool committed = false;
        try
        {
            progress.Report(new TransferProgress(name, 0, expected, report.Copied, report.Skipped));
            await AdbStore.PushAsync(serial, source, temporary, token);
            long received = await AdbStore.LengthAsync(serial, temporary, token);
            if (received != expected) throw new IOException($"Размер не совпал: компьютер {expected} Б, телефон {received} Б.");
            if (replace)
            {
                backup = target + ".phonecopy-backup-" + Guid.NewGuid().ToString("N");
                await AdbStore.MoveAsync(serial, target, backup, CancellationToken.None);
            }
            try { await AdbStore.MoveAsync(serial, temporary, target, CancellationToken.None); committed = true; }
            catch
            {
                if (backup is not null && !await AdbStore.ExistsAsync(serial, target, CancellationToken.None))
                    await AdbStore.MoveAsync(serial, backup, target, CancellationToken.None);
                throw;
            }
            try
            {
                File.Delete(source);
                if (File.Exists(source)) throw new IOException("Windows продолжает показывать исходный файл.");
                report.Copied++;
                progress.Report(new TransferProgress(name, expected, expected, report.Copied, report.Skipped));
            }
            catch (Exception error)
            {
                throw new IOException("Копия создана, но исходный файл на компьютере не удалён. " +
                    (backup is null ? "" : "Старая версия назначения сохранена: " + backup + ". ") + error.Message, error);
            }
            if (backup is not null)
            {
                try { await AdbStore.DeleteFileAsync(serial, backup); }
                catch (Exception error) { report.Errors.Add("Не удалось удалить резервную копию " + backup + ": " + error.Message); }
            }
        }
        finally
        {
            if (!committed && backup is not null)
                try { if (!await AdbStore.ExistsAsync(serial, target, CancellationToken.None)) await AdbStore.MoveAsync(serial, backup, target, CancellationToken.None); } catch { }
            await AdbStore.RemoveOwnTemporaryAsync(serial, temporary);
        }
    }

    private static async Task TryMoveAsync(Func<Task> action, string path, bool isFile, TransferReport report, IProgress<TransferProgress> progress)
    {
        int copiedBefore = report.Copied;
        try { await action(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { report.Errors.Add(path + ": " + error.Message); }
        if (isFile) TransferService.ReportFileFinished(progress, path, report, report.Copied > copiedBefore);
    }
}
