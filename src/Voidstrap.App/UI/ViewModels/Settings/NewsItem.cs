using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace Voidstrap.UI.ViewModels.Settings
{
    public partial class NewsItem : ObservableObject
    {
        [ObservableProperty]
        public partial string Title { get; set; } = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsNew))]
        [NotifyPropertyChangedFor(nameof(AgeLabel))]
        public partial DateTime Date { get; set; }

        [ObservableProperty]
        public partial string Content { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string ImageUrl { get; set; } = string.Empty;

        [ObservableProperty]
        public partial BitmapSource? Image { get; set; }

        private readonly ObservableCollection<string> tags = new();
        public ObservableCollection<string> Tags => tags;

        partial void OnContentChanged(string value)
        {
            GenerateTags(value);
            OnPropertyChanged(nameof(DisplayContent));
        }

        private void GenerateTags(string? text)
        {
            tags.Clear();

            if (string.IsNullOrWhiteSpace(text))
                return;

            var matches = UrlRegex.Matches(text);

            foreach (var url in matches
                .Select(m => m.Value.TrimEnd('.', ',', ')'))
                .Where(u => Uri.IsWellFormedUriString(u, UriKind.Absolute))
                .Distinct())
            {
                tags.Add(url);
            }
        }

        public string DisplayContent =>
            string.IsNullOrWhiteSpace(Content)
                ? string.Empty
                : UrlStripRegex.Replace(Content, "").Trim();

        public bool IsNew =>
            (DateTime.Now - Date).TotalDays <= 3;

        public string AgeLabel => "NEW";

        [GeneratedRegex(@"(https?://[^\s]+)", RegexOptions.IgnoreCase, "en-US")]
        private static partial Regex UrlRegex { get; }
        [GeneratedRegex(@"https?://[^\s]+", RegexOptions.IgnoreCase, "en-US")]
        private static partial Regex UrlStripRegex { get; }
    }
}