using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Voidstrap.Utility;

namespace Voidstrap.UI.Elements.ContextMenu
{
    public class ProfileLinkRow
    {
        public string Label { get; set; } = "";
        public string Url { get; set; } = "";
    }

    public partial class ProfileEditorWindow
    {
        private static readonly string[] GradKeys = { "none", "violet", "sunset", "ocean", "forest", "rose", "mono", "custom" };
        private static readonly string[] GradLabels = { "None", "Violet", "Sunset", "Ocean", "Forest", "Rose", "Mono", "Custom" };
        private static readonly string[] BorderModes = { "None", "Solid color", "Gradient" };

        private static readonly Regex GradParse = new(@"^linear-gradient\((\d{1,3})deg,(#[0-9a-fA-F]{6}),(#[0-9a-fA-F]{6})\)$", RegexOptions.Compiled);

        private readonly ObservableCollection<ProfileLinkRow> _links = new();
        private string? _bannerChange;
        private int _bannerPreviewVersion;

        public bool Saved { get; private set; }

        public ProfileEditorWindow()
        {
            InitializeComponent();
            GradientCombo.ItemsSource = GradLabels;
            GradientCombo.SelectedIndex = 0;
            BorderCombo.ItemsSource = BorderModes;
            BorderCombo.SelectedIndex = 0;
            LinksList.ItemsSource = _links;
            Loaded += ProfileEditorWindow_Loaded;
        }

        private async void ProfileEditorWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= ProfileEditorWindow_Loaded;

            if (!WebsiteProfileEditor.IsSignedIn())
            {
                StatusText.Text = "Sign in to the Voidstrap website to edit your profile.";
                SaveBtn.IsEnabled = false;
                return;
            }

            StatusText.Text = "Loading...";
            var d = await WebsiteProfileEditor.LoadAsync();
            if (d is null)
            {
                StatusText.Text = "Could not load your profile.";
                return;
            }

            StatusBox.Text = d.Status;
            AboutBox.Text = d.About;

            int gi = Array.IndexOf(GradKeys, string.IsNullOrEmpty(d.GradientKey) ? "none" : d.GradientKey);
            GradientCombo.SelectedIndex = gi >= 0 ? gi : 0;
            if (d.GradientKey == "custom")
            {
                var m = GradParse.Match(d.GradientCss ?? "");
                if (m.Success) { GradAngle.Text = m.Groups[1].Value; GradC1.Text = m.Groups[2].Value; GradC2.Text = m.Groups[3].Value; }
            }

            string ab = (d.AvatarBorder ?? "").Trim();
            if (ab.Length == 0) BorderCombo.SelectedIndex = 0;
            else if (ab.StartsWith("#")) { BorderCombo.SelectedIndex = 1; BorderSolid.Text = ab; }
            else
            {
                var bm = GradParse.Match(ab);
                if (bm.Success) { BorderCombo.SelectedIndex = 2; BorderAngle.Text = bm.Groups[1].Value; BorderC1.Text = bm.Groups[2].Value; BorderC2.Text = bm.Groups[3].Value; }
            }

            _links.Clear();
            foreach (var l in d.Links)
                _links.Add(new ProfileLinkRow { Label = l.Label, Url = l.Url });
            UpdateAddLinkState();

            _bannerChange = null;
            await SetBannerPreviewAsync(d.Banner);

            StatusText.Text = "";
        }

        private void GradientCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CustomGradPanel is null) return;
            CustomGradPanel.Visibility = GradientCombo.SelectedIndex == 7 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BorderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (BorderSolidPanel is null || BorderGradPanel is null) return;
            BorderSolidPanel.Visibility = BorderCombo.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            BorderGradPanel.Visibility = BorderCombo.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void ImportBanner_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif",
                    Multiselect = false
                };
                if (dlg.ShowDialog() != true) return;

                StatusText.Text = "Processing image...";
                var (ok, dataUrl, err) = WebsiteProfileEditor.BuildBannerDataUrl(dlg.FileName);
                if (!ok) { StatusText.Text = err ?? "Could not read that image."; return; }

                _bannerChange = dataUrl;
                await SetBannerPreviewAsync(dataUrl);
                StatusText.Text = "";
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("ProfileEditorWindow::ImportBanner", ex);
                StatusText.Text = "Could not import that image.";
            }
        }

        private void RemoveBanner_Click(object sender, RoutedEventArgs e)
        {
            _bannerPreviewVersion++;
            _bannerChange = "";
            BannerPreview.Background = new SolidColorBrush(Color.FromArgb(0x22, 0, 0, 0));
        }

        private void AddLink_Click(object sender, RoutedEventArgs e)
        {
            if (_links.Count >= 5) return;
            _links.Add(new ProfileLinkRow());
            UpdateAddLinkState();
        }

        private void RemoveLink_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ProfileLinkRow row)
            {
                _links.Remove(row);
                UpdateAddLinkState();
            }
        }

        private void UpdateAddLinkState()
        {
            if (AddLinkBtn != null) AddLinkBtn.IsEnabled = _links.Count < 5;
        }

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SaveBtn.IsEnabled = false;
                StatusText.Text = "Saving...";

                var d = new WebsiteProfileData
                {
                    Status = StatusBox.Text ?? "",
                    About = AboutBox.Text ?? "",
                    Banner = _bannerChange
                };

                string key = GradKeys[Math.Max(0, GradientCombo.SelectedIndex)];
                if (key == "custom")
                {
                    d.GradientKey = "custom";
                    d.GradientCss = $"linear-gradient({SanitizeAngle(GradAngle.Text)}deg,{NormHex(GradC1.Text)},{NormHex(GradC2.Text)})";
                }
                else
                {
                    d.GradientKey = key;
                }

                int bm = Math.Max(0, BorderCombo.SelectedIndex);
                if (bm == 1) d.AvatarBorder = NormHex(BorderSolid.Text);
                else if (bm == 2) d.AvatarBorder = $"linear-gradient({SanitizeAngle(BorderAngle.Text)}deg,{NormHex(BorderC1.Text)},{NormHex(BorderC2.Text)})";
                else d.AvatarBorder = "";

                foreach (var l in _links)
                    d.Links.Add(new WebsiteProfileLink { Label = l.Label ?? "", Url = l.Url ?? "" });

                var (ok, queued, message) = await WebsiteProfileEditor.SaveAsync(d);
                if (ok)
                {
                    Saved = true;
                    Close();
                    return;
                }
                if (queued)
                {
                    Saved = true;
                    Voidstrap.UI.Frontend.ShowMessageBox(message ?? "Your changes are queued and will apply automatically once Cloudflare recovers.", System.Windows.MessageBoxImage.Information);
                    Close();
                    return;
                }

                StatusText.Text = message ?? "Could not save.";
                SaveBtn.IsEnabled = true;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("ProfileEditorWindow::Save", ex);
                StatusText.Text = "Could not save.";
                SaveBtn.IsEnabled = true;
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

        private async Task SetBannerPreviewAsync(string? url)
        {
            int version = ++_bannerPreviewVersion;
            try
            {
                if (string.IsNullOrEmpty(url))
                {
                    BannerPreview.Background = new SolidColorBrush(Color.FromArgb(0x22, 0, 0, 0));
                    return;
                }

                string resolved = url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? url
                    : App.WebsiteBaseUrl.TrimEnd('/') + (url.StartsWith("/") ? url : "/" + url);
                BitmapSource? bi = await GradientWebsite.LoadBannerImageAsync(resolved);

                if (version == _bannerPreviewVersion && bi != null)
                    BannerPreview.Background = new ImageBrush(bi) { Stretch = Stretch.UniformToFill };
                else if (version == _bannerPreviewVersion)
                    BannerPreview.Background = new SolidColorBrush(Color.FromArgb(0x22, 0, 0, 0));
            }
            catch
            {
                if (version == _bannerPreviewVersion)
                    BannerPreview.Background = new SolidColorBrush(Color.FromArgb(0x22, 0, 0, 0));
            }
        }

        private static string SanitizeAngle(string? s)
        {
            if (int.TryParse((s ?? "").Trim(), out int a))
                return Math.Clamp(a, 0, 360).ToString();
            return "135";
        }

        private static string NormHex(string? s)
        {
            string v = (s ?? "").Trim();
            if (v.Length == 0) return "#000000";
            if (!v.StartsWith("#")) v = "#" + v;
            return Regex.IsMatch(v, "^#[0-9a-fA-F]{6}$") ? v : "#000000";
        }
    }
}
