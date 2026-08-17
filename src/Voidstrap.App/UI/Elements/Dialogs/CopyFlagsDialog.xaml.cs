using System.Collections.Generic;
using System.Windows;

namespace Voidstrap.UI.Elements.Dialogs;

public enum CopyFlagsFormat
{
    Json,
    GroupedJson,
    Base64,
}

public partial class CopyFlagsDialog
{
    private sealed class FormatOption
    {
        public CopyFlagsFormat Format { get; init; }
        public string Label { get; init; } = string.Empty;
        public override string ToString() => Label;
    }

    private static readonly List<FormatOption> Options = new()
    {
        new FormatOption { Format = CopyFlagsFormat.Json, Label = "JSON" },
        new FormatOption { Format = CopyFlagsFormat.GroupedJson, Label = "Grouped JSON" },
        new FormatOption { Format = CopyFlagsFormat.Base64, Label = "Base64" },
    };

    public CopyFlagsFormat SelectedFormat =>
        FormatBox.SelectedItem is FormatOption option ? option.Format : CopyFlagsFormat.Json;

    public CopyFlagsDialog(int flagCount)
    {
        InitializeComponent();
        FormatBox.ItemsSource = Options;
        FormatBox.SelectedIndex = 0;
        CountText.Text = flagCount == 1
            ? "1 flag will be copied to your clipboard."
            : flagCount + " flags will be copied to your clipboard.";
        CopyButton.IsEnabled = flagCount > 0;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
