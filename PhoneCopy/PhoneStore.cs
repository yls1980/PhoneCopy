using MediaDevices;
using System.Collections.Concurrent;

namespace PhoneCopy;

internal sealed record PhoneChoice(string Id, string Name, bool IsAdb, bool IsDrive = false);

internal static class PhoneStore
{
    private static readonly BlockingCollection<Action> workQueue = new();

    static PhoneStore()
    {
        var worker = new Thread(() =>
        {
            foreach (var work in workQueue.GetConsumingEnumerable()) work();
        }) { IsBackground = true, Name = "PhoneCopy MTP" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    public static Task<T> RunStaAsync<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        workQueue.Add(() =>
        {
            try { completion.SetResult(action()); }
            catch (Exception error) { completion.SetException(error); }
        });
        return completion.Task;
    }

    public static async Task<PhoneChoice[]> FindAsync()
    {
        var mtp = await RunStaAsync(() =>
        {
            var found = new List<PhoneChoice>();
            foreach (var device in MediaDevice.GetDevices())
            {
                using (device)
                    found.Add(new PhoneChoice(device.DeviceId,
                        "[MTP] " + (string.IsNullOrWhiteSpace(device.FriendlyName) ? device.Description : device.FriendlyName), false));
            }
            return found.ToArray();
        });
        var adb = await AdbStore.FindAsync();
        var drives = new List<PhoneChoice>();
        var mountedLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady && !string.IsNullOrWhiteSpace(drive.VolumeLabel)) mountedLabels.Add(drive.VolumeLabel);
                if (drive.IsReady && drive.DriveType == DriveType.Removable)
                    drives.Add(new PhoneChoice(drive.RootDirectory.FullName,
                        "[USB-диск] " + drive.Name + (string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "" : " — " + drive.VolumeLabel), false, true));
            }
            catch { /* A removable drive can disappear during enumeration. */ }
        }
        return drives.OrderByDescending(choice => HasCameraFiles(choice.Id)).ThenByDescending(choice => UsedBytes(choice.Id))
            .Concat(mtp.Where(choice => !choice.Id.Contains("wpdbusenum", StringComparison.OrdinalIgnoreCase) ||
                !mountedLabels.Contains(choice.Name["[MTP] ".Length..]))
                .OrderByDescending(choice => choice.Id.Contains("usb#vid_", StringComparison.OrdinalIgnoreCase)))
            .Concat(adb).ToArray();
    }

    private static bool HasCameraFiles(string root)
    {
        try
        {
            string dcim = Path.Combine(root, "DCIM");
            return Directory.Exists(dcim) && Directory.EnumerateFileSystemEntries(dcim).Any();
        }
        catch { return false; }
    }

    private static long UsedBytes(string root)
    {
        try
        {
            var drive = new DriveInfo(root);
            return drive.TotalSize - drive.AvailableFreeSpace;
        }
        catch { return 0; }
    }

    public static T WithDevice<T>(string id, Func<MediaDevice, T> work)
    {
        var devices = MediaDevice.GetDevices().ToArray();
        MediaDevice? selected = devices.FirstOrDefault(d => d.DeviceId == id);
        foreach (var other in devices.Where(d => !ReferenceEquals(d, selected))) other.Dispose();
        using var device = selected ?? throw new IOException("Телефон отключён. Проверьте USB-кабель и режим передачи файлов.");
        device.Connect();
        try { return work(device); }
        finally { device.Disconnect(); }
    }

    public static Task<BrowserItem[]> ListAsync(PhoneChoice choice, string path) => choice.IsDrive
        ? Task.Run(() => PcStore.List(path))
        : choice.IsAdb
        ? Task.Run(() => AdbStore.ListAsync(choice.Id, path))
        : RunStaAsync(() => WithDevice(choice.Id, device => Entries(device, path)));

    public static BrowserItem[] Entries(MediaDevice device, string path)
    {
        MediaDirectoryInfo folder = path == "\\" ? device.GetRootDirectory() : device.GetDirectoryInfo(path);
        return folder.EnumerateFileSystemInfos().Select(entry =>
        {
            bool directory = entry is MediaDirectoryInfo;
            long? length = null;
            DateTime? modified = null;
            if (!directory) { try { ulong size = entry.Length; length = size <= long.MaxValue ? (long)size : null; } catch { /* Some MTP objects omit size. */ } }
            try { modified = entry.LastWriteTime; } catch { /* Optional MTP metadata. */ }
            return new BrowserItem(entry.Name, entry.FullName, directory, length, modified);
        }).ToArray();
    }
}
