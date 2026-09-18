using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Voidstrap.Integrations;

namespace Voidstrap.UI.ViewModels.Settings
{
    internal class RobloxNewsEntry
    {
        public long Id { get; init; }

        public string Title { get; init; } = string.Empty;

        public string Summary { get; init; } = string.Empty;

        public string Url { get; init; } = string.Empty;

        public string Tag { get; init; } = string.Empty;

        public string? ImageUrl { get; init; }

        public int Views { get; init; }

        public int Replies { get; init; }

        public DateTime Created { get; init; }

        public bool HasImage => !string.IsNullOrEmpty(ImageUrl);

        public Visibility TagVisibility => string.IsNullOrEmpty(Tag) ? Visibility.Collapsed : Visibility.Visible;

        public string DateLabel
        {
            get
            {
                if (Created == DateTime.MinValue)
                    return string.Empty;
                DateTime local = Created.ToLocalTime();
                double days = Math.Floor((DateTime.Now.Date - local.Date).TotalDays);
                if (days <= 0)
                    return "Today";
                if (days == 1)
                    return "Yesterday";
                if (days < 7)
                    return $"{days:0} days ago";
                return local.ToString("MMMM d, yyyy", CultureInfo.CurrentCulture);
            }
        }

        public string StatsLabel
        {
            get
            {
                string views = Format(Views) + (Views == 1 ? " view" : " views");
                return Replies > 0 ? views + "  •  " + Format(Replies) + (Replies == 1 ? " reply" : " replies") : views;
            }
        }

        private static string Format(int value)
        {
            if (value >= 1000000)
                return (value / 1000000d).ToString("0.#", CultureInfo.CurrentCulture) + "M";
            return value >= 1000 ? (value / 1000d).ToString("0.#", CultureInfo.CurrentCulture) + "K" : value.ToString(CultureInfo.CurrentCulture);
        }
    }

    internal class RobloxNewsViewModel : NotifyPropertyChangedViewModel
    {
        private readonly ObservableCollection<RobloxNewsEntry> _posts = new();
        private bool _isLoading = true;
        private string _status = string.Empty;

        public RobloxNewsViewModel()
        {
            RefreshCommand = new AsyncRelayCommand(() => LoadAsync(true));
            OpenPostCommand = new RelayCommand<RobloxNewsEntry>(OpenPost);
            OpenFeedCommand = new RelayCommand(OpenFeed);
        }

        public ObservableCollection<RobloxNewsEntry> Posts => _posts;

        public ICommand RefreshCommand { get; }

        public ICommand OpenPostCommand { get; }

        public ICommand OpenFeedCommand { get; }

        public bool IsLoading
        {
            get => _isLoading;
            private set { _isLoading = value; OnPropertyChanged(nameof(IsLoading)); OnPropertyChanged(nameof(HasPosts)); }
        }

        public bool HasPosts => !_isLoading && _posts.Count > 0;

        public string Status
        {
            get => _status;
            private set { _status = value; OnPropertyChanged(nameof(Status)); }
        }

        public async Task LoadAsync(bool force)
        {
            if (_posts.Count > 0 && !force)
                return;

            IsLoading = _posts.Count == 0;
            try
            {
                List<RobloxNews.NewsPost> source = await RobloxNews.GetAllAsync(CancellationToken.None).ConfigureAwait(true);
                List<RobloxNewsEntry> entries = source.Select(post => new RobloxNewsEntry
                {
                    Id = post.Id,
                    Title = post.Title,
                    Summary = post.Summary,
                    Url = post.Url,
                    Tag = post.Tag,
                    ImageUrl = string.IsNullOrEmpty(post.ImageUrl) ? null : RobloxNews.Thumbnail(post.ImageUrl, 420),
                    Views = post.Views,
                    Replies = post.Replies,
                    Created = post.Created
                }).ToList();

                _posts.Clear();
                foreach (RobloxNewsEntry entry in entries)
                    _posts.Add(entry);

                Status = entries.Count == 0
                    ? "No announcements could be loaded right now."
                    : entries.Count + (entries.Count == 1 ? " announcement" : " announcements") + " from the Roblox announcements feed";
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("RobloxNewsViewModel::LoadAsync", ex);
                Status = "The announcements feed could not be reached.";
            }
            finally
            {
                IsLoading = false;
                OnPropertyChanged(nameof(HasPosts));
            }
        }

        private static void OpenPost(RobloxNewsEntry? entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Url))
                return;
            try
            {
                Utilities.ShellExecute(entry.Url);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("RobloxNewsViewModel::OpenPost", ex);
            }
        }

        private static void OpenFeed()
        {
            try
            {
                Utilities.ShellExecute(RobloxNews.FeedPageUrl);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("RobloxNewsViewModel::OpenFeed", ex);
            }
        }
    }
}
