using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Voidstrap.UI.Elements.Controls;

public partial class BootstrapperImportControl : UserControl
{
    private CancellationTokenSource? _importCts;

    private System.Windows.Controls.Button? _importButton;
    public bool StageOnly { get; set; }
    internal Voidstrap.Utility.BootstrapperImportPlan? PendingPlan { get; private set; }
    public event EventHandler? Imported;

    public BootstrapperImportControl()
    {
        InitializeComponent();
        if (Voidstrap.Utility.Platform.IsLinux)
            Visibility = Visibility.Collapsed;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _importCts?.Cancel();
    }

    private void ApplyFrom_Click(object sender, RoutedEventArgs e)
    {
        if (_importCts != null)
            return;
        _importButton = sender as System.Windows.Controls.Button;
        if (Window.GetWindow(this) is Voidstrap.UI.Elements.Settings.MainWindow main)
        {
            main.ShowBootstrapperImport(OnSourceChosen);
            return;
        }
        ShowStandaloneChooser();
    }

    private void ShowStandaloneChooser()
    {
        Voidstrap.UI.Elements.Dialogs.BootstrapperImportDialog view = new();
        Wpf.Ui.Controls.UiWindow window = new()
        {
            Title = Strings.Menu_Import_Title,
            Width = 560,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            ExtendsContentIntoTitleBar = true,
            Owner = Window.GetWindow(this),
        };
        window.SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        window.Content = DialogChrome.Host(DialogChrome.TitleBar(Strings.Menu_Import_Title), view);
        string? chosen = null;
        void OnChosen(object? s, string source)
        {
            chosen = source;
            window.Close();
        }
        void OnClose(object? s, EventArgs args) => window.Close();
        view.SourceChosen += OnChosen;
        view.CloseRequested += OnClose;
        window.ShowOwnedDialog();
        view.SourceChosen -= OnChosen;
        view.CloseRequested -= OnClose;
        view.Release();
        if (chosen != null)
            OnSourceChosen(chosen);
    }

    private async void OnSourceChosen(string source)
    {
        if (_importCts != null)
            return;
        using CancellationTokenSource cts = new();
        _importCts = cts;
        if (_importButton != null)
            _importButton.IsEnabled = false;
        Window? owner = Window.GetWindow(this);
        bool ownerEnabled = owner?.IsEnabled == true;
        bool ownerDisabled = false;
        try
        {
            string? settingsPath = await Voidstrap.Utility.BootstrapperSettingsImport.FindSettingsAsync(source, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (settingsPath == null)
            {
                var picker = new Microsoft.Win32.OpenFileDialog
                {
                    Title = string.Format(Strings.Menu_Import_PickFile, source),
                    Filter = "Settings JSON (*.json)|*.json",
                    FileName = "Settings.json",
                    CheckFileExists = true,
                    Multiselect = false,
                };
                if (picker.ShowDialog(Window.GetWindow(this)) != true)
                    return;
                settingsPath = await Voidstrap.Utility.BootstrapperSettingsImport.ResolvePickedPathAsync(picker.FileName);
            }

            if (owner != null && ownerEnabled)
            {
                ownerDisabled = true;
                owner.IsEnabled = false;
            }
            var plan = await Voidstrap.Utility.BootstrapperSettingsImport.ReadAsync(source, settingsPath, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            string previousLocale = App.Settings.Prop.Locale;
            if (StageOnly)
            {
                PendingPlan = plan;
            }
            else
            {
                App.Settings.FlushDeferred();
                await Task.Run(() => Voidstrap.Utility.BootstrapperSettingsImport.Apply(plan, App.Settings, App.FastFlags), cts.Token);
                if (cts.IsCancellationRequested)
                    return;
                Imported?.Invoke(this, EventArgs.Empty);
            }
            if (ownerDisabled)
            {
                ownerDisabled = false;
                owner!.IsEnabled = true;
            }
            string summary = string.Format(Strings.Menu_Import_Done, plan.Settings.Count, plan.Flags?.Count ?? 0, source, plan.Skipped);
            Frontend.ShowMessageBox(StageOnly ? summary + "\n\n" + Strings.Menu_Import_Staged : summary);
            if (!StageOnly)
                RefreshLive(owner, previousLocale);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (ownerDisabled)
                owner!.IsEnabled = true;
            App.Logger.WriteException("BootstrapperImport::ApplyFrom", ex);
            if (!cts.IsCancellationRequested)
                Frontend.ShowMessageBox(string.Format(Strings.Menu_Import_Failed, ex.Message), MessageBoxImage.Warning);
        }
        finally
        {
            if (ownerDisabled)
                owner!.IsEnabled = true;
            _importCts = null;
            if (_importButton != null)
                _importButton.IsEnabled = true;
        }
    }

    private static void RefreshLive(Window? owner, string previousLocale)
    {
        const string LOG_IDENT = "BootstrapperImportControl::RefreshLive";
        try
        {
            if (!string.Equals(previousLocale, App.Settings.Prop.Locale, StringComparison.Ordinal))
                Locale.Set(App.Settings.Prop.Locale);
            Voidstrap.UI.Elements.Base.WpfUiWindow.InvalidateCustomTheme();
            Voidstrap.UI.WindowBackdrop.ApplyThemeToAllOpenWindows();
            if (owner is Voidstrap.UI.Elements.Settings.MainWindow main)
                main.ApplyTheme();
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "Live refresh failed: " + ex.Message);
        }
    }
}
