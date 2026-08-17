using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Voidstrap.Integrations.Overlays;
using Voidstrap.Utility;

namespace Voidstrap.Integrations.GameChat
{
    public class GameChatProfileWindow : Window
    {
        private static readonly System.Windows.Media.FontFamily UiFont = new System.Windows.Media.FontFamily("Segoe UI");
        private static readonly Color CardColor = Color.FromRgb(24, 24, 27);
        private static readonly Brush SubtleBrush = Freeze(Color.FromRgb(150, 150, 158));
        private static readonly System.Windows.Media.Effects.DropShadowEffect CardShadow = CreateShadow();
        private static readonly Regex GradientRe = new Regex(@"linear-gradient\((\d{1,3})deg,\s*(#[0-9a-fA-F]{6}),\s*(#[0-9a-fA-F]{6})\)", RegexOptions.Compiled);

        private const double AvatarSize = 120;
        private const double AvatarContainer = 150;

        private readonly long _robloxId;
        private readonly Border _band;
        private readonly Grid _avatarWrap;
        private readonly System.Windows.Shapes.Ellipse _avatar;
        private readonly System.Windows.Controls.Image _borderImage;
        private readonly TextBlock _displayName;
        private readonly TextBlock _username;
        private readonly WrapPanel _badgesPanel;
        private readonly TextBlock _status;
        private readonly TextBlock _counts;
        private readonly TextBlock _about;
        private readonly TextBlock _wearingHeader;
        private readonly WrapPanel _wearingPanel;
        private readonly ScrollViewer _wearingScroll;
        private readonly Button _addFriendBtn;
        private readonly Button _viewBtn;
        private readonly Button? _reportBtn;
        private readonly Button _closeBtn;
        private readonly TextBlock _result;
        private readonly Func<Task<GameChatBugResult>>? _reporter;
        private readonly CancellationTokenSource _lifetimeCts = new();
        private bool _addBusy;
        private bool _reportBusy;
		private bool _closed;
		private IntPtr _windowHandle;

        private static Brush Freeze(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private static System.Windows.Media.Effects.DropShadowEffect CreateShadow()
        {
            var effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 0, Opacity = 0.6, Color = Colors.Black };
            effect.Freeze();
            return effect;
        }

        public GameChatProfileWindow(long robloxId, Func<Task<GameChatBugResult>>? reporter = null)
        {
            _robloxId = robloxId;
            _reporter = reporter;

            WindowStyle = WindowStyle.None;
			AllowsTransparency = false;
			Background = Freeze(CardColor);
            ShowInTaskbar = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.Manual;
            Width = 420;
            Height = 660;
            WindowStartupLocation = WindowStartupLocation.Manual;
            SourceInitialized += OnSourceInitialized;

            _band = new Border
            {
                Height = 110,
                CornerRadius = new CornerRadius(10, 10, 0, 0),
                Background = new LinearGradientBrush(Color.FromRgb(58, 92, 160), Color.FromRgb(38, 40, 48), 90),
                VerticalAlignment = VerticalAlignment.Top,
            };

            _closeBtn = new Button
            {
                Content = "✕",
                Width = 30,
                Height = 30,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)),
                BorderThickness = new Thickness(0),
                FontSize = 14,
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 14, 14, 0),
                Template = BuildRoundButtonTemplate(),
            };
            _closeBtn.Click += OnCloseClick;

            _avatar = new System.Windows.Shapes.Ellipse
            {
                Width = AvatarSize,
                Height = AvatarSize,
                Stroke = Freeze(CardColor),
                StrokeThickness = 5,
                Fill = new ImageBrush { Stretch = Stretch.UniformToFill },
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _borderImage = new System.Windows.Controls.Image
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };
            _avatarWrap = new Grid
            {
                Width = AvatarContainer,
                Height = AvatarContainer,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 45, 0, 0),
            };
            _avatarWrap.Children.Add(_avatar);
            _avatarWrap.Children.Add(_borderImage);

            _displayName = MakeText(20, Brushes.White, FontWeights.Bold);
            _displayName.Text = "Loading...";
            _username = MakeText(13, SubtleBrush, FontWeights.Normal);
            _badgesPanel = new WrapPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(16, 5, 16, 0),
                Visibility = Visibility.Collapsed,
            };
            _status = MakeText(12.5, Freeze(Color.FromRgb(180, 190, 210)), FontWeights.Normal);
            _status.Margin = new Thickness(24, 8, 24, 0);
            _counts = MakeText(12, SubtleBrush, FontWeights.SemiBold);
            _counts.Margin = new Thickness(0, 8, 0, 0);

            _about = new TextBlock
            {
                FontFamily = UiFont,
                FontSize = 12.5,
                Foreground = Freeze(Color.FromRgb(210, 210, 216)),
                TextWrapping = TextWrapping.Wrap,
            };
            var aboutScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 110,
                Margin = new Thickness(20, 14, 20, 0),
                Content = _about,
            };

            _wearingHeader = MakeText(12, SubtleBrush, FontWeights.SemiBold);
            _wearingHeader.Text = "Wearing";
            _wearingHeader.Margin = new Thickness(16, 14, 16, 0);
            _wearingHeader.Visibility = Visibility.Collapsed;
            _wearingPanel = new WrapPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(12, 6, 12, 0),
            };
            _wearingScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 128,
                Content = _wearingPanel,
                Visibility = Visibility.Collapsed,
            };

            _addFriendBtn = MakeButton("Add Friend", Color.FromRgb(70, 120, 220));
            _addFriendBtn.Click += OnAddFriendClick;
            _viewBtn = MakeButton("View Profile", Color.FromRgb(55, 55, 62));
            _viewBtn.Click += OnViewClick;

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 16, 0, 0),
            };
            buttons.Children.Add(_addFriendBtn);
            buttons.Children.Add(_viewBtn);

            if (_reporter != null)
            {
                _reportBtn = MakeButton("Report", Color.FromRgb(188, 62, 62));
                _reportBtn.Click += OnReportClick;
                buttons.Children.Add(_reportBtn);
            }

            _result = MakeText(11.5, SubtleBrush, FontWeights.Normal);
            _result.Margin = new Thickness(20, 10, 20, 0);

            var content = new StackPanel();
            content.Children.Add(_avatarWrap);
            content.Children.Add(_displayName);
            content.Children.Add(_username);
            content.Children.Add(_badgesPanel);
            content.Children.Add(_status);
            content.Children.Add(_counts);
            content.Children.Add(aboutScroll);
            content.Children.Add(_wearingHeader);
            content.Children.Add(_wearingScroll);
            content.Children.Add(buttons);
            content.Children.Add(_result);

            var grid = new Grid();
            grid.Children.Add(_band);
            grid.Children.Add(content);
            grid.Children.Add(_closeBtn);

            Content = new Border
            {
                Background = new SolidColorBrush(CardColor),
                CornerRadius = new CornerRadius(10),
                Effect = CardShadow,
                Child = grid,
            };

            KeyDown += OnKeyDownHandler;
            _ = LoadInitialContentAsync();
        }

        private async Task LoadInitialContentAsync()
        {
            try
            {
                await Task.WhenAll(LoadAsync(), LoadWearingAsync()).WaitAsync(_lifetimeCts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("GameChatProfileWindow::Load", ex);
            }
        }

        private static ControlTemplate BuildRoundButtonTemplate()
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);
            return new ControlTemplate(typeof(Button)) { VisualTree = border };
        }

        private async Task LoadWearingAsync()
        {
            var items = await GameChatRoblox.GetWearingAsync(_robloxId, _lifetimeCts.Token);
			if (_closed || _lifetimeCts.IsCancellationRequested || items.Count == 0)
                return;
            foreach (var item in items)
            {
                var tile = new Border
                {
                    Width = 52,
                    Height = 52,
                    Margin = new Thickness(3),
                    CornerRadius = new CornerRadius(6),
                    Background = new SolidColorBrush(Color.FromRgb(34, 34, 38)),
                    ToolTip = string.IsNullOrEmpty(item.Name) ? item.Id.ToString() : item.Name,
                };
                if (item.Image != null)
                {
                    tile.Child = new System.Windows.Controls.Image
                    {
                        Source = item.Image,
                        Stretch = Stretch.Uniform,
                        Margin = new Thickness(3),
                    };
                }
                _wearingPanel.Children.Add(tile);
            }
            _wearingHeader.Text = "Wearing (" + items.Count + ")";
            _wearingHeader.Visibility = Visibility.Visible;
            _wearingScroll.Visibility = Visibility.Visible;
        }

        private TextBlock MakeText(double size, Brush brush, FontWeight weight)
        {
            return new TextBlock
            {
                FontFamily = UiFont,
                FontSize = size,
                FontWeight = weight,
                Foreground = brush,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(16, 2, 16, 0),
            };
        }

        private Button MakeButton(string text, Color bg)
        {
            return new Button
            {
                Content = text,
                FontFamily = UiFont,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(bg),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(16, 8, 16, 8),
                Margin = new Thickness(5, 0, 5, 0),
                Cursor = Cursors.Hand,
            };
        }

        private async Task LoadAsync()
        {
            var data = await GameChatRoblox.GetVoidstrapProfileAsync(_robloxId, _lifetimeCts.Token);
			if (_closed || _lifetimeCts.IsCancellationRequested)
				return;
            if (data == null || !data.Exists)
            {
                _displayName.Text = "No Voidstrap profile";
                _username.Text = "";
                _about.Text = "This user has not set up a Voidstrap profile.";
                _addFriendBtn.IsEnabled = false;
                return;
            }

            _displayName.Text = string.IsNullOrEmpty(data.DisplayName) ? data.Username : data.DisplayName;
            _username.Text = string.IsNullOrEmpty(data.Username) ? "" : "@" + data.Username;
            _status.Text = string.IsNullOrWhiteSpace(data.Status) ? "" : data.Status;
            _counts.Text = data.Friends + " friends   •   " + data.Followers + " followers";
            _about.Text = string.IsNullOrWhiteSpace(data.About) ? "This user has no bio." : data.About;
            LoadBadges(data.Badges);
            ApplyFriendState(data);

            Task<ImageSource?> bannerTask = string.IsNullOrEmpty(data.BannerUrl)
                ? Task.FromResult<ImageSource?>(null)
				: LoadBannerAsync(data.BannerUrl, _lifetimeCts.Token);
            var (avatar, border) = await Task.Run(() => ResolveVisuals(data), _lifetimeCts.Token);
            ImageSource? banner = await bannerTask.WaitAsync(_lifetimeCts.Token);
			if (_closed || _lifetimeCts.IsCancellationRequested)
				return;

            if (avatar != null && _avatar.Fill is ImageBrush brush)
                brush.ImageSource = avatar;

            if (!string.IsNullOrEmpty(data.AvatarBorderCss))
            {
                var ring = GradientProfileBorder.ParseBorder(data.AvatarBorderCss);
                if (ring != null)
                    _avatar.Stroke = ring;
            }

            if (banner != null)
                _band.Background = new ImageBrush(banner) { Stretch = Stretch.UniformToFill };
            else
                ApplyGradient(data);

            if (border?.Image != null)
            {
                _borderImage.Source = border.Image;
                _borderImage.Width = border.Width;
                _borderImage.Height = border.Height;
                _borderImage.Margin = border.Margin;
                Panel.SetZIndex(_borderImage, border.ZIndex);
                Panel.SetZIndex(_avatar, 0);
                _borderImage.Visibility = Visibility.Visible;
            }
        }

        private void LoadBadges(IReadOnlyList<GameChatBadge> badges)
        {
            _badgesPanel.Children.Clear();
            foreach (var badge in badges)
            {
                if (string.IsNullOrWhiteSpace(badge.Name) || string.IsNullOrWhiteSpace(badge.Image))
                    continue;
                var image = new System.Windows.Controls.Image
                {
                    Width = 20,
                    Height = 20,
                    Stretch = Stretch.Uniform,
                    ToolTip = badge.Name,
                    SnapsToDevicePixels = true,
                };
                _badgesPanel.Children.Add(new Border
                {
                    Width = 24,
                    Height = 24,
                    Margin = new Thickness(2, 0, 2, 0),
                    Padding = new Thickness(2),
                    CornerRadius = new CornerRadius(5),
                    Background = Freeze(Color.FromArgb(36, 255, 255, 255)),
                    ToolTip = badge.Name,
                    Child = image,
                });
                _ = LoadBadgeAsync(new WeakReference<System.Windows.Controls.Image>(image), badge.Image, _lifetimeCts.Token);
            }
            _badgesPanel.Visibility = _badgesPanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private async Task LoadBadgeAsync(WeakReference<System.Windows.Controls.Image> imageReference, string value, CancellationToken token)
        {
            try
            {
                ImageSource? source;
                if (value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) && value.Length <= 2800000)
                {
                    source = await Task.Run(() =>
                    {
                        int comma = value.IndexOf(',');
                        if (comma < 0)
                            return null;
                        byte[] bytes = Convert.FromBase64String(value.Substring(comma + 1));
                        return bytes.Length <= 2 * 1024 * 1024 ? SafeImaging.FromBytes(bytes, 40) : null;
                    }, token);
                }
                else
                {
                    string url = value;
                    if (url.StartsWith("/", StringComparison.Ordinal))
                        url = App.WebsiteBaseUrl.TrimEnd('/') + url;
                    if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || !Uri.TryCreate(App.WebsiteBaseUrl, UriKind.Absolute, out Uri? site) || uri.Scheme != site.Scheme || !string.Equals(uri.Host, site.Host, StringComparison.OrdinalIgnoreCase) || uri.Port != site.Port)
                        return;
                    source = await AppImage.LoadAsync(uri.AbsoluteUri, 40, token);
                }
                if (!_closed && !token.IsCancellationRequested && source != null && imageReference.TryGetTarget(out var image))
                    image.Source = source;
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
            }
        }

		private static async Task<ImageSource?> LoadBannerAsync(string value, CancellationToken token)
        {
            string url = value.StartsWith("/", StringComparison.Ordinal)
                ? App.WebsiteBaseUrl.TrimEnd('/') + value
                : value;
			return await GradientWebsite.LoadBannerImageAsync(url, token).ConfigureAwait(false);
        }

        private (ImageSource? Avatar, BorderRender? Border) ResolveVisuals(GameChatVoidstrapProfile data)
        {
            ImageSource? avatar = string.IsNullOrEmpty(data.AvatarUrl) ? null : WebsiteBorderRenderer.LoadSecureImage(data.AvatarUrl);
            BorderRender? border = null;
            if (!string.IsNullOrEmpty(data.EquippedBorderJson))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(data.EquippedBorderJson);
                    border = WebsiteBorderRenderer.Build(doc.RootElement, AvatarSize, AvatarContainer);
                }
                catch
                {
                }
            }
            return (avatar, border);
        }

        private void ApplyGradient(GameChatVoidstrapProfile data)
        {
            var match = GradientRe.Match(data.GradientCss ?? "");
            if (!match.Success || !double.TryParse(match.Groups[1].Value, out double angle))
                return;
            try
            {
                var c1 = (Color)ColorConverter.ConvertFromString(match.Groups[2].Value);
                var c2 = (Color)ColorConverter.ConvertFromString(match.Groups[3].Value);
                _band.Background = new LinearGradientBrush(c1, c2, angle);
            }
            catch
            {
            }
        }

        private void ApplyFriendState(GameChatVoidstrapProfile data)
        {
            if (data.Self)
            {
                _addFriendBtn.Content = "This is you";
                _addFriendBtn.IsEnabled = false;
            }
            else if (data.IsFriend)
            {
                _addFriendBtn.Content = "Friends";
                _addFriendBtn.IsEnabled = false;
            }
            else if (data.RequestSent)
            {
                _addFriendBtn.Content = "Requested";
                _addFriendBtn.IsEnabled = false;
            }
        }

        private async void OnAddFriendClick(object sender, RoutedEventArgs e)
        {
            if (_addBusy)
                return;
            _addBusy = true;
            _addFriendBtn.IsEnabled = false;
            _result.Text = "Sending friend request...";
            try
            {
                var (ok, error) = await GameChatRoblox.AddVoidstrapFriendAsync(_robloxId, _lifetimeCts.Token);
				if (_closed)
					return;
                if (ok)
                {
                    _result.Text = "Friend request sent.";
                    _addFriendBtn.Content = "Requested";
                }
                else
                {
                    _result.Text = error ?? "Could not send friend request.";
                    _addFriendBtn.IsEnabled = true;
                }
            }
			catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
			{
			}
			catch (Exception ex)
			{
				if (!_closed)
				{
					_result.Text = ex.Message;
					_addFriendBtn.IsEnabled = true;
				}
			}
            finally
            {
                _addBusy = false;
            }
        }

        private async void OnReportClick(object sender, RoutedEventArgs e)
        {
            if (_reportBusy || _reporter == null || _reportBtn == null)
                return;
            _reportBusy = true;
            _reportBtn.IsEnabled = false;
            _result.Text = "Sending report...";
            try
            {
                var outcome = await _reporter();
				if (_closed)
					return;
                switch (outcome)
                {
                    case GameChatBugResult.Ok:
                        _result.Text = "Report sent to moderators.";
                        _reportBtn.Content = "Reported";
                        break;
                    case GameChatBugResult.RateLimited:
                        _result.Text = "Please wait before reporting again.";
                        _reportBtn.IsEnabled = true;
                        break;
                    case GameChatBugResult.NotConnected:
                        _result.Text = "Not connected.";
                        _reportBtn.IsEnabled = true;
                        break;
                    default:
                        _result.Text = "Could not send report.";
                        _reportBtn.IsEnabled = true;
                        break;
                }
            }
            catch
            {
                _result.Text = "Could not send report.";
                _reportBtn.IsEnabled = true;
            }
            finally
            {
                _reportBusy = false;
            }
        }

        private void OnViewClick(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = App.WebsiteBaseUrl + "/pages/profile.html?id=" + _robloxId,
                    UseShellExecute = true,
                });
            }
            catch
            {
            }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void OnKeyDownHandler(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
                Close();
        }

		private void OnSourceInitialized(object? sender, EventArgs e)
		{
			_windowHandle = new WindowInteropHelper(this).Handle;
			OverlayDiagnostics.RegisterOverlayHandle(_windowHandle);
		}

        protected override void OnClosed(EventArgs e)
        {
			_closed = true;
            _lifetimeCts.Cancel();
            SourceInitialized -= OnSourceInitialized;
            OverlayDiagnostics.UnregisterOverlayHandle(_windowHandle);
			_windowHandle = IntPtr.Zero;
            KeyDown -= OnKeyDownHandler;
            _closeBtn.Click -= OnCloseClick;
            _addFriendBtn.Click -= OnAddFriendClick;
            _viewBtn.Click -= OnViewClick;
            if (_reportBtn != null)
                _reportBtn.Click -= OnReportClick;
			_wearingPanel.Children.Clear();
			_badgesPanel.Children.Clear();
			_avatar.Fill = null;
			_borderImage.Source = null;
			_band.Background = null;
            _lifetimeCts.Dispose();
            GC.SuppressFinalize(this);
            base.OnClosed(e);
        }
    }
}
