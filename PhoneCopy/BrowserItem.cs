namespace PhoneCopy;

internal sealed record BrowserItem(string Name, string Path, bool IsDirectory, long? Length, DateTime? Modified)
{
    public string DisplayName => IsDirectory ? "📁 " + Name : Name;
    public string DisplaySize => IsDirectory || Length is null ? "" : FormatSize(Length.Value);
    public string DisplayDate => Modified is { Year: > 1900 } date ? date.ToString("dd.MM.yyyy HH:mm") : "";

    private static string FormatSize(long size)
    {
        string[] units = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
        double value = size;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{size} Б" : $"{value:0.##} {units[unit]}";
    }
}
