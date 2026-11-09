using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Voidstrap.UI.Elements.Dialogs;

public partial class BootstrapperImportDialog : UserControl
{
    private readonly CancellationTokenSource _cancel = new();

    public event EventHandler? CloseRequested;

    public event EventHandler<string>? SourceChosen;

    public BootstrapperImportDialog()
    {
        InitializeComponent();
        BloxstrapTitle.Text = string.Format(Strings.Menu_Import_From, "Bloxstrap");
        FishstrapTitle.Text = string.Format(Strings.Menu_Import_From, "Fishstrap");
        BloxstrapStatus.Text = Strings.Menu_Import_Checking;
        FishstrapStatus.Text = Strings.Menu_Import_Checking;
        _ = DetectAsync("Bloxstrap", BloxstrapStatus);
        _ = DetectAsync("Fishstrap", FishstrapStatus);
    }

    public void Release()
    {
        _cancel.Cancel();
        _cancel.Dispose();
    }

    private async Task DetectAsync(string source, TextBlock status)
    {
        const string LOG_IDENT = "BootstrapperImportDialog::DetectAsync";
        try
        {
            string? path = await Voidstrap.Utility.BootstrapperSettingsImport.FindSettingsAsync(source, _cancel.Token);
            status.Text = path != null ? Strings.Menu_Import_Found : Strings.Menu_Import_NotFound;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, source + ": " + ex.Message);
            status.Text = Strings.Menu_Import_NotFound;
        }
    }

    private void Source_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string source })
            SourceChosen?.Invoke(this, source);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
