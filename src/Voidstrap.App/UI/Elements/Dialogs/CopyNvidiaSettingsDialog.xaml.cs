using System.Collections.Generic;
using System.Windows;

namespace Voidstrap.UI.Elements.Dialogs;

public enum CopyNvidiaSettingsFormat
{
    NipProfile,
    SettingRows,
    Base64Nip,
}

public partial class CopyNvidiaSettingsDialog
{
    private sealed class FormatOption
    {
        public CopyNvidiaSettingsFormat Format { get; init; }
        public string Label { get; init; } = string.Empty;
        public override string ToString() => Label;
    }

    private static readonly List<FormatOption> Options = new()
    {
        new FormatOption { Format = CopyNvidiaSettingsFormat.NipProfile, Label = "NIP profile" },
        new FormatOption { Format = CopyNvidiaSettingsFormat.SettingRows, Label = "Setting rows" },
        new FormatOption { Format = CopyNvidiaSettingsFormat.Base64Nip, Label = "Base64 NIP" },
    };

    public CopyNvidiaSettingsFormat SelectedFormat =>
        FormatBox.SelectedItem is FormatOption option ? option.Format : CopyNvidiaSettingsFormat.NipProfile;

    public CopyNvidiaSettingsDialog(int flagCount)
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
