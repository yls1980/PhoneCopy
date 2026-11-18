using MediaDevices;
using PhoneCopy;
using System.Text;

if (args.Contains("--device-list"))
{
    foreach (PhoneChoice choice in await PhoneStore.FindAsync())
        Console.WriteLine(choice.Name + " | drive=" + choice.IsDrive + " | id=" + choice.Id);
    return;
}

int readCopyIndex = Array.IndexOf(args, "--read-copy-smoke");
if (readCopyIndex >= 0)
{
    if (readCopyIndex + 1 >= args.Length) throw new ArgumentException("Укажите полный путь к файлу на камере.");
    string source = Path.GetFullPath(args[readCopyIndex + 1]);
    string workspace = Path.GetFullPath(Environment.CurrentDirectory);
    string testRoot = Path.Combine(workspace, ".phonecopy-camera-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(testRoot);
    try
    {
        var item = new FileInfo(source);
        var progress = new Progress<TransferProgress>(_ => { });
        var report = LocalTransferService.Copy(new[] { new BrowserItem(item.Name, source, false, item.Length, item.LastWriteTime) },
            testRoot, false, CancellationToken.None, progress);
        string copied = Path.Combine(testRoot, item.Name);
        Require(report.Copied == 1 && report.FilesFinished == 1 && report.Errors.Count == 0 && File.Exists(source) &&
            File.Exists(copied) && new FileInfo(copied).Length == item.Length,
            "Чтение и копирование файла камеры не прошло: " + string.Join("; ", report.Errors));
        string originalHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(source)));
        string copiedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(copied)));
        Require(originalHash == copiedHash, "Содержимое копии отличается от файла камеры.");
        Console.WriteLine($"PASS: {item.Name}, {item.Length} байт, SHA-256 совпадает; исходник сохранён.");
    }
    finally
    {
        string full = Path.GetFullPath(testRoot);
        if (!full.StartsWith(workspace.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Тестовая папка находится вне рабочей области: " + full);
        Directory.Delete(full, true);
    }
    return;
}

if (args.Contains("--local-device-smoke"))
{
    string workspace = Path.GetFullPath(Environment.CurrentDirectory);
    string testRoot = Path.Combine(workspace, ".phonecopy-local-test-" + Guid.NewGuid().ToString("N"));
    string deviceFolder = Path.Combine(testRoot, "device");
    string pcFolder = Path.Combine(testRoot, "pc");
    string cameraFolder = Path.Combine(deviceFolder, "Camera");
    Directory.CreateDirectory(cameraFolder);
    Directory.CreateDirectory(pcFolder);
    var progress = new Progress<TransferProgress>(_ => { });
    try
    {
        string original = Path.Combine(cameraFolder, "clip.mp4");
        File.WriteAllText(original, "camera-video");
        var folderItem = new BrowserItem("Camera", cameraFolder, true, null, null);
        var copied = LocalTransferService.Copy(new[] { folderItem }, pcFolder, false, CancellationToken.None, progress);
        Require(copied.Copied == 1 && copied.FilesFinished == 1 && copied.Errors.Count == 0 &&
            File.ReadAllText(Path.Combine(pcFolder, "Camera", "clip.mp4")) == "camera-video" && File.Exists(original),
            "Копирование папки камеры завершилось неверно.");

        var skipped = LocalTransferService.Copy(new[] { folderItem }, pcFolder, false, CancellationToken.None, progress);
        Require(skipped.Skipped == 1 && skipped.FilesFinished == 1 && skipped.Errors.Count == 0,
            "Совпадающий файл не был пропущен.");

        string moving = Path.Combine(deviceFolder, "replace.mp4");
        string destination = Path.Combine(pcFolder, "replace.mp4");
        File.WriteAllText(moving, "new-video");
        File.WriteAllText(destination, "old-video");
        var moved = LocalTransferService.Move(new[] { new BrowserItem("replace.mp4", moving, false, 9, null) }, pcFolder,
            (_, _) => ConflictChoice.Replace, CancellationToken.None, progress);
        Require(moved.Copied == 1 && moved.FilesFinished == 1 && moved.Errors.Count == 0 &&
            !File.Exists(moving) && File.ReadAllText(destination) == "new-video",
            "Перенос с заменой завершился неверно.");

        var sameFolder = LocalTransferService.Move(new[] { new BrowserItem("replace.mp4", destination, false, 9, null) }, pcFolder,
            (_, _) => ConflictChoice.Replace, CancellationToken.None, progress);
        Require(sameFolder.Copied == 0 && sameFolder.Errors.Count == 1 && File.ReadAllText(destination) == "new-video",
            "Совпадение источника и назначения не было остановлено.");
        Console.WriteLine("PASS: USB-диск: копирование папки, пропуск, перенос с заменой, защита от совпадающих путей.");
    }
    finally
    {
        string full = Path.GetFullPath(testRoot);
        if (!full.StartsWith(workspace.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Тестовая папка находится вне рабочей области: " + full);
        Directory.Delete(full, true);
    }
    return;
}

if (args.Contains("--count-smoke"))
{
    string folder = Path.Combine(Path.GetTempPath(), "PhoneCopyCount-" + Guid.NewGuid().ToString("N"));
    string nested = Path.Combine(folder, "nested");
    Directory.CreateDirectory(nested);
    string first = Path.Combine(folder, "first.txt");
    string second = Path.Combine(nested, "second.txt");
    string third = Path.Combine(nested, "third.txt");
    try
    {
        File.WriteAllText(first, "one");
        File.WriteAllText(second, "two");
        File.WriteAllText(third, "three");
        var selected = new[] { new BrowserItem("test", folder, true, null, null) };
        Require(TransferCounter.CountPcFiles(selected, CancellationToken.None) == 3, "Вложенные файлы подсчитаны неверно.");
        var report = new TransferReport();
        var progress = new Progress<TransferProgress>(_ => { });
        TransferService.ReportFileFinished(progress, first, report);
        TransferService.ReportFileFinished(progress, second, report);
        Require(report.FilesFinished == 2, "Завершённые файлы подсчитаны неверно.");
        Console.WriteLine("PASS: три файла во вложенной папке; два завершённых файла.");
    }
    finally
    {
        File.Delete(first);
        File.Delete(second);
        File.Delete(third);
        Directory.Delete(nested);
        Directory.Delete(folder);
    }
    return;
}

if (args.Contains("--list-smoke"))
{
    var choices = await PhoneStore.FindAsync();
    foreach (var item in choices) Console.WriteLine(item.Name + " direct USB: " + item.Id.Contains("usb#vid_", StringComparison.OrdinalIgnoreCase));
    var choice = choices.First(x => x.Name.Contains("Armor 22", StringComparison.OrdinalIgnoreCase));
    Console.WriteLine("Found: " + choice.Name);
    var rootItems = await PhoneStore.ListAsync(choice, "\\");
    Console.WriteLine("Root items: " + rootItems.Length);
    var storage = rootItems.FirstOrDefault(x => x.IsDirectory);
    if (storage is not null)
    {
        var contents = await PhoneStore.ListAsync(choice, storage.Path);
        Console.WriteLine("Storage items: " + contents.Length);
    }
    Console.WriteLine("Root again: " + (await PhoneStore.ListAsync(choice, "\\")).Length);
    Console.WriteLine("Find again: " + (await PhoneStore.FindAsync()).Length);
    return;
}

if (!args.Contains("--phone-move-smoke"))
{
    Console.WriteLine("Укажите --count-smoke, --local-device-smoke или --phone-move-smoke. Последний тест создаёт и удаляет файл на телефоне.");
    return;
}

var device = MediaDevice.GetDevices().FirstOrDefault(d => d.FriendlyName.Contains("Armor 22", StringComparison.OrdinalIgnoreCase))
    ?? throw new Exception("Armor 22 не найден.");
using (device)
{
    device.Connect();
    string name = "phonecopy-move-test-" + Guid.NewGuid().ToString("N") + ".txt";
    string storage = device.GetRootDirectory().EnumerateDirectories().First().FullName;
    string folder = storage.TrimEnd('\\') + "\\Download";
    if (!device.DirectoryExists(folder)) throw new Exception("На телефоне нет папки Download для безопасного теста.");
    string remote = folder + "\\" + name;
    string localFolder = Path.Combine(Path.GetTempPath(), "PhoneCopyTests");
    Directory.CreateDirectory(localFolder);
    string local = Path.Combine(localFolder, name);
    string blockedFolder = Path.Combine(localFolder, "blocked-" + Guid.NewGuid().ToString("N"));
    byte[] payload = Encoding.UTF8.GetBytes("PhoneCopy move test: " + Guid.NewGuid().ToString("N"));
    string numbered = Path.Combine(localFolder, Path.GetFileNameWithoutExtension(name) + " (2)" + Path.GetExtension(name));
    var progress = new Progress<TransferProgress>(_ => { });
    try
    {
        File.WriteAllBytes(local, payload);
        var toPhone = MoveService.ToPhone(device,
            new[] { new BrowserItem(name, local, false, payload.Length, null) }, folder,
            (_, _) => throw new Exception("Неожиданный конфликт при отправке на телефон."), CancellationToken.None, progress);
        Require(toPhone.Copied == 1 && toPhone.Errors.Count == 0, "Перенос на телефон завершился с ошибкой: " + string.Join("; ", toPhone.Errors));
        Require(!File.Exists(local) && device.FileExists(remote), "После переноса на телефон исходный файл не удалён или целевой не найден.");

        File.WriteAllText(local, "old destination", Encoding.UTF8);
        var skipped = MoveService.ToPc(device,
            new[] { new BrowserItem(name, remote, false, payload.Length, null) }, localFolder,
            (_, _) => ConflictChoice.Skip, CancellationToken.None, progress);
        Require(skipped.Skipped == 1 && skipped.Copied == 0 && skipped.Errors.Count == 0, "Режим «Пропустить» работает неверно.");
        Require(device.FileExists(remote) && File.ReadAllText(local, Encoding.UTF8) == "old destination", "Пропуск изменил исходный или целевой файл.");

        var replaced = MoveService.ToPc(device,
            new[] { new BrowserItem(name, remote, false, payload.Length, null) }, localFolder,
            (_, _) => ConflictChoice.Replace, CancellationToken.None, progress);
        Require(replaced.Copied == 1 && replaced.Errors.Count == 0, "Замена при переносе на компьютер завершилась с ошибкой: " + string.Join("; ", replaced.Errors));
        Require(!device.FileExists(remote) && File.ReadAllBytes(local).SequenceEqual(payload), "После замены данные отличаются или источник остался на телефоне.");

        using (var oldPhoneFile = new MemoryStream(Encoding.UTF8.GetBytes("old phone destination")))
            device.UploadFile(oldPhoneFile, remote);
        byte[] newPayload = Encoding.UTF8.GetBytes("PhoneCopy replacement test: " + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(local, newPayload);
        var replacedOnPhone = MoveService.ToPhone(device,
            new[] { new BrowserItem(name, local, false, newPayload.Length, null) }, folder,
            (_, _) => ConflictChoice.Replace, CancellationToken.None, progress);
        Require(replacedOnPhone.Copied == 1 && replacedOnPhone.Errors.Count == 0, "Замена на телефоне завершилась с ошибкой: " + string.Join("; ", replacedOnPhone.Errors));
        using (var phoneRead = device.GetFileInfo(remote).OpenRead())
        using (var content = new MemoryStream())
        {
            phoneRead.CopyTo(content);
            Require(!File.Exists(local) && content.ToArray().SequenceEqual(newPayload), "Замена на телефоне не сохранила новое содержимое.");
        }

        File.WriteAllText(local, "keep original", Encoding.UTF8);
        var keptBoth = MoveService.ToPc(device,
            new[] { new BrowserItem(name, remote, false, newPayload.Length, null) }, localFolder,
            (_, _) => ConflictChoice.KeepBoth, CancellationToken.None, progress);
        Require(keptBoth.Copied == 1 && keptBoth.Errors.Count == 0, "Режим «Сохранить оба» завершился с ошибкой: " + string.Join("; ", keptBoth.Errors));
        Require(!device.FileExists(remote) && File.ReadAllText(local, Encoding.UTF8) == "keep original" && File.ReadAllBytes(numbered).SequenceEqual(newPayload),
            "Режим «Сохранить оба» не сохранил обе версии или не удалил источник.");

        using (var lastPhoneFile = new MemoryStream(payload)) device.UploadFile(lastPhoneFile, remote);
        File.WriteAllText(blockedFolder, "not a directory", Encoding.UTF8);
        var failedCopy = MoveService.ToPc(device,
            new[] { new BrowserItem(name, remote, false, payload.Length, null) }, blockedFolder,
            (_, _) => throw new Exception("Неожиданный конфликт в тесте ошибки копирования."), CancellationToken.None, progress);
        Require(failedCopy.Copied == 0 && failedCopy.Errors.Count == 1 && device.FileExists(remote),
            "При ошибке копирования исходный файл не должен удаляться.");
        Console.WriteLine("PASS: перенос в обе стороны; все варианты конфликта; исходник сохранён при ошибке копирования.");
    }
    finally
    {
        if (File.Exists(local)) File.Delete(local);
        if (File.Exists(numbered)) File.Delete(numbered);
        if (File.Exists(blockedFolder)) File.Delete(blockedFolder);
        try
        {
            if (device.FileExists(remote)) device.DeleteFile(remote);
            if (device.FileExists(remote)) Console.Error.WriteLine("Тестовый файл остался на телефоне: " + remote);
        }
        catch (Exception error) { Console.Error.WriteLine("Не удалось убрать тестовый файл " + remote + ": " + error.Message); }
        device.Disconnect();
    }
}

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
