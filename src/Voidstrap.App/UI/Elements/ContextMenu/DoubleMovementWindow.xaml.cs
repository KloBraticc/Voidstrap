using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Forms;
using System.Windows.Threading;
using Voidstrap.Models;
using Voidstrap.UI.ViewModels;
using Voidstrap.Utility;

namespace Voidstrap.UI.Elements.ContextMenu
{
    public partial class DoubleMovementWindow
    {
        private readonly DispatcherTimer _saveTimer;
        private bool _pendingSave;
        private int _capturingKeybindIndex = -1;

        public DoubleMovementWindow()
        {
            InitializeComponent();
            DataContext = new DoubleMovementViewModel(this);

            _saveTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(600)
            };
            _saveTimer.Tick += OnSaveTimerTick;

            ResetButton.Click += OnResetButtonClick;
            SaveButton.Click += OnSaveButtonClick;
            Closed += OnClosed;
            Loaded += OnLoaded;
            ViGEmHelper.OnProgressChanged += OnViGEmProgressChanged;
            PreviewKeyDown += OnWindowPreviewKeyDown;
        }

        private void OnViGEmProgressChanged(string title, bool show)
        {
            Dispatcher.Invoke(() =>
            {
                ViGEmProgressTitle.Text = title;
                ViGEmProgressOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            });
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;
            try
            {
                var result = await ViGEmHelper.EnsureViGEmBusInstalledAsync();
                if (DataContext is DoubleMovementViewModel vm)
                    vm.ViGEmIsReady = result;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("DoubleMovementWindow", ex);
            }
        }

        private void OnSaveButtonClick(object? sender, RoutedEventArgs e)
        {
            ForceSave();
            Close();
        }

        private void OnSaveTimerTick(object? sender, EventArgs e)
        {
            _saveTimer.Stop();
            if (!_pendingSave) return;
            _pendingSave = false;
            try
            {
                App.Settings.Save();
                StatusTextBlock.Text = $"Settings saved at {DateTime.Now:HH:mm:ss}";
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("DoubleMovementWindow", $"Save failed: {ex.Message}");
                StatusTextBlock.Text = "Save failed!";
            }
        }

        internal void ScheduleSave()
        {
            _pendingSave = true;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        internal void ForceSave()
        {
            _saveTimer.Stop();
            _pendingSave = false;
            try
            {
                if (DataContext is DoubleMovementViewModel vm)
                    vm.SyncKeybindsToSettings();
                App.Settings.Save();
                StatusTextBlock.Text = $"Settings saved at {DateTime.Now:HH:mm:ss}";
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("DoubleMovementWindow", $"Save failed: {ex.Message}");
                StatusTextBlock.Text = "Save failed!";
            }
        }

        private void OnResetButtonClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is DoubleMovementViewModel vm)
                vm.ResetToDefaults();
        }

        private void OnWindowPreviewKeyDown(object? sender, System.Windows.Input.KeyEventArgs e)
        {
            if (_capturingKeybindIndex >= 0 && DataContext is DoubleMovementViewModel vm)
            {
                e.Handled = true;

                if (e.Key == Key.Escape)
                {
                    _capturingKeybindIndex = -1;
                    KeybindListBox.Items.Refresh();
                    return;
                }

                var actualKey = e.Key == Key.System ? e.SystemKey : e.Key;
                int vk = KeyInterop.VirtualKeyFromKey(actualKey);
                string keyName = ((Keys)vk).ToString();

                if (_capturingKeybindIndex < vm.Keybinds.Count)
                {
                    vm.Keybinds[_capturingKeybindIndex].Key = keyName;
                    vm.MarkDirty();
                }

                _capturingKeybindIndex = -1;
                KeybindListBox.Items.Refresh();
            }
        }

        private void OnCaptureKeyClick(object? sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is DMKeybind kb
                && DataContext is DoubleMovementViewModel vm)
            {
                int idx = vm.Keybinds.IndexOf(kb);
                if (idx >= 0)
                {
                    _capturingKeybindIndex = idx;
                    if (sender is System.Windows.Controls.Button btn)
                        btn.Content = "...";
                }
            }
        }

        private void OnRemoveKeybindClick(object? sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is DMKeybind kb
                && DataContext is DoubleMovementViewModel vm)
            {
                vm.Keybinds.Remove(kb);
                vm.MarkDirty();
            }
        }

        private void OnAddKeybindClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is DoubleMovementViewModel vm)
            {
                vm.Keybinds.Add(new DMKeybind { Key = "None", Button = "A" });
                vm.MarkDirty();
            }
        }

        private void OnResetKeybindsClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is DoubleMovementViewModel vm)
                vm.ResetKeybindsToDefaults();
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            ResetButton.Click -= OnResetButtonClick;
            SaveButton.Click -= OnSaveButtonClick;
            PreviewKeyDown -= OnWindowPreviewKeyDown;
            Closed -= OnClosed;
            Loaded -= OnLoaded;
            ViGEmHelper.OnProgressChanged -= OnViGEmProgressChanged;
            _saveTimer.Tick -= OnSaveTimerTick;
            _saveTimer.Stop();
            if (_pendingSave)
            {
                try { App.Settings.Save(); }
                catch (Exception ex)
                {
                    App.Logger.WriteLine("DoubleMovementWindow", $"Final save failed: {ex.Message}");
                }
                _pendingSave = false;
            }
        }
    }

    public class DoubleMovementViewModel : NotifyPropertyChangedViewModel
    {
        private readonly DoubleMovementWindow _window;

        public DoubleMovementViewModel(DoubleMovementWindow window)
        {
            _window = window;
            _keybinds = new ObservableCollection<DMKeybind>();
            foreach (var kb in App.Settings.Prop.DoubleMovementKeybinds)
                _keybinds.Add(new DMKeybind { Key = kb.Key, Button = kb.Button });
        }

        private bool _viGEmIsReady;
        public bool ViGEmIsReady
        {
            get => _viGEmIsReady;
            set
            {
                if (_viGEmIsReady == value) return;
                _viGEmIsReady = value;
                OnPropertyChanged(nameof(ViGEmIsReady));
            }
        }

        private ObservableCollection<DMKeybind> _keybinds;
        public ObservableCollection<DMKeybind> Keybinds
        {
            get => _keybinds;
            set
            {
                _keybinds = value;
                OnPropertyChanged(nameof(Keybinds));
            }
        }

        public void MarkDirty()
        {
            _window.ScheduleSave();
        }

        public void ResetKeybindsToDefaults()
        {
            Keybinds.Clear();
            foreach (var kb in DefaultKeybinds())
                Keybinds.Add(new DMKeybind { Key = kb.Key, Button = kb.Button });
            SyncKeybindsToSettings();
            MarkDirty();
        }

        internal void SyncKeybindsToSettings()
        {
            App.Settings.Prop.DoubleMovementKeybinds.Clear();
            foreach (var kb in Keybinds)
                App.Settings.Prop.DoubleMovementKeybinds.Add(new DMKeybind { Key = kb.Key, Button = kb.Button });
        }

        private static List<DMKeybind> DefaultKeybinds() => new()
        {
            new() { Key = "Space", Button = "A" },
            new() { Key = "E", Button = "X" },
            new() { Key = "Q", Button = "Y" },
            new() { Key = "R", Button = "B" },
            new() { Key = "F", Button = "LeftThumb" },
            new() { Key = "C", Button = "RightThumb" },
            new() { Key = "Tab", Button = "Back" },
            new() { Key = "Escape", Button = "Start" },
            new() { Key = "LShiftKey", Button = "LeftShoulder" },
            new() { Key = "LControlKey", Button = "RightShoulder" },
            new() { Key = "D1", Button = "DPadUp" },
            new() { Key = "D2", Button = "DPadRight" },
            new() { Key = "D3", Button = "DPadDown" },
            new() { Key = "D4", Button = "DPadLeft" },
        };

        public bool DoubleMovementEnabled
        {
            get => App.Settings.Prop.DoubleMovementEnabled;
            set
            {
                App.Settings.Prop.DoubleMovementEnabled = value;
                OnPropertyChanged(nameof(DoubleMovementEnabled));
                App.Logger.WriteLine("DM", $"Enabled={value}");
                MarkDirty();
            }
        }

        public string DoubleMovementControllerType
        {
            get => App.Settings.Prop.DoubleMovementControllerType;
            set
            {
                App.Settings.Prop.DoubleMovementControllerType = value;
                OnPropertyChanged(nameof(DoubleMovementControllerType));
                App.Logger.WriteLine("DM", $"CtrlType={value}");
                MarkDirty();
            }
        }

        public int DoubleMovementSocdMode
        {
            get => (int)App.Settings.Prop.DoubleMovementSocdMode;
            set
            {
                App.Settings.Prop.DoubleMovementSocdMode = value;
                OnPropertyChanged(nameof(DoubleMovementSocdMode));
                MarkDirty();
            }
        }

        public double DoubleMovementForwardAngle
        {
            get => App.Settings.Prop.DoubleMovementForwardAngle;
            set
            {
                App.Settings.Prop.DoubleMovementForwardAngle = value;
                OnPropertyChanged(nameof(DoubleMovementForwardAngle));
                MarkDirty();
            }
        }

        public double DoubleMovementStrafeAngle
        {
            get => App.Settings.Prop.DoubleMovementStrafeAngle;
            set
            {
                App.Settings.Prop.DoubleMovementStrafeAngle = value;
                OnPropertyChanged(nameof(DoubleMovementStrafeAngle));
                MarkDirty();
            }
        }

        public double DoubleMovementSensitivity
        {
            get => App.Settings.Prop.DoubleMovementSensitivity;
            set
            {
                App.Settings.Prop.DoubleMovementSensitivity = value;
                OnPropertyChanged(nameof(DoubleMovementSensitivity));
                MarkDirty();
            }
        }

        public string DoubleMovementCurve
        {
            get => App.Settings.Prop.DoubleMovementCurve;
            set
            {
                App.Settings.Prop.DoubleMovementCurve = value;
                OnPropertyChanged(nameof(DoubleMovementCurve));
                App.Logger.WriteLine("DM", $"Curve={value}");
                MarkDirty();
            }
        }

        public string DMMouseLeft
        {
            get => App.Settings.Prop.DMMouseLeft;
            set { App.Settings.Prop.DMMouseLeft = value; OnPropertyChanged(nameof(DMMouseLeft)); MarkDirty(); }
        }

        public string DMMouseRight
        {
            get => App.Settings.Prop.DMMouseRight;
            set { App.Settings.Prop.DMMouseRight = value; OnPropertyChanged(nameof(DMMouseRight)); MarkDirty(); }
        }

        public string DMMouseMiddle
        {
            get => App.Settings.Prop.DMMouseMiddle;
            set { App.Settings.Prop.DMMouseMiddle = value; OnPropertyChanged(nameof(DMMouseMiddle)); MarkDirty(); }
        }

        public string DMMouseX1
        {
            get => App.Settings.Prop.DMMouseX1;
            set { App.Settings.Prop.DMMouseX1 = value; OnPropertyChanged(nameof(DMMouseX1)); MarkDirty(); }
        }

        public string DMMouseX2
        {
            get => App.Settings.Prop.DMMouseX2;
            set { App.Settings.Prop.DMMouseX2 = value; OnPropertyChanged(nameof(DMMouseX2)); MarkDirty(); }
        }

        public string DMScrollUp
        {
            get => App.Settings.Prop.DMScrollUp;
            set { App.Settings.Prop.DMScrollUp = value; OnPropertyChanged(nameof(DMScrollUp)); MarkDirty(); }
        }

        public string DMScrollDown
        {
            get => App.Settings.Prop.DMScrollDown;
            set { App.Settings.Prop.DMScrollDown = value; OnPropertyChanged(nameof(DMScrollDown)); MarkDirty(); }
        }

        public double DMMouseSensitivity
        {
            get => App.Settings.Prop.DMMouseSensitivity;
            set
            {
                App.Settings.Prop.DMMouseSensitivity = value;
                OnPropertyChanged(nameof(DMMouseSensitivity));
                MarkDirty();
            }
        }

        public void ResetToDefaults()
        {
            App.Settings.Prop.DoubleMovementEnabled = false;
            App.Settings.Prop.DoubleMovementControllerType = "Xbox360";
            App.Settings.Prop.DoubleMovementSocdMode = 0;
            App.Settings.Prop.DoubleMovementForwardAngle = 55.0;
            App.Settings.Prop.DoubleMovementStrafeAngle = 55.0;
            App.Settings.Prop.DoubleMovementSensitivity = 1.0;
            App.Settings.Prop.DoubleMovementCurve = "Linear";
            App.Settings.Prop.DMMouseLeft = "A";
            App.Settings.Prop.DMMouseRight = "B";
            App.Settings.Prop.DMMouseMiddle = "";
            App.Settings.Prop.DMMouseX1 = "";
            App.Settings.Prop.DMMouseX2 = "";
            App.Settings.Prop.DMScrollUp = "DPadUp";
            App.Settings.Prop.DMScrollDown = "DPadDown";
            App.Settings.Prop.DMMouseSensitivity = 3.0;
            ResetKeybindsToDefaults();
            _window.ForceSave();
            OnPropertyChanged(nameof(DoubleMovementEnabled));
            OnPropertyChanged(nameof(DoubleMovementControllerType));
            OnPropertyChanged(nameof(DoubleMovementSocdMode));
            OnPropertyChanged(nameof(DoubleMovementForwardAngle));
            OnPropertyChanged(nameof(DoubleMovementStrafeAngle));
            OnPropertyChanged(nameof(DoubleMovementSensitivity));
            OnPropertyChanged(nameof(DoubleMovementCurve));
            OnPropertyChanged(nameof(DMMouseLeft));
            OnPropertyChanged(nameof(DMMouseRight));
            OnPropertyChanged(nameof(DMMouseMiddle));
            OnPropertyChanged(nameof(DMMouseX1));
            OnPropertyChanged(nameof(DMMouseX2));
            OnPropertyChanged(nameof(DMScrollUp));
            OnPropertyChanged(nameof(DMScrollDown));
            OnPropertyChanged(nameof(DMMouseSensitivity));
            App.Logger.WriteLine("DM", "Reset to defaults");
        }
    }
}
