using Windows.Win32;
using Voidstrap.UI.Elements.Bootstrapper.Base;
using Voidstrap.UI.ViewModels.Bootstrapper;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using System.Windows.Shell;
using System.Windows.Threading;
using Voidstrap;
using System.Windows;

namespace Voidstrap.UI.Elements.Bootstrapper
{
    /// <summary>
    /// Interaction logic for CustomDialog.xaml
    /// </summary>
    public partial class CustomDialog : IBootstrapperDialog
    {
        private readonly BootstrapperDialogViewModel _viewModel;
        private Window? _mainWindow;
        public Voidstrap.Bootstrapper? Bootstrapper { get; set; }

        private bool _isClosing;
        private bool _isClosed;

        #region UI Elements
        public string Message
        {
            get => _viewModel.Message;
            set
            {
                _viewModel.Message = value;
                _viewModel.OnPropertyChanged(nameof(_viewModel.Message));
            }
        }

        public ProgressBarStyle ProgressStyle
        {
            get => _viewModel.ProgressIndeterminate ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
            set
            {
                _viewModel.ProgressIndeterminate = (value == ProgressBarStyle.Marquee);
                _viewModel.OnPropertyChanged(nameof(_viewModel.ProgressIndeterminate));
            }
        }

        public int ProgressMaximum
        {
            get => _viewModel.ProgressMaximum;
            set
            {
                _viewModel.ProgressMaximum = value;
                _viewModel.OnPropertyChanged(nameof(_viewModel.ProgressMaximum));
            }
        }

        public int ProgressValue
        {
            get => _viewModel.ProgressValue;
            set
            {
                _viewModel.ProgressValue = value;
                _viewModel.OnPropertyChanged(nameof(_viewModel.ProgressValue));
            }
        }

        public TaskbarItemProgressState TaskbarProgressState
        {
            get => _viewModel.TaskbarProgressState;
            set
            {
                _viewModel.TaskbarProgressState = value;
                _viewModel.OnPropertyChanged(nameof(_viewModel.TaskbarProgressState));
            }
        }

        public double TaskbarProgressValue
        {
            get => _viewModel.TaskbarProgressValue;
            set
            {
                _viewModel.TaskbarProgressValue = value;
                _viewModel.OnPropertyChanged(nameof(_viewModel.TaskbarProgressValue));
            }
        }

        public Action? CancelCallback { get; set; }

        public bool CancelEnabled
        {
            get => _viewModel.CancelEnabled;
            set
            {
                _viewModel.CancelEnabled = value;

                _viewModel.OnPropertyChanged(nameof(_viewModel.CancelButtonVisibility));
                _viewModel.OnPropertyChanged(nameof(_viewModel.CancelEnabled));
            }
        }
        #endregion

        public CustomDialog() : this(false)
        {
        }

        internal CustomDialog(bool isDesignPreview)
        {
            IsDesignPreview = isDesignPreview;
            InitializeComponent();
            if (!IsDesignPreview)
            {
                _mainWindow = System.Windows.Application.Current.Windows
                    .OfType<Voidstrap.UI.Elements.Settings.MainWindow>()
                    .FirstOrDefault();
                if (App.Settings.Prop.BackgroundWindow)
                    _mainWindow?.Hide();

                Voidstrap.UI.Elements.Bootstrapper.AudioPlayerHelper.PlayStartupAudio();
            }
            Closed += OnDialogClosed;
            _viewModel = new BootstrapperDialogViewModel(this);
            DataContext = _viewModel;
            Title = App.Settings.Prop.BootstrapperTitle;
            Icon = Voidstrap.Extensions.IconEx.GetBootstrapperWindowIcon();
        }

        internal bool IsDesignPreview { get; }

        private void OnDialogClosed(object? sender, EventArgs e)
        {
            Closed -= OnDialogClosed;
            _isClosed = true;
            DisposeWebPanels();

            if (IsDesignPreview)
                return;

            _mainWindow = System.Windows.Application.Current.Windows
                .OfType<Voidstrap.UI.Elements.Settings.MainWindow>()
                .FirstOrDefault();
            if (App.Settings.Prop.BackgroundWindow)
                _mainWindow?.Show();

            Voidstrap.UI.Elements.Bootstrapper.AudioPlayerHelper.StopAudio();
        }

        private void UiWindow_Closing(object sender, CancelEventArgs e)
        {
            if (IsDesignPreview)
            {
                return;
            }
            if (!_isClosing)
            {
                try { CancelCallback?.Invoke(); } catch { }
                Bootstrapper?.Cancel();
            }
        }

        #region IBootstrapperDialog Methods
        public void ShowBootstrapper() => this.ShowDialog();

        public void CloseBootstrapper()
        {
            _isClosing = true;
            Dispatcher.BeginInvoke(this.Close);
        }

        public void ShowSuccess(string message, Action? callback) => BaseFunctions.ShowSuccess(message, callback);
        #endregion

        #region Web panels
        private readonly HashSet<Microsoft.Web.WebView2.Wpf.WebView2> _webPanels = new();
        private readonly CancellationTokenSource _webPanelLifetime = new();
        private int _webPanelUpdatePending;
        private int _webPanelUpdateRunning;

        internal CancellationToken WebPanelLifetimeToken => _webPanelLifetime.Token;

        internal bool WebPanelLifetimeEnded => _isClosed || _webPanelLifetime.IsCancellationRequested;

        internal void RegisterWebPanel(Microsoft.Web.WebView2.Wpf.WebView2 view)
        {
            var core = view.CoreWebView2;
            if (core == null || WebPanelLifetimeEnded || !_webPanels.Add(view))
                return;

            if (_webPanels.Count == 1)
                _viewModel.PropertyChanged += OnWebPanelStateChanged;

            core.WebMessageReceived += OnWebPanelMessage;
            core.NavigationCompleted += OnWebPanelNavigated;
        }

        internal void UnregisterWebPanel(Microsoft.Web.WebView2.Wpf.WebView2 view)
        {
            var core = view.CoreWebView2;
            if (core != null)
            {
                core.WebMessageReceived -= OnWebPanelMessage;
                core.NavigationCompleted -= OnWebPanelNavigated;
            }

            bool removed = _webPanels.Remove(view);

            if (removed && _webPanels.Count == 0)
                _viewModel.PropertyChanged -= OnWebPanelStateChanged;
        }

        private void OnWebPanelMessage(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
        {
            string message = string.Empty;

            try
            {
                message = e.TryGetWebMessageAsString() ?? string.Empty;
            }
            catch
            {
                try { message = (e.WebMessageAsJson ?? string.Empty).Trim('"'); } catch { }
            }

            if (message == "voidstrap:drag")
            {
                Dispatcher.BeginInvoke(new Action(DragFromWebPanel));
                return;
            }

            if (message != "voidstrap:cancel")
            {
                App.Logger.WriteLine("CustomDialog::OnWebPanelMessage", "Ignored a panel message");
                return;
            }

            App.Logger.WriteLine("CustomDialog::OnWebPanelMessage", "The panel asked to cancel");
            Dispatcher.BeginInvoke(new Action(CancelFromWebPanel));
        }

        internal bool AllowWebPanelDrag { get; set; }

        private void DragFromWebPanel()
        {
            if (!AllowWebPanelDrag || this.WindowState == System.Windows.WindowState.Maximized)
                return;

            try
            {
                IntPtr handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (handle == IntPtr.Zero)
                    return;

                PInvoke.ReleaseCapture();
                PInvoke.SendMessage(new Windows.Win32.Foundation.HWND(handle), WmNcLeftButtonDown, HtCaption, 0);
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("CustomDialog::DragFromWebPanel", ex.Message);
            }
        }

        private const uint WmNcLeftButtonDown = 0x00A1;

        private const int HtCaption = 2;

        private void CancelFromWebPanel()
        {
            if (!_viewModel.CancelEnabled)
            {
                App.Logger.WriteLine("CustomDialog::CancelFromWebPanel", "Cancelling is not available yet, ignoring");
                return;
            }

            var command = _viewModel.CancelInstallCommand;
            if (command != null && command.CanExecute(null))
                command.Execute(null);
        }

        private void OnWebPanelNavigated(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
        {
            QueueWebPanelState();
        }

        private void OnWebPanelStateChanged(object? sender, PropertyChangedEventArgs e)
        {
            QueueWebPanelState();
        }

        private void QueueWebPanelState()
        {
            if (WebPanelLifetimeEnded)
                return;

            if (!Dispatcher.CheckAccess())
            {
                if (!Dispatcher.HasShutdownStarted && Interlocked.Exchange(ref _webPanelUpdatePending, 1) == 0)
                    Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(QueueWebPanelState));
                return;
            }

            Interlocked.Exchange(ref _webPanelUpdatePending, 0);
            Interlocked.Exchange(ref _webPanelUpdatePending, 1);

            if (Interlocked.Exchange(ref _webPanelUpdateRunning, 1) == 0)
                _ = PushWebPanelStateAsync(_webPanelLifetime.Token);
        }

        private async Task PushWebPanelStateAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested && Interlocked.Exchange(ref _webPanelUpdatePending, 0) != 0)
                {
                    if (_webPanels.Count == 0)
                        return;

                    string payload = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        status = _viewModel.Message ?? string.Empty,
                        progress = _viewModel.ProgressValue,
                        max = _viewModel.ProgressMaximum,
                        indeterminate = _viewModel.ProgressIndeterminate,
                        cancelEnabled = _viewModel.CancelEnabled
                    });

                    string script = "window.voidstrap && window.voidstrap.__apply(" + payload + ");";
                    Task<string>[] updates = _webPanels
                        .Select(view => view.CoreWebView2)
                        .Where(core => core != null)
                        .Select(core => core!.ExecuteScriptAsync(script))
                        .ToArray();

                    if (updates.Length > 0)
                        await Task.WhenAll(updates);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !token.IsCancellationRequested)
            {
                App.Logger.WriteLine("CustomDialog::PushWebPanelState", ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _webPanelUpdateRunning, 0);
                if (!token.IsCancellationRequested && Volatile.Read(ref _webPanelUpdatePending) != 0)
                    QueueWebPanelState();
            }
        }

        private void DisposeWebPanels()
        {
            if (!_webPanelLifetime.IsCancellationRequested)
                _webPanelLifetime.Cancel();

            _viewModel.PropertyChanged -= OnWebPanelStateChanged;

            foreach (Microsoft.Web.WebView2.Wpf.WebView2 view in _webPanels.ToArray())
                CleanupWebPanel(view);

            _webPanels.Clear();
            _webPanelLifetime.Dispose();
        }
        #endregion
    }
}
