using System;
using System.Collections.Generic;
using Voidstrap.UI.ViewModels;

namespace Voidstrap.UI.Elements.Settings.Pages;

public sealed class ExtensionEntry : NotifyPropertyChangedViewModel
{
	private readonly Func<bool> _getEnabled;

	private readonly Action<bool> _setEnabled;

	private string _openLabel = "Open";

	private bool _visible = true;

	public ExtensionEntry(string id, Func<bool> getEnabled, Action<bool> setEnabled)
	{
		Id = id;
		_getEnabled = getEnabled;
		_setEnabled = setEnabled;
	}

	public string Id { get; }

	public string Name { get; init; } = "";

	public string Author { get; init; } = "";

	public string AuthorIcon { get; init; } = "";

	public string Type { get; init; } = "";

	public IReadOnlyList<string> WorksWith { get; init; } = Array.Empty<string>();

	public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

	public string Summary { get; init; } = "";

	public IReadOnlyList<string> Description { get; init; } = Array.Empty<string>();

	public string Icon { get; init; } = "";

	private string _version = "";

	public string Version
	{
		get => _version.Length > 0 ? _version : GitHubRepo.Length > 0 ? "Checking version" : "No version for this content";
		private set
		{
			if (_version == value)
				return;
			_version = value;
			OnPropertyChanged(nameof(Version));
		}
	}

	public bool VersionResolved => _version.Length > 0;

	public void ApplyReleases(IReadOnlyList<Voidstrap.Models.APIs.GitHub.GithubRelease>? releases)
	{
		if (releases == null)
			return;
		foreach (Voidstrap.Models.APIs.GitHub.GithubRelease release in releases)
		{
			if (release.Draft || release.Prerelease || string.IsNullOrEmpty(release.TagName))
				continue;
			Version = release.TagName;
			return;
		}
		foreach (Voidstrap.Models.APIs.GitHub.GithubRelease release in releases)
		{
			if (!release.Draft && !string.IsNullOrEmpty(release.TagName))
			{
				Version = release.TagName;
				return;
			}
		}
		Version = "No releases yet";
	}

	public string Updated { get; init; } = "";

	public string Source { get; init; } = "";

	public string License { get; init; } = "";

	public bool CanOpen { get; init; }

	public bool HasTools { get; init; }

	public string SearchText { get; init; } = "";

	public bool IsEnabled
	{
		get => _getEnabled();
		set
		{
			if (_getEnabled() == value)
				return;
			_setEnabled(value);
			RefreshEnabled();
		}
	}

	public string EnableLabel => IsEnabled ? "Disable" : "Enable";

	public string StatusLabel => IsEnabled ? "Enabled" : "Not enabled";

	public string OpenLabel
	{
		get => _openLabel;
		set
		{
			if (_openLabel == value)
				return;
			_openLabel = value;
			OnPropertyChanged(nameof(OpenLabel));
		}
	}

	public bool Visible
	{
		get => _visible;
		set
		{
			if (_visible == value)
				return;
			_visible = value;
			OnPropertyChanged(nameof(Visible));
		}
	}

	public bool HasSource => Source.Length > 0;

	public string GitHubRepo
	{
		get
		{
			if (!Uri.TryCreate(Source, UriKind.Absolute, out Uri? uri) || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
				return "";
			string[] parts = uri.AbsolutePath.Trim('/').Split('/');
			return parts.Length >= 2 && parts[0].Length > 0 && parts[1].Length > 0 ? parts[0] + "/" + parts[1] : "";
		}
	}

	public string TagsLine => string.Join(", ", Tags);

	public void RefreshEnabled()
	{
		OnPropertyChanged(nameof(IsEnabled));
		OnPropertyChanged(nameof(EnableLabel));
		OnPropertyChanged(nameof(StatusLabel));
	}
}

public sealed record ReleaseRow(string Title, string Meta, string Url)
{
	public bool HasUrl => Url.Length > 0;
}
