using MediaDevices;
using System.IO;
using System.Runtime.InteropServices;

namespace PhoneCopy;

internal sealed record TransferProgress(string Name, long Done, long? Total, int Copied, int Skipped, bool FileFinished = false, long FilesFinished = 0, long FilesCopied = 0);

internal sealed class TransferReport
{
    public int Copied { get; set; }
    public int Skipped { get; set; }
    public long FilesFinished { get; set; }
    public long FilesCopied { get; set; }
    public List<string> Errors { get; } = new();
}

internal static class TransferService
{
    public static TransferReport ToPc(MediaDevice device, IReadOnlyList<BrowserItem> items, string destination,
        bool numberOnConflict, CancellationToken token, IProgress<TransferProgress> progress)
    {
        var report = new TransferReport();
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            TryItem(() => DownloadItem(device, item, destination, numberOnConflict, token, progress, report), item.Path, !item.IsDirectory, report, progress);
        }
        return report;
    }

    public static TransferReport ToPhone(MediaDevice device, IReadOnlyList<BrowserItem> items, string destination,
        bool numberOnConflict, CancellationToken token, IProgress<TransferProgress> progress)
    {
        if (destination == "\\") throw new IOException("Откройте хранилище телефона (например, Internal shared storage) перед копированием.");
        var report = new TransferReport();
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            TryItem(() => UploadItem(device, item.Path, destination, numberOnConflict, token, progress, report), item.Path, TransferCounter.IsPcFile(item.Path), report, progress);
        }
        return report;
    }

    private static void DownloadItem(MediaDevice device, BrowserItem item, string destination, bool number,
        CancellationToken token, IProgress<TransferProgress> progress, TransferReport report)
    {
        token.ThrowIfCancellationRequested();
        string target = Path.Combine(destination, SafeWindowsName(item.Name));
        if (item.IsDirectory)
        {
            if (File.Exists(target)) throw new IOException("На месте папки уже есть файл: " + target);
            Directory.CreateDirectory(target);
            foreach (var child in PhoneStore.Entries(device, item.Path))
            {
                token.ThrowIfCancellationRequested();
                TryItem(() => DownloadItem(device, child, target, number, token, progress, report), child.Path, !child.IsDirectory, report, progress);
            }
            return;
        }
        if (Directory.Exists(target)) throw new IOException("На месте файла уже есть папка: " + target);
        if (File.Exists(target))
        {
            if (!number) { report.Skipped++; return; }
            target = NumberedPcPath(target);
        }
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".phonecopy-part";
        try
        {
            using (var source = OpenPhoneFile(device, item.Path, token))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024))
            {
                byte[] buffer = new byte[1024 * 1024];
                long done = 0;
                int read;
                progress.Report(new TransferProgress(item.Name, 0, item.Length, report.Copied, report.Skipped));
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, read);
                    done += read;
                    progress.Report(new TransferProgress(item.Name, done, item.Length, report.Copied, report.Skipped));
                }
                output.Flush(true);
                if (item.Length is long size && done != size)
                    throw new IOException($"Размер не совпал: телефон {size} Б, получено {done} Б.");
            }
            if (item.Modified is { Year: > 1900 } date)
            {
                try { File.SetLastWriteTime(temporary, date); } catch { /* Some MTP dates are unsupported by NTFS. */ }
            }
            File.Move(temporary, target, false);
            report.Copied++;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void UploadItem(MediaDevice device, string source, string destination, bool number,
        CancellationToken token, IProgress<TransferProgress> progress, TransferReport report)
    {
        token.ThrowIfCancellationRequested();
        var attributes = File.GetAttributes(source);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Ссылка на другую папку или файл пропущена: " + source);
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
        string target = PhonePath(destination, name);
        if ((attributes & FileAttributes.Directory) != 0)
        {
            if (device.FileExists(target)) throw new IOException("На месте папки уже есть файл: " + target);
            if (!device.DirectoryExists(target)) device.CreateDirectory(target);
            foreach (string child in Directory.EnumerateFileSystemEntries(source))
            {
                token.ThrowIfCancellationRequested();
                TryItem(() => UploadItem(device, child, target, number, token, progress, report), child, TransferCounter.IsPcFile(child), report, progress);
            }
            return;
        }
        if (device.DirectoryExists(target)) throw new IOException("На месте файла уже есть папка: " + target);
        if (device.FileExists(target))
        {
            if (!number) { report.Skipped++; return; }
            target = NumberedPhonePath(device, destination, name);
        }
        long size = new FileInfo(source).Length;
        bool uploaded = false;
        try
        {
            using var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024);
            using var input = new ProgressReadStream(file, token, done =>
                progress.Report(new TransferProgress(name, done, size, report.Copied, report.Skipped)));
            progress.Report(new TransferProgress(name, 0, size, report.Copied, report.Skipped));
            device.UploadFile(input, target);
            uploaded = true;
            ulong received = device.GetFileInfo(target).Length;
            if (received != (ulong)size) throw new IOException($"Размер не совпал: компьютер {size} Б, телефон {received} Б.");
            report.Copied++;
        }
        catch
        {
            if (!uploaded)
            {
                try { if (device.FileExists(target)) device.DeleteFile(target); } catch { }
            }
            throw;
        }
    }

    private static void TryItem(Action action, string path, bool isFile, TransferReport report, IProgress<TransferProgress> progress)
    {
        int copiedBefore = report.Copied;
        try { action(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            string message = error is COMException { ErrorCode: unchecked((int)0x800700AA) }
                ? "MTP: ресурс занят (0x800700AA). Отсоедините и вновь подключите USB-кабель, оставив «Передачу данных» включённой."
                : error.Message;
            report.Errors.Add(path + ": " + message);
        }
        if (isFile) ReportFileFinished(progress, path, report, report.Copied > copiedBefore);
    }

    internal static void ReportFileFinished(IProgress<TransferProgress> progress, string path, TransferReport report, bool copied = false)
    {
        report.FilesFinished++;
        if (copied) report.FilesCopied++;
        progress.Report(new TransferProgress(Path.GetFileName(path.TrimEnd('\\', '/')), 0, null, report.Copied, report.Skipped, true, report.FilesFinished, report.FilesCopied));
    }

    internal static Stream OpenPhoneFile(MediaDevice device, string path, CancellationToken token)
    {
        for (int attempt = 1; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { return device.GetFileInfo(path).OpenRead(); }
            catch (COMException error) when (error.ErrorCode is unchecked((int)0x80004005) or unchecked((int)0x800700AA))
            {
                if (attempt == 3)
                    throw new IOException($"MTP не смог открыть содержимое файла после {attempt} попыток (0x{error.ErrorCode:X8}). Исходный файл на телефоне сохранён.", error);
                Thread.Sleep(attempt * 300);
            }
        }
    }

    internal static string SafeWindowsName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string result = new(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        result = result.TrimEnd(' ', '.');
        if (result is "" or "." or "..") result = "_";
        string stem = result.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9" or "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9")
            result = "_" + result;
        return result;
    }

    internal static string NumberedPcPath(string path)
    {
        string folder = Path.GetDirectoryName(path)!;
        string stem = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        for (int i = 2; i < 100000; i++)
        {
            string candidate = Path.Combine(folder, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        throw new IOException("Не удалось подобрать свободное имя: " + path);
    }

    internal static string NumberedPhonePath(MediaDevice device, string folder, string name)
    {
        string stem = Path.GetFileNameWithoutExtension(name);
        string extension = Path.GetExtension(name);
        for (int i = 2; i < 100000; i++)
        {
            string candidate = PhonePath(folder, $"{stem} ({i}){extension}");
            if (!device.FileExists(candidate) && !device.DirectoryExists(candidate)) return candidate;
        }
        throw new IOException("Не удалось подобрать свободное имя: " + name);
    }

    private static string PhonePath(string folder, string name) => folder.TrimEnd('\\') + "\\" + name;

    private sealed class ProgressReadStream : Stream
    {
        private readonly Stream inner;
        private readonly CancellationToken token;
        private readonly Action<long> onRead;
        private long readTotal;
        public ProgressReadStream(Stream inner, CancellationToken token, Action<long> onRead)
        { this.inner = inner; this.token = token; this.onRead = onRead; }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            token.ThrowIfCancellationRequested();
            int read = inner.Read(buffer, offset, count);
            readTotal += read;
            onRead(readTotal);
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() => inner.Flush();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
