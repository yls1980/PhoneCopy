using MediaDevices;

namespace PhoneCopy;

internal static class MoveService
{
    internal static TransferReport ToPc(MediaDevice device, IReadOnlyList<BrowserItem> items, string destination,
        Func<string, string, ConflictChoice> resolve, CancellationToken token, IProgress<TransferProgress> progress)
    {
        var report = new TransferReport();
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            TryMove(() => PhoneItemToPc(device, item, destination, resolve, token, progress, report), item.Path, !item.IsDirectory, report, progress);
        }
        return report;
    }

    internal static TransferReport ToPhone(MediaDevice device, IReadOnlyList<BrowserItem> items, string destination,
        Func<string, string, ConflictChoice> resolve, CancellationToken token, IProgress<TransferProgress> progress)
    {
        var report = new TransferReport();
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            TryMove(() => PcItemToPhone(device, item.Path, destination, resolve, token, progress, report), item.Path, TransferCounter.IsPcFile(item.Path), report, progress);
        }
        return report;
    }

    private static void PhoneItemToPc(MediaDevice device, BrowserItem item, string destination,
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
            foreach (var child in PhoneStore.Entries(device, item.Path))
            {
                token.ThrowIfCancellationRequested();
                TryMove(() => PhoneItemToPc(device, child, target, resolve, token, progress, report), child.Path, !child.IsDirectory, report, progress);
            }
            if (report.Skipped == skippedBefore && report.Errors.Count == errorsBefore)
            {
                device.DeleteDirectory(item.Path, false);
                if (device.DirectoryExists(item.Path)) throw new IOException("Телефон продолжает показывать исходную папку.");
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
        long expected = item.Length ?? checked((long)device.GetFileInfo(item.Path).Length);
        string temporary = target + ".phonecopy-" + Guid.NewGuid().ToString("N") + ".part";
        string? backup = null;
        bool committed = false;
        try
        {
            using (var source = TransferService.OpenPhoneFile(device, item.Path, token))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024))
            {
                byte[] buffer = new byte[1024 * 1024];
                long done = 0;
                int read;
                progress.Report(new TransferProgress(item.Name, 0, expected, report.Copied, report.Skipped));
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, read);
                    done += read;
                    progress.Report(new TransferProgress(item.Name, done, expected, report.Copied, report.Skipped));
                }
                output.Flush(true);
                if (done != expected) throw new IOException($"Размер не совпал: телефон {expected} Б, компьютер {done} Б.");
            }
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
                device.DeleteFile(item.Path);
                if (device.FileExists(item.Path)) throw new IOException("Телефон продолжает показывать исходный файл.");
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

    private static void PcItemToPhone(MediaDevice device, string source, string destination,
        Func<string, string, ConflictChoice> resolve, CancellationToken token,
        IProgress<TransferProgress> progress, TransferReport report)
    {
        token.ThrowIfCancellationRequested();
        FileAttributes attributes = File.GetAttributes(source);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Ссылка на другую папку или файл пропущена: " + source);
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
        string target = PhonePath(destination, name);
        if ((attributes & FileAttributes.Directory) != 0)
        {
            if (device.FileExists(target)) throw new IOException("На месте папки уже есть файл: " + target);
            if (!device.DirectoryExists(target)) device.CreateDirectory(target);
            int skippedBefore = report.Skipped, errorsBefore = report.Errors.Count;
            foreach (string child in Directory.EnumerateFileSystemEntries(source))
            {
                token.ThrowIfCancellationRequested();
                TryMove(() => PcItemToPhone(device, child, target, resolve, token, progress, report), child, TransferCounter.IsPcFile(child), report, progress);
            }
            if (report.Skipped == skippedBefore && report.Errors.Count == errorsBefore)
            {
                Directory.Delete(source, false);
                report.Copied++;
            }
            return;
        }
        if (device.DirectoryExists(target)) throw new IOException("На месте файла уже есть папка: " + target);
        bool replace = false;
        if (device.FileExists(target))
        {
            switch (resolve(source, target))
            {
                case ConflictChoice.Skip: report.Skipped++; return;
                case ConflictChoice.KeepBoth:
                    target = TransferService.NumberedPhonePath(device, destination, name);
                    name = target[(target.LastIndexOf('\\') + 1)..];
                    break;
                case ConflictChoice.Replace: replace = true; break;
                default: throw new OperationCanceledException();
            }
        }
        long expected = new FileInfo(source).Length;
        string? backupPath = null;
        bool backupCreated = false;
        bool verified = false;
        try
        {
            if (replace)
            {
                string backupName = ".phonecopy-backup-" + Guid.NewGuid().ToString("N");
                backupPath = PhonePath(destination, backupName);
                device.Rename(target, backupName);
                if (!device.FileExists(backupPath) || device.FileExists(target))
                    throw new IOException("Не удалось сохранить старую версию файла перед заменой.");
                backupCreated = true;
            }
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024))
            {
                progress.Report(new TransferProgress(name, 0, expected, report.Copied, report.Skipped));
                device.UploadFile(input, target);
            }
            ulong received = device.GetFileInfo(target).Length;
            if (received != (ulong)expected) throw new IOException($"Размер не совпал: компьютер {expected} Б, телефон {received} Б.");
            verified = true;
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
                    (backupPath is null ? "" : "Старая версия назначения сохранена: " + backupPath + ". ") + error.Message, error);
            }
            if (backupPath is not null)
            {
                try
                {
                    device.DeleteFile(backupPath);
                    if (device.FileExists(backupPath)) throw new IOException("Телефон продолжает показывать резервную копию.");
                }
                catch (Exception error) { report.Errors.Add("Не удалось удалить резервную копию " + backupPath + ": " + error.Message); }
            }
        }
        catch
        {
            if (!verified)
            {
                if (!replace || backupCreated)
                    try { if (device.FileExists(target)) device.DeleteFile(target); } catch { }
                if (backupCreated && backupPath is not null)
                    try { if (!device.FileExists(target)) device.Rename(backupPath, Path.GetFileName(target)); } catch { }
                if (backupPath is not null)
                    try { if (device.FileExists(backupPath)) report.Errors.Add("Старая версия назначения сохранена на телефоне: " + backupPath); } catch { }
            }
            throw;
        }
    }

    internal static void TryMove(Action action, string path, bool isFile, TransferReport report, IProgress<TransferProgress> progress)
    {
        try { action(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { report.Errors.Add(path + ": " + error.Message); }
        if (isFile) TransferService.ReportFileFinished(progress, path, report);
    }

    private static string PhonePath(string folder, string name) => folder.TrimEnd('\\') + "\\" + name;
}
