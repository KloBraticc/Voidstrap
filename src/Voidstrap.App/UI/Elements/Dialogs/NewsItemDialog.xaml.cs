using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Voidstrap.UI.Elements.Base;
using Voidstrap.UI.ViewModels.Settings;

namespace Voidstrap.UI.Elements.Dialogs
{
    public partial class NewsItemDialog : WpfUiWindow
    {
        private readonly NewsItem _item;

        public NewsItemDialog(NewsItem item)
        {
            _item = item ?? throw new ArgumentNullException(nameof(item));
            DataContext = _item;
            InitializeComponent();
        }

        private void CopyText_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var text = $"{_item.Title}\n{_item.Date:G}\n\n{_item.Content}";
                Clipboard.SetText(text);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NewsItemDialog::CopyText] {ex.Message}");
            }
        }

        private void TagButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Content: string url } && Uri.IsWellFormedUriString(url, UriKind.Absolute))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[NewsItemDialog::OpenTag] {ex.Message}");
                }
            }
        }
    }
}
