using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Voidstrap.Integrations;
using Voidstrap.Resources;
using Voidstrap.UI.ViewModels.Settings;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class DeploymentControllerPage : UiPage
{
    private bool _refreshingControllers;
    private CancellationTokenSource? _controllerPreviewCancellation;

    public DeploymentControllerPage(BehaviourViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e) => await RefreshControllersAsync();

    private void OnPageUnloaded(object sender, RoutedEventArgs e) => _controllerPreviewCancellation?.Cancel();

    private async void ControllerPreview_Click(object sender, RoutedEventArgs e)
    {
        if (_controllerPreviewCancellation is { } activePreview)
        {
            activePreview.Cancel();
            return;
        }

        using CancellationTokenSource preview = new();
        _controllerPreviewCancellation = preview;
        ControllerPreviewButton.SetBinding(ContentControl.ContentProperty, ControllerText(nameof(Strings.Ui_Controller_StopPreview)));
        SetControllerPreviewStatus(nameof(Strings.Ui_Controller_Previewing));
        try
        {
            DualSenseFeedback.PreviewResult result = await Task.Run(() =>
                DualSenseFeedback.PreviewAsync(() => DualSenseFeedback.ReadPresets(App.Settings.Prop), preview.Token));
            SetControllerPreviewStatus(result switch
            {
                DualSenseFeedback.PreviewResult.Stopped => nameof(Strings.Ui_Controller_Previewed),
                DualSenseFeedback.PreviewResult.NoPreset => nameof(Strings.Ui_Controller_SelectPreset),
                DualSenseFeedback.PreviewResult.NoController => nameof(Strings.Ui_Controller_NoController),
                _ => nameof(Strings.Ui_Controller_PreviewFailed)
            });
        }
        finally
        {
            _controllerPreviewCancellation = null;
            ControllerPreviewButton.SetBinding(ContentControl.ContentProperty, ControllerText(nameof(Strings.Ui_Controller_Preview)));
        }
    }

    private async void ControllerRefresh_Click(object sender, RoutedEventArgs e) => await RefreshControllersAsync();

    private async Task RefreshControllersAsync()
    {
        if (_refreshingControllers || DataContext is not BehaviourViewModel viewModel)
            return;
        _refreshingControllers = true;
        try
        {
            await viewModel.RefreshControllerDevicesAsync();
        }
        finally
        {
            _refreshingControllers = false;
        }
    }

    private void SetControllerPreviewStatus(string resourceKey)
    {
        ControllerPreviewStatus.SetBinding(TextBlock.TextProperty, ControllerText(resourceKey));
    }

    private static Binding ControllerText(string resourceKey) => new($"[{resourceKey}]") { Source = LocalizedStrings.Instance };
}
