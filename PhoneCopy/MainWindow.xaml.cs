using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PhoneCopy;

public partial class MainWindow : Window
{
    private string phonePath = "\\";
    private string? pcPath;
    private BrowserItem[] phoneItems = Array.Empty<BrowserItem>();
    private BrowserItem[] pcItems = Array.Empty<BrowserItem>();
    private string phoneSort = "Имя";
    private string pcSort = "Имя";
    private bool phoneDescending;
    private bool pcDescending;
    private bool busy;
    private int phoneBrowseVersion;
    private int pcBrowseVersion;
    private CancellationTokenSource? transferCancellation;
    private bool currentOperationMove;
    private long? totalFiles;
    private long filesFinished;
    private long filesCopied;

    public MainWindow() => InitializeComponent();

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        await BrowsePcAsync(Directory.Exists(pictures) ? pictures : null);
        await FindDevicesAsync();
    }

    private async Task FindDevicesAsync()
    {
        if (busy) return;
        SetBusy(true);
        StatusText.Text = "Поиск USB-устройств…";
        try
        {
            PhoneChoice? previous = DevicesBox.SelectedItem as PhoneChoice;
            var devices = await PhoneStore.FindAsync();
            if (previous is { IsDrive: true } && Directory.Exists(previous.Id) &&
                !devices.Any(device => device.Id.Equals(previous.Id, StringComparison.OrdinalIgnoreCase)))
                devices = devices.Append(previous).ToArray();
            DevicesBox.ItemsSource = devices;
            if (devices.Length > 0)
                DevicesBox.SelectedItem = devices.FirstOrDefault(device => device.Id == previous?.Id) ?? devices[0];
            else
            {
                phoneItems = Array.Empty<BrowserItem>();
                PhoneList.ItemsSource = phoneItems;
                PhonePathText.Text = "Устройство не найдено";
                StatusText.Text = "USB-накопитель не найден. Проверьте подключение и доступ к файлам, затем нажмите «Найти устройства» или «Выбрать диск/папку».";
            }
        }
        catch (Exception error) { ShowError(error); }
        finally { SetBusy(false); }
    }

    private async Task BrowsePhoneAsync(string path, bool updateStatus = true, bool clearErrors = true)
    {
        if (DevicesBox.SelectedItem is not PhoneChoice choice || busy && transferCancellation is not null) return;
        int version = ++phoneBrowseVersion;
        PhonePathText.Text = path + "  •  загрузка…";
        try
        {
            var entries = await PhoneStore.ListAsync(choice, path);
            if (version != phoneBrowseVersion) return;
            phonePath = path;
            phoneItems = entries;
            PhonePathText.Text = path;
            RenderPhone();
            if (updateStatus)
                StatusText.Text = path == "\\" && entries.Length == 0 && !choice.IsDrive
                    ? "Устройство подключено, но хранилище не открыто через MTP. Проверьте режим передачи файлов и нажмите «Найти устройства»."
                    : $"Устройство: {entries.Length} элементов";
            if (clearErrors) ErrorsText.Text = "";
        }
        catch (Exception error)
        {
            if (version == phoneBrowseVersion) { PhonePathText.Text = phonePath; ShowError(error); }
        }
    }

    private async Task BrowsePcAsync(string? path)
    {
        int version = ++pcBrowseVersion;
        PcPathText.Text = (path ?? "Этот компьютер") + "  •  загрузка…";
        try
        {
            var entries = await Task.Run(() => PcStore.List(path));
            if (version != pcBrowseVersion) return;
            pcPath = path;
            pcItems = entries;
            PcPathText.Text = path ?? "Этот компьютер";
            RenderPc();
        }
        catch (Exception error)
        {
            if (version == pcBrowseVersion) { PcPathText.Text = pcPath ?? "Этот компьютер"; ShowError(error); }
        }
    }

    private static IEnumerable<BrowserItem> Sorted(IEnumerable<BrowserItem> items, string key, bool descending)
    {
        var ordered = items.OrderBy(item => !item.IsDirectory);
        return key switch
        {
            "Размер" => descending ? ordered.ThenByDescending(item => item.Length ?? -1).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                                  : ordered.ThenBy(item => item.Length ?? -1).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase),
            "Изменён" => descending ? ordered.ThenByDescending(item => item.Modified ?? DateTime.MinValue).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                                   : ordered.ThenBy(item => item.Modified ?? DateTime.MinValue).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => descending ? ordered.ThenByDescending(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                            : ordered.ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
        };
    }

    private void RenderPhone() => PhoneList.ItemsSource = Sorted(phoneItems, phoneSort, phoneDescending).ToArray();
    private void RenderPc() => PcList.ItemsSource = Sorted(pcItems, pcSort, pcDescending).ToArray();

    private static string SelectionSummary(System.Windows.Controls.ListView list)
    {
        int files = 0;
        int folders = 0;
        foreach (BrowserItem item in list.SelectedItems)
            if (item.IsDirectory) folders++;
            else files++;
        return $"Выбрано файлов: {files:N0}" + (folders > 0 ? $"  •  папок: {folders:N0}" : "");
    }

    private void PhoneList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PhoneSelectionText is not null) PhoneSelectionText.Text = SelectionSummary(PhoneList);
    }

    private void PcList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PcSelectionText is not null) PcSelectionText.Text = SelectionSummary(PcList);
    }

    private void SetBusy(bool value)
    {
        busy = value;
        DevicesBox.IsEnabled = !value;
        FindButton.IsEnabled = !value;
        CameraFolderButton.IsEnabled = !value;
        PhoneList.IsEnabled = !value;
        PcList.IsEnabled = !value;
        PhoneUpButton.IsEnabled = !value;
        PcUpButton.IsEnabled = !value;
        PcChooseButton.IsEnabled = !value;
        ToPcButton.IsEnabled = !value;
        ToPhoneButton.IsEnabled = !value;
        MoveToPcButton.IsEnabled = !value;
        MoveToPhoneButton.IsEnabled = !value;
        ConflictBox.IsEnabled = !value;
        CancelButton.IsEnabled = value && transferCancellation is not null;
    }

    private void ShowError(Exception error)
    {
        ErrorsText.Text = error is System.Runtime.InteropServices.COMException { ErrorCode: unchecked((int)0x800700AA) }
            ? "MTP: ресурс устройства занят (0x800700AA). Проверьте режим передачи файлов, переподключите кабель и нажмите «Найти устройства»."
            : error.Message;
        StatusText.Text = "Ошибка";
    }

    private async void FindButton_Click(object sender, RoutedEventArgs e) => await FindDevicesAsync();

    private void CameraFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "Диск или папка камеры, видимая в Проводнике Windows" };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        string path = Path.GetFullPath(dialog.SelectedPath);
        var choice = new PhoneChoice(path, "[Диск/папка] " + path, false, true);
        var current = (DevicesBox.ItemsSource as IEnumerable<PhoneChoice> ?? Array.Empty<PhoneChoice>())
            .Where(item => !item.Id.Equals(path, StringComparison.OrdinalIgnoreCase)).Append(choice).ToArray();
        DevicesBox.ItemsSource = current;
        DevicesBox.SelectedItem = choice;
    }

    private async void DevicesBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DevicesBox.SelectedItem is PhoneChoice choice)
            await BrowsePhoneAsync(choice.IsDrive ? choice.Id : choice.IsAdb ? "/sdcard" : "\\");
    }

    private async void PhoneUpButton_Click(object sender, RoutedEventArgs e)
    {
        if (DevicesBox.SelectedItem is not PhoneChoice choice) return;
        if (choice.IsDrive)
        {
            string driveRoot = Path.GetFullPath(choice.Id).TrimEnd(Path.DirectorySeparatorChar);
            string current = Path.GetFullPath(phonePath).TrimEnd(Path.DirectorySeparatorChar);
            if (current.Equals(driveRoot, StringComparison.OrdinalIgnoreCase)) return;
            string? parent = Directory.GetParent(phonePath)?.FullName;
            string normalizedParent = parent is null ? "" : Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar);
            await BrowsePhoneAsync(normalizedParent.Equals(driveRoot, StringComparison.OrdinalIgnoreCase) ||
                normalizedParent.StartsWith(driveRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                ? parent! : choice.Id);
            return;
        }
        string root = choice.IsAdb ? "/sdcard" : "\\";
        if (phonePath == root) return;
        char separator = choice.IsAdb ? '/' : '\\';
        string trimmed = phonePath.TrimEnd(separator);
        int last = trimmed.LastIndexOf(separator);
        await BrowsePhoneAsync(last <= 0 || choice.IsAdb && last < root.Length ? root : trimmed[..last]);
    }

    private async void PcUpButton_Click(object sender, RoutedEventArgs e)
    {
        if (pcPath is null) return;
        await BrowsePcAsync(Directory.GetParent(pcPath)?.FullName);
    }

    private async void PcChooseButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "Папка на компьютере" };
        if (pcPath is not null) dialog.SelectedPath = pcPath;
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) await BrowsePcAsync(dialog.SelectedPath);
    }

    private async void PhoneList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!busy && PhoneList.SelectedItem is BrowserItem { IsDirectory: true } item) await BrowsePhoneAsync(item.Path);
    }

    private async void PcList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!busy && PcList.SelectedItem is BrowserItem { IsDirectory: true } item) await BrowsePcAsync(item.Path);
    }

    private async void PhoneList_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Back) { PhoneUpButton_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.Enter && PhoneList.SelectedItem is BrowserItem { IsDirectory: true } item)
        { await BrowsePhoneAsync(item.Path); e.Handled = true; }
    }

    private async void PcList_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Back) { PcUpButton_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.Enter && PcList.SelectedItem is BrowserItem { IsDirectory: true } item)
        { await BrowsePcAsync(item.Path); e.Handled = true; }
    }

    private void PhoneHeader_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header || header.Content is not string key) return;
        phoneDescending = phoneSort == key && !phoneDescending;
        phoneSort = key;
        RenderPhone();
    }

    private void PcHeader_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header || header.Content is not string key) return;
        pcDescending = pcSort == key && !pcDescending;
        pcSort = key;
        RenderPc();
    }

    private async void ToPcButton_Click(object sender, RoutedEventArgs e)
    {
        if (DevicesBox.SelectedItem is not PhoneChoice choice) { ShowError(new IOException("Сначала выберите устройство.")); return; }
        if (pcPath is null) { ShowError(new IOException("Откройте папку назначения на компьютере.")); return; }
        var selected = PhoneList.SelectedItems.Cast<BrowserItem>().ToArray();
        if (selected.Length == 0) { ShowError(new IOException("Выберите файлы или папки слева.")); return; }
        string destination = pcPath;
        bool number = ConflictBox.SelectedIndex == 1;
        var progress = new Progress<TransferProgress>(ShowProgress);
        await TransferAsync(token => CountFilesAsync(choice, selected, true, token), () => choice.IsDrive
            ? Task.Run(() => LocalTransferService.Copy(selected, destination, number, transferCancellation!.Token, progress))
            : choice.IsAdb
                ? Task.Run(() => AdbTransferService.ToPcAsync(choice.Id, selected, destination, number, transferCancellation!.Token, progress))
                : PhoneStore.RunStaAsync(() => PhoneStore.WithDevice(choice.Id, device =>
                    TransferService.ToPc(device, selected, destination, number, transferCancellation!.Token, progress))), choice.IsAdb);
        await BrowsePcAsync(pcPath);
    }

    private async void ToPhoneButton_Click(object sender, RoutedEventArgs e)
    {
        if (DevicesBox.SelectedItem is not PhoneChoice choice) { ShowError(new IOException("Сначала выберите устройство.")); return; }
        if (!choice.IsAdb && !choice.IsDrive && phonePath == "\\") { ShowError(new IOException("Откройте хранилище устройства перед копированием.")); return; }
        var selected = PcList.SelectedItems.Cast<BrowserItem>().ToArray();
        if (selected.Length == 0) { ShowError(new IOException("Выберите файлы или папки справа.")); return; }
        string destination = phonePath;
        bool number = ConflictBox.SelectedIndex == 1;
        var progress = new Progress<TransferProgress>(ShowProgress);
        await TransferAsync(token => CountFilesAsync(choice, selected, false, token), () => choice.IsDrive
            ? Task.Run(() => LocalTransferService.Copy(selected, destination, number, transferCancellation!.Token, progress))
            : choice.IsAdb
                ? Task.Run(() => AdbTransferService.ToPhoneAsync(choice.Id, selected, destination, number, transferCancellation!.Token, progress))
                : PhoneStore.RunStaAsync(() => PhoneStore.WithDevice(choice.Id, device =>
                    TransferService.ToPhone(device, selected, destination, number, transferCancellation!.Token, progress))), choice.IsAdb);
        await BrowsePhoneAsync(phonePath, false, false);
    }

    private Func<string, string, ConflictChoice> CreateMoveConflictResolver()
    {
        ConflictChoice? remembered = null;
        return (source, target) => Dispatcher.Invoke(() =>
        {
            if (remembered is { } answer) return answer;
            var dialog = new ConflictDialog(source, target) { Owner = this };
            dialog.ShowDialog();
            if (dialog.ApplyToAll && dialog.Choice != ConflictChoice.Cancel) remembered = dialog.Choice;
            return dialog.Choice;
        });
    }

    private async void MoveToPcButton_Click(object sender, RoutedEventArgs e)
    {
        if (DevicesBox.SelectedItem is not PhoneChoice choice) { ShowError(new IOException("Сначала выберите устройство.")); return; }
        if (pcPath is null) { ShowError(new IOException("Откройте папку назначения на компьютере.")); return; }
        var selected = PhoneList.SelectedItems.Cast<BrowserItem>().ToArray();
        if (selected.Length == 0) { ShowError(new IOException("Выберите файлы или папки слева.")); return; }
        string destination = pcPath;
        var resolve = CreateMoveConflictResolver();
        var progress = new Progress<TransferProgress>(ShowProgress);
        await TransferAsync(token => CountFilesAsync(choice, selected, true, token), () => choice.IsDrive
            ? Task.Run(() => LocalTransferService.Move(selected, destination, resolve, transferCancellation!.Token, progress))
            : choice.IsAdb
                ? Task.Run(() => AdbMoveService.ToPcAsync(choice.Id, selected, destination, resolve, transferCancellation!.Token, progress))
                : PhoneStore.RunStaAsync(() => PhoneStore.WithDevice(choice.Id, device =>
                    MoveService.ToPc(device, selected, destination, resolve, transferCancellation!.Token, progress))), choice.IsAdb, true);
        await BrowsePhoneAsync(phonePath, false, false);
        await BrowsePcAsync(pcPath);
    }

    private async void MoveToPhoneButton_Click(object sender, RoutedEventArgs e)
    {
        if (DevicesBox.SelectedItem is not PhoneChoice choice) { ShowError(new IOException("Сначала выберите устройство.")); return; }
        if (!choice.IsAdb && !choice.IsDrive && phonePath == "\\") { ShowError(new IOException("Откройте хранилище устройства перед переносом.")); return; }
        var selected = PcList.SelectedItems.Cast<BrowserItem>().ToArray();
        if (selected.Length == 0) { ShowError(new IOException("Выберите файлы или папки справа.")); return; }
        string destination = phonePath;
        var resolve = CreateMoveConflictResolver();
        var progress = new Progress<TransferProgress>(ShowProgress);
        await TransferAsync(token => CountFilesAsync(choice, selected, false, token), () => choice.IsDrive
            ? Task.Run(() => LocalTransferService.Move(selected, destination, resolve, transferCancellation!.Token, progress))
            : choice.IsAdb
                ? Task.Run(() => AdbMoveService.ToPhoneAsync(choice.Id, selected, destination, resolve, transferCancellation!.Token, progress))
                : PhoneStore.RunStaAsync(() => PhoneStore.WithDevice(choice.Id, device =>
                    MoveService.ToPhone(device, selected, destination, resolve, transferCancellation!.Token, progress))), choice.IsAdb, true);
        await BrowsePcAsync(pcPath);
        await BrowsePhoneAsync(phonePath, false, false);
    }

    private static Task<long> CountFilesAsync(PhoneChoice choice, BrowserItem[] selected, bool fromPhone, CancellationToken token)
    {
        if (selected.All(item => !item.IsDirectory)) return Task.FromResult((long)selected.Length);
        if (fromPhone && choice.IsDrive) return Task.Run(() => TransferCounter.CountPcFiles(selected, token), token);
        if (!fromPhone) return Task.Run(() => TransferCounter.CountPcFiles(selected, token), token);
        if (choice.IsAdb) return TransferCounter.CountAdbFilesAsync(choice.Id, selected, token);
        return PhoneStore.RunStaAsync(() => PhoneStore.WithDevice(choice.Id, device =>
            TransferCounter.CountMtpFiles(device, selected, token)));
    }

    private void UpdateRemainingText()
    {
        string verb = currentOperationMove ? "Перенесено" : "Скопировано";
        FilesRemainingText.Text = totalFiles is long total
            ? $"{verb} файлов: {filesCopied:N0} из {total:N0}  •  обработано: {filesFinished:N0}  •  осталось в очереди: {Math.Max(0, total - filesFinished):N0}"
            : $"{verb} файлов: {filesCopied:N0}  •  обработано: {filesFinished:N0}  •  осталось в очереди: неизвестно";
    }

    private async Task TransferAsync(Func<CancellationToken, Task<long>> countFiles, Func<Task<TransferReport>> transfer, bool adb, bool move = false)
    {
        if (busy) return;
        transferCancellation = new CancellationTokenSource();
        currentOperationMove = move;
        totalFiles = null;
        filesFinished = 0;
        filesCopied = 0;
        SetBusy(true);
        ErrorsText.Text = "";
        FileProgress.Value = 0;
        FileProgress.IsIndeterminate = true;
        FilesRemainingText.Visibility = Visibility.Visible;
        FilesRemainingText.Text = "Подсчёт выбранных файлов…";
        StatusText.Text = "Подсчёт файлов…";
        try
        {
            try { totalFiles = await countFiles(transferCancellation.Token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { ErrorsText.Text = "Не удалось подсчитать файлы: " + error.Message; }
            UpdateRemainingText();
            FileProgress.IsIndeterminate = adb;
            StatusText.Text = move ? "Перенос…" : "Копирование…";
            var report = await transfer();
            filesFinished = Math.Max(filesFinished, report.FilesFinished);
            filesCopied = Math.Max(filesCopied, report.FilesCopied);
            UpdateRemainingText();
            StatusText.Text = $"{(report.Errors.Count == 0 ? "Готово" : "Завершено с ошибками")}: {(move ? "перенесено" : "скопировано")} файлов {report.FilesCopied:N0}" +
                              (totalFiles is long total ? $" из {total:N0}" : "") +
                              $", пропущено {report.Skipped:N0}, ошибок {report.Errors.Count:N0}.";
            string countWarning = totalFiles is null ? ErrorsText.Text : "";
            ErrorsText.Text = countWarning + (countWarning.Length > 0 && report.Errors.Count > 0 ? Environment.NewLine : "") + string.Join(Environment.NewLine, report.Errors.Take(3));
            if (report.Errors.Count > 0)
            {
                try
                {
                    string logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhoneCopy", "Logs");
                    Directory.CreateDirectory(logDirectory);
                    string logPath = Path.Combine(logDirectory, "errors-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
                    File.WriteAllLines(logPath, report.Errors);
                    ErrorsText.Text += "\nПолный список: " + logPath;
                }
                catch { if (report.Errors.Count > 3) ErrorsText.Text += $"\nИ ещё {report.Errors.Count - 3} ошибок."; }
            }
            FileProgress.IsIndeterminate = false;
            FileProgress.Value = report.Errors.Count == 0 ? 100 : 0;
        }
        catch (OperationCanceledException) { StatusText.Text = move ? "Перенос отменён. Уже завершённые файлы сохранены." : "Копирование отменено. Уже скопированные файлы сохранены."; }
        catch (Exception error) { ShowError(error); }
        finally
        {
            FileProgress.IsIndeterminate = false;
            transferCancellation.Dispose();
            transferCancellation = null;
            currentOperationMove = false;
            SetBusy(false);
        }
    }

    private void ShowProgress(TransferProgress p)
    {
        if (transferCancellation is null) return;
        if (p.FileFinished)
        {
            filesFinished = Math.Max(filesFinished, p.FilesFinished);
            filesCopied = Math.Max(filesCopied, p.FilesCopied);
            UpdateRemainingText();
            return;
        }
        string copiedCount = totalFiles is long totalFilesValue ? $"{filesCopied:N0} из {totalFilesValue:N0}" : $"{filesCopied:N0}";
        StatusText.Text = $"{p.Name} — {p.Done:N0}" + (p.Total is long total ? $" / {total:N0} Б" : " Б") +
                          $"  •  {(currentOperationMove ? "перенесено" : "скопировано")} файлов {copiedCount}, пропущено {p.Skipped:N0}";
        FileProgress.Value = p.Total is > 0 ? Math.Min(100, 100.0 * p.Done / p.Total.Value) : 0;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        transferCancellation?.Cancel();
        CancelButton.IsEnabled = false;
        StatusText.Text = "Отмена… ожидаем завершения текущей USB-операции.";
    }
}
