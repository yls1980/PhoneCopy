using System.Diagnostics;
using System.Text;

namespace PhoneCopy;

internal static class AdbStore
{
    private sealed record Result(int ExitCode, byte[] Output, string Error)
    {
        public string Text => Encoding.UTF8.GetString(Output);
    }

    public static async Task<PhoneChoice[]> FindAsync()
    {
        if (FindExecutable() is null) return Array.Empty<PhoneChoice>();
        try
        {
            var result = await RunAsync(null, new[] { "devices", "-l" }, null, CancellationToken.None);
            if (result.ExitCode != 0) return Array.Empty<PhoneChoice>();
            return result.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => !line.StartsWith("List of devices") && line.Contains("\tdevice"))
                .Select(line =>
                {
                    string serial = line.Split('\t')[0];
                    string model = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault(piece => piece.StartsWith("model:"))?[6..].Replace('_', ' ') ?? serial;
                    return new PhoneChoice(serial, "[ADB] " + model + " (" + serial + ")", true);
                }).ToArray();
        }
        catch { return Array.Empty<PhoneChoice>(); }
    }

    public static async Task<BrowserItem[]> ListAsync(string serial, string path, CancellationToken token = default)
    {
        string script = "dir=" + Quote(path) + "\n" +
            "[ -d \"$dir\" ] || exit 3\n" +
            "for f in \"$dir\"/* \"$dir\"/.[!.]* \"$dir\"/..?*; do\n" +
            "  [ -e \"$f\" ] || continue\n" +
            "  if [ -d \"$f\" ]; then kind=D; else kind=F; fi\n" +
            "  size=$(stat -c %s \"$f\" 2>/dev/null) || size=0\n" +
            "  modified=$(stat -c %Y \"$f\" 2>/dev/null) || modified=0\n" +
            "  printf '%s\\0%s\\0%s\\0%s\\0' \"$f\" \"$kind\" \"$size\" \"$modified\"\n" +
            "done\n";
        var result = await ShellAsync(serial, script, token);
        Check(result, "Не удалось открыть папку телефона");
        string[] fields = result.Text.Split('\0');
        var items = new List<BrowserItem>();
        for (int i = 0; i + 3 < fields.Length; i += 4)
        {
            string fullPath = fields[i];
            if (fullPath.Length == 0) continue;
            string name = fullPath[(fullPath.LastIndexOf('/') + 1)..];
            bool directory = fields[i + 1] == "D";
            long? length = long.TryParse(fields[i + 2], out long parsedSize) ? parsedSize : null;
            DateTime? modified = long.TryParse(fields[i + 3], out long unixSeconds) && unixSeconds > 0
                ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime : null;
            items.Add(new BrowserItem(name, fullPath, directory, directory ? null : length, modified));
        }
        return items.ToArray();
    }

    public static async Task<bool> ExistsAsync(string serial, string path, CancellationToken token)
    {
        var result = await ShellAsync(serial, "[ -e " + Quote(path) + " ]\n", token);
        if (result.ExitCode == 0) return true;
        if (result.ExitCode == 1) return false;
        Check(result, "Не удалось проверить путь на телефоне");
        return false;
    }

    public static async Task<bool> IsDirectoryAsync(string serial, string path, CancellationToken token)
    {
        var result = await ShellAsync(serial, "[ -d " + Quote(path) + " ]\n", token);
        if (result.ExitCode == 0) return true;
        if (result.ExitCode == 1) return false;
        Check(result, "Не удалось проверить папку на телефоне");
        return false;
    }

    public static async Task<long> LengthAsync(string serial, string path, CancellationToken token)
    {
        var result = await ShellAsync(serial, "stat -c %s " + Quote(path) + "\n", token);
        Check(result, "Не удалось проверить размер файла на телефоне");
        if (!long.TryParse(result.Text.Trim(), out long length)) throw new IOException("Телефон вернул некорректный размер файла.");
        return length;
    }

    public static async Task MakeDirectoryAsync(string serial, string path, CancellationToken token)
    {
        var result = await ShellAsync(serial, "mkdir -p " + Quote(path) + "\n", token);
        Check(result, "Не удалось создать папку на телефоне");
    }

    public static async Task MoveAsync(string serial, string oldPath, string newPath, CancellationToken token)
    {
        var result = await ShellAsync(serial, "mv " + Quote(oldPath) + " " + Quote(newPath) + "\n", token);
        Check(result, "Не удалось завершить запись файла на телефоне");
    }

    public static async Task DeleteFileAsync(string serial, string path)
    {
        if (!path.StartsWith("/sdcard/", StringComparison.Ordinal)) throw new IOException("Удаление вне хранилища телефона запрещено.");
        var result = await ShellAsync(serial, "rm " + Quote(path) + "\n", CancellationToken.None);
        Check(result, "Не удалось удалить исходный файл на телефоне");
        if (await ExistsAsync(serial, path, CancellationToken.None)) throw new IOException("Телефон продолжает показывать исходный файл.");
    }

    public static async Task RemoveEmptyDirectoryAsync(string serial, string path)
    {
        if (!path.StartsWith("/sdcard/", StringComparison.Ordinal)) throw new IOException("Удаление вне хранилища телефона запрещено.");
        var result = await ShellAsync(serial, "rmdir " + Quote(path) + "\n", CancellationToken.None);
        Check(result, "Не удалось удалить пустую исходную папку на телефоне");
        if (await ExistsAsync(serial, path, CancellationToken.None)) throw new IOException("Телефон продолжает показывать исходную папку.");
    }

    public static async Task RemoveOwnTemporaryAsync(string serial, string path)
    {
        try { await ShellAsync(serial, "rm -f " + Quote(path) + "\n", CancellationToken.None); } catch { }
    }

    public static async Task PullAsync(string serial, string remotePath, string localPath, CancellationToken token)
    {
        var result = await RunAsync(serial, new[] { "pull", "-q", remotePath, localPath }, null, token);
        Check(result, "Не удалось скачать файл с телефона");
    }

    public static async Task PushAsync(string serial, string localPath, string remotePath, CancellationToken token)
    {
        var result = await RunAsync(serial, new[] { "push", "-q", localPath, remotePath }, null, token);
        Check(result, "Не удалось отправить файл на телефон");
    }

    private static Task<Result> ShellAsync(string serial, string script, CancellationToken token) =>
        RunAsync(serial, new[] { "exec-out", "sh" }, script, token);

    private static async Task<Result> RunAsync(string? serial, IEnumerable<string> arguments, string? script, CancellationToken token)
    {
        string exe = FindExecutable() ?? throw new FileNotFoundException(
            "ADB не найден. Установите Android SDK Platform-Tools или положите папку platform-tools рядом с приложением.");
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = script is not null,
            StandardInputEncoding = new UTF8Encoding(false)
        };
        if (serial is not null) { start.ArgumentList.Add("-s"); start.ArgumentList.Add(serial); }
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("ADB не запустился.");
        using var registration = token.Register(() => { try { process.Kill(entireProcessTree: true); } catch { } });
        using var output = new MemoryStream();
        Task outputTask = process.StandardOutput.BaseStream.CopyToAsync(output, token);
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        if (script is not null)
        {
            await process.StandardInput.WriteAsync(script);
            process.StandardInput.Close();
        }
        await process.WaitForExitAsync(token);
        await outputTask;
        string error = await errorTask;
        token.ThrowIfCancellationRequested();
        return new Result(process.ExitCode, output.ToArray(), error.Trim());
    }

    private static void Check(Result result, string context)
    {
        if (result.ExitCode == 0) return;
        throw new IOException(context + ": " + (result.Error.Length > 0 ? result.Error : result.Text.Trim()));
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";

    public static string? FindExecutable()
    {
        string baseDirectory = AppContext.BaseDirectory;
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "platform-tools", "adb.exe"),
            Path.Combine(baseDirectory, "adb.exe"),
            Path.Combine(localAppData, "Android", "Sdk", "platform-tools", "adb.exe"),
            Path.Combine(Environment.GetEnvironmentVariable("ANDROID_HOME") ?? "", "platform-tools", "adb.exe"),
            Path.Combine(Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT") ?? "", "platform-tools", "adb.exe")
        };
        foreach (string path in candidates) if (File.Exists(path)) return path;
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (folder.Length == 0) continue;
            string path = Path.Combine(folder, "adb.exe");
            if (File.Exists(path)) return path;
        }
        return null;
    }
}
