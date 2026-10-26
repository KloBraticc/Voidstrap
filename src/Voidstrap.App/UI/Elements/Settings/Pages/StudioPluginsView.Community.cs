using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Voidstrap.Integrations;
using Voidstrap.Integrations.Studio;
using Voidstrap.Resources;
using Wpf.Ui.Common;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class StudioPluginsView
{
	private CancellationTokenSource? _community;

	private string? _reviewCursor;

	private bool _isCreator;

	private bool? _vote;

	private bool _signedIn;

	private long CommunityAssetId => _detail?.AssetId ?? 0;

	private void StartCommunity(StudioPluginEntry entry)
	{
		StopCommunity();
		ReviewsPanel.Children.Clear();
		ReviewsStatus.Text = "";
		ReviewBox.Text = "";
		_reviewCursor = null;
		_isCreator = false;
		_vote = null;
		bool store = entry.AssetId.HasValue;
		ReviewsTab.Visibility = store ? Visibility.Visible : Visibility.Collapsed;
		VotePanel.Visibility = store ? Visibility.Visible : Visibility.Collapsed;
		ReviewsMoreButton.Visibility = Visibility.Collapsed;
		UpdateVoteButtons();
		if (!store)
			return;
		_signedIn = !string.IsNullOrEmpty(RobloxCookie.Get());
		ReviewComposer.Visibility = _signedIn ? Visibility.Visible : Visibility.Collapsed;
		if (!_signedIn)
			ReviewsStatus.Text = Strings.Ui_Mods_StudioPlugins_SignIn;
		_community = new CancellationTokenSource();
		_ = LoadCommunityAsync(entry.AssetId!.Value, _community.Token);
	}

	private void StopCommunity()
	{
		CancellationTokenSource? source = Interlocked.Exchange(ref _community, null);
		source?.Cancel();
		source?.Dispose();
	}

	private async Task LoadCommunityAsync(long assetId, CancellationToken token)
	{
		try
		{
			if (_signedIn)
			{
				Task<bool?> vote = StudioPluginCommunity.GetVoteAsync(assetId, token);
				Task<bool> creator = StudioPluginCommunity.IsCreatorAsync(assetId, token);
				_vote = await vote;
				_isCreator = await creator;
				token.ThrowIfCancellationRequested();
				UpdateVoteButtons();
			}
			await LoadReviewsAsync(assetId, token);
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
		}
		catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException or ArgumentException)
		{
			App.Logger.WriteException(LogIdent + "::Community", ex);
			ReviewsStatus.Text = Strings.Ui_Mods_StudioPlugins_ReviewsUnavailable;
		}
	}

	private async Task LoadReviewsAsync(long assetId, CancellationToken token)
	{
		ReviewsRing.Visibility = Visibility.Visible;
		ReviewsMoreButton.Visibility = Visibility.Collapsed;
		try
		{
			(RobloxAuthedStatus status, StudioPluginReviewPage? page) = await StudioPluginCommunity.GetReviewsAsync(assetId, _reviewCursor, token);
			token.ThrowIfCancellationRequested();
			if (page == null)
			{
				ReviewsStatus.Text = status == RobloxAuthedStatus.NotSignedIn ? Strings.Ui_Mods_StudioPlugins_SignIn : Strings.Ui_Mods_StudioPlugins_ReviewsUnavailable;
				return;
			}
			foreach (StudioPluginReview review in page.Reviews)
				ReviewsPanel.Children.Add(BuildReview(review));
			_reviewCursor = page.NextCursor;
			ReviewsMoreButton.Visibility = _reviewCursor != null ? Visibility.Visible : Visibility.Collapsed;
			if (ReviewsPanel.Children.Count == 0 && ReviewsStatus.Text.Length == 0)
				ReviewsStatus.Text = Strings.Ui_Mods_StudioPlugins_NoReviews;
		}
		finally
		{
			ReviewsRing.Visibility = Visibility.Collapsed;
		}
	}

	private FrameworkElement BuildReview(StudioPluginReview review)
	{
		StackPanel body = new();
		StackPanel header = new() { Orientation = Orientation.Horizontal };
		header.Children.Add(new SymbolIcon { Symbol = review.Recommended ? SymbolRegular.ThumbLike24 : SymbolRegular.ThumbDislike24, FontSize = 14, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
		header.Children.Add(new System.Windows.Controls.TextBlock { Text = review.Username, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
		if (review.Created is DateTime created)
		{
			System.Windows.Controls.TextBlock date = new() { Text = created.ToString("d", CultureInfo.CurrentCulture), Margin = new Thickness(10, 0, 0, 0), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
			date.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
			header.Children.Add(date);
		}
		body.Children.Add(header);
		body.Children.Add(new System.Windows.Controls.TextBlock { Text = review.Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });

		if (review.Reply is StudioPluginReply reply)
		{
			Border replyBorder = new() { Margin = new Thickness(16, 8, 0, 0), Padding = new Thickness(10), CornerRadius = new CornerRadius(6) };
			replyBorder.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");
			StackPanel replyBody = new();
			replyBody.Children.Add(new System.Windows.Controls.TextBlock { Text = string.Format(Strings.Ui_Mods_StudioPlugins_CreatorReply, reply.Username), FontWeight = FontWeights.SemiBold, FontSize = 12 });
			replyBody.Children.Add(new System.Windows.Controls.TextBlock { Text = reply.Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
			replyBorder.Child = replyBody;
			body.Children.Add(replyBorder);
		}

		StackPanel actions = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
		if (_signedIn && review.Id.Length > 0)
		{
			Wpf.Ui.Controls.Button helpful = new() { Content = string.Format(Strings.Ui_Mods_StudioPlugins_Helpful, review.HelpfulCount), Appearance = ControlAppearance.Secondary, Tag = review, Padding = new Thickness(10, 4, 10, 4) };
			helpful.Click += Helpful_Click;
			actions.Children.Add(helpful);
		}
		else
		{
			System.Windows.Controls.TextBlock count = new() { Text = string.Format(Strings.Ui_Mods_StudioPlugins_Helpful, review.HelpfulCount), FontSize = 12 };
			count.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
			actions.Children.Add(count);
		}
		body.Children.Add(actions);

		if (_isCreator && review.Reply == null && review.Id.Length > 0)
		{
			Grid replyRow = new() { Margin = new Thickness(0, 8, 0, 0) };
			replyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
			replyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
			Wpf.Ui.Controls.TextBox box = new() { PlaceholderText = Strings.Ui_Mods_StudioPlugins_ReplyPlaceholder, MaxLength = StudioPluginCommunity.MaxTextLength, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MinHeight = 32 };
			Wpf.Ui.Controls.Button send = new() { Content = Strings.Ui_Mods_StudioPlugins_Reply, Appearance = ControlAppearance.Primary, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top, Tag = (review, box) };
			send.Click += Reply_Click;
			Grid.SetColumn(send, 1);
			replyRow.Children.Add(box);
			replyRow.Children.Add(send);
			body.Children.Add(replyRow);
		}

		Border card = new() { Child = body, Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(12) };
		card.SetResourceReference(StyleProperty, "RowCard");
		return card;
	}

	private void ReviewsMore_Click(object sender, RoutedEventArgs e)
	{
		if (_community != null && CommunityAssetId > 0)
			_ = RunCommunityAction(sender as UIElement, token => LoadReviewsAsync(CommunityAssetId, token));
	}

	private void Like_Click(object sender, RoutedEventArgs e) => _ = VoteAsync(true);

	private void Dislike_Click(object sender, RoutedEventArgs e) => _ = VoteAsync(false);

	private async Task VoteAsync(bool up)
	{
		long assetId = CommunityAssetId;
		if (assetId <= 0)
			return;
		if (!_signedIn)
		{
			DetailStatus.Text = Strings.Ui_Mods_StudioPlugins_SignIn;
			return;
		}
		await RunCommunityAction(VotePanel, async token =>
		{
			RobloxAuthedResult result = await StudioPluginCommunity.VoteAsync(assetId, up, token);
			if (result.Succeeded)
			{
				_vote = up;
				UpdateVoteButtons();
			}
			DetailStatus.Text = result.Succeeded ? Strings.Ui_Mods_StudioPlugins_VoteSaved : Describe(result.Status);
		});
	}

	private void UpdateVoteButtons()
	{
		LikeButton.Appearance = _vote == true ? ControlAppearance.Primary : ControlAppearance.Secondary;
		DislikeButton.Appearance = _vote == false ? ControlAppearance.Primary : ControlAppearance.Secondary;
	}

	private void PostReview_Click(object sender, RoutedEventArgs e)
	{
		long assetId = CommunityAssetId;
		string text = StudioPluginCommunity.CleanText(ReviewBox.Text);
		if (assetId <= 0 || text.Length == 0)
			return;
		_ = RunCommunityAction(ReviewComposer, async token =>
		{
			RobloxAuthedResult result = await StudioPluginCommunity.PostReviewAsync(assetId, text, token);
			if (result.Succeeded)
			{
				ReviewBox.Text = "";
				ReviewsStatus.Text = Strings.Ui_Mods_StudioPlugins_ReviewPosted;
				return;
			}
			ReviewsStatus.Text = result.Status == RobloxAuthedStatus.NotAllowed ? Strings.Ui_Mods_StudioPlugins_ReviewNeedsOwnership : Describe(result.Status);
		});
	}

	private void Helpful_Click(object sender, RoutedEventArgs e)
	{
		if (sender is not Wpf.Ui.Controls.Button { Tag: StudioPluginReview review } button)
			return;
		_ = RunCommunityAction(button, async token =>
		{
			RobloxAuthedResult result = await StudioPluginCommunity.MarkHelpfulAsync(review.Id, token);
			if (result.Succeeded)
			{
				button.Content = string.Format(Strings.Ui_Mods_StudioPlugins_Helpful, review.HelpfulCount + 1);
				button.Appearance = ControlAppearance.Primary;
				button.Click -= Helpful_Click;
			}
			ReviewsStatus.Text = result.Succeeded ? Strings.Ui_Mods_StudioPlugins_HelpfulSaved : Describe(result.Status);
		});
	}

	private void Reply_Click(object sender, RoutedEventArgs e)
	{
		if (sender is not Wpf.Ui.Controls.Button { Tag: ValueTuple<StudioPluginReview, Wpf.Ui.Controls.TextBox> tag } button)
			return;
		(StudioPluginReview review, Wpf.Ui.Controls.TextBox box) = tag;
		long assetId = CommunityAssetId;
		string text = StudioPluginCommunity.CleanText(box.Text);
		if (assetId <= 0 || text.Length == 0)
			return;
		_ = RunCommunityAction(button, async token =>
		{
			RobloxAuthedResult result = await StudioPluginCommunity.ReplyAsync(assetId, review.Id, text, token);
			if (result.Succeeded)
			{
				box.Text = "";
				box.IsEnabled = false;
				button.Click -= Reply_Click;
			}
			ReviewsStatus.Text = result.Succeeded ? Strings.Ui_Mods_StudioPlugins_ReplyPosted : Describe(result.Status);
		});
	}

	private async Task RunCommunityAction(UIElement? busy, Func<CancellationToken, Task> action)
	{
		CancellationTokenSource? source = _community;
		if (source == null)
			return;
		CancellationToken token = source.Token;
		if (busy != null)
			busy.IsEnabled = false;
		try
		{
			await action(token);
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
		}
		catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException or ArgumentException)
		{
			App.Logger.WriteException(LogIdent + "::CommunityAction", ex);
			ReviewsStatus.Text = Strings.Ui_Mods_StudioPlugins_ActionFailed;
		}
		finally
		{
			if (busy != null && !token.IsCancellationRequested)
				busy.IsEnabled = true;
		}
	}

	private static string Describe(RobloxAuthedStatus status) => status switch
	{
		RobloxAuthedStatus.NotSignedIn => Strings.Ui_Mods_StudioPlugins_SignIn,
		RobloxAuthedStatus.RateLimited => Strings.Ui_Mods_StudioPlugins_RateLimited,
		_ => Strings.Ui_Mods_StudioPlugins_ActionFailed
	};

	private void Publish_Click(object sender, RoutedEventArgs e)
	{
		StudioPluginPublishDialog dialog = new() { Owner = Window.GetWindow(this) };
		dialog.ShowOwnedDialog();
	}
}
