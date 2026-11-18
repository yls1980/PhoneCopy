using System.Windows;
using System.Windows.Controls;

namespace PhoneCopy;

internal enum ConflictChoice { Replace, KeepBoth, Skip, Cancel }

public partial class ConflictDialog : Window
{
    internal ConflictChoice Choice { get; private set; } = ConflictChoice.Cancel;
    internal bool ApplyToAll => ApplyAllBox.IsChecked == true;

    public ConflictDialog(string source, string target)
    {
        InitializeComponent();
        SourceText.Text = "Источник: " + source;
        TargetText.Text = "Назначение: " + target;
    }

    private void Choice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string name } && Enum.TryParse(name, out ConflictChoice choice))
            Choice = choice;
        Close();
    }
}
