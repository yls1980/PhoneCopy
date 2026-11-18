using MediaDevices;

// Read-only diagnostic for devices exposed through Windows Portable Devices.
string? inspectedPath = null;
int inspectIndex = Array.IndexOf(args, "--inspect");
if (inspectIndex >= 0)
{
    if (inspectIndex + 1 >= args.Length) throw new ArgumentException("Укажите путь файла после --inspect.");
    inspectedPath = args[inspectIndex + 1];
}

foreach (var device in MediaDevice.GetDevices())
{
    using (device)
    {
        string name = string.IsNullOrWhiteSpace(device.FriendlyName) ? device.Description : device.FriendlyName;
        Console.Write(name + ": ");
        try
        {
            device.Connect();
            var roots = device.GetRootDirectory().EnumerateFileSystemInfos().ToArray();
            Console.WriteLine(roots.Length + " visible storage roots");
            if (inspectedPath is null || !device.FileExists(inspectedPath)) continue;

            var file = device.GetFileInfo(inspectedPath);
            Console.WriteLine($"Inspect: {file.FullName}; length={file.Length}; modified={file.LastWriteTime:O}");
            using var input = file.OpenRead();
            byte[] header = new byte[16];
            int read = input.Read(header, 0, header.Length);
            Console.WriteLine($"Read {read} bytes; header={Convert.ToHexString(header.AsSpan(0, read))}");
        }
        catch (Exception error)
        {
            Console.WriteLine($"{error.GetType().Name} (0x{error.HResult:X8}): {error.Message}");
        }
        finally { if (device.IsConnected) device.Disconnect(); }
    }
}
