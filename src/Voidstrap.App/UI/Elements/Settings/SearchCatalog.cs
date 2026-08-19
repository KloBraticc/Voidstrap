using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Voidstrap.Core;
using Voidstrap.Platform;
using Voidstrap.Resources;

namespace Voidstrap.UI.Elements.Settings;

internal sealed class SearchCatalogOption
{
	public string Id { get; }

	public Type PageType { get; }

	public string TitleToken { get; }

	public string DescriptionToken { get; }

	public IReadOnlyList<string> Aliases { get; }

	public string TargetName { get; }

	public IReadOnlyList<string> Containers { get; }

	public SearchCatalogOption(string id, Type pageType, string titleToken, string descriptionToken, IReadOnlyList<string> aliases, string targetName, IReadOnlyList<string> containers)
	{
		Id = id;
		PageType = pageType;
		TitleToken = titleToken;
		DescriptionToken = descriptionToken;
		Aliases = aliases;
		TargetName = targetName;
		Containers = containers;
	}
}

internal static class SearchCatalog
{
	private static readonly object OptionsGate = new();

	private static IReadOnlyList<SearchCatalogOption>? _options;

	private static Task? _loadTask;

	public static IReadOnlyList<SearchCatalogOption> Options
	{
		get
		{
			lock (OptionsGate)
			{
				return _options ?? Array.Empty<SearchCatalogOption>();
			}
		}
	}

	public static Task LoadOptionsAsync()
	{
		lock (OptionsGate)
		{
			if (_options is { Count: > 0 })
			{
				return Task.CompletedTask;
			}
			return _loadTask ??= LoadOptionsCoreAsync();
		}
	}

	private static async Task LoadOptionsCoreAsync()
	{
		IReadOnlyList<SearchCatalogOption> loaded = Array.Empty<SearchCatalogOption>();
		try
		{
			OperationResult<IReadOnlyCollection<SettingsCatalogEntry>> result = await SettingsCatalogImporter.LoadAsync().ConfigureAwait(false);
			if (result.Succeeded && result.Value is not null)
			{
				loaded = result.Value
					.Select(static entry => new
					{
						Entry = entry,
						PageType = ResolvePageType(entry.SourcePage)
					})
					.Where(static item => item.PageType is not null
						&& IsPageAvailableOnThisPlatform(item.Entry.SourcePage)
						&& IsVisibleOnThisPlatform(item.Entry.VisibilityExpression))
					.Select(static item => new SearchCatalogOption(item.Entry.Id, item.PageType!, item.Entry.Title, item.Entry.Description, item.Entry.Aliases.ToArray(), item.Entry.TargetName, item.Entry.Containers.ToArray()))
					.ToArray();
			}
			else
			{
				App.Logger.WriteLine("SearchCatalog", "Settings catalog load failed: " + result.Failure?.Code + ", " + result.Failure?.Message);
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("SearchCatalog", "Settings catalog load failed: " + ex.Message);
		}

		lock (OptionsGate)
		{
			if (loaded.Count > 0)
			{
				_options = loaded;
			}
			_loadTask = null;
		}
	}

	private static readonly HashSet<string> WindowsOnlyPages = new(StringComparer.Ordinal)
	{
		"ShortcutsPage",
		"NvidiaFastFlagsPage",
		"NvidaEditor"
	};

	private static bool IsPageAvailableOnThisPlatform(string sourcePage)
	{
		return !Voidstrap.Utility.Platform.IsLinux || !WindowsOnlyPages.Contains(sourcePage);
	}

	private static bool IsVisibleOnThisPlatform(string visibilityExpression)
	{
		if (string.IsNullOrWhiteSpace(visibilityExpression))
			return true;

		Match match = Regex.Match(visibilityExpression, @"PlatformFeatureVisibility\.(?<member>[A-Za-z0-9_]+)");
		if (!match.Success)
			return true;

		System.Reflection.PropertyInfo? member = typeof(Voidstrap.UI.PlatformFeatureVisibility)
			.GetProperty(match.Groups["member"].Value, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
		return member?.GetValue(null) is not System.Windows.Visibility visibility || visibility == System.Windows.Visibility.Visible;
	}

	public static string Resolve(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return string.Empty;
		}
		Match match = Regex.Match(value, "Strings\\.([A-Za-z0-9_]+)");
		if (match.Success)
		{
			return Strings.ResourceManager.GetString(match.Groups[1].Value) ?? match.Groups[1].Value;
		}
		return value;
	}

	public static string FriendlyPageName(Type pageType)
	{
		return pageType.Name switch
		{
			"BehaviourPage" => "Deployment",
			"GBSEditorPage" => "Global",
			"FastFlagsPage" => "FastFlag Settings",
			"FastFlagEditorPage" => "FastFlag Editor",
			"ChannelPage" => "Settings",
			_ => pageType.Name
		};
	}

	private static Type? ResolvePageType(string sourcePage)
	{
		if (string.IsNullOrWhiteSpace(sourcePage))
		{
			return null;
		}

		return typeof(SearchCatalog).Assembly.GetType("Voidstrap.UI.Elements.Settings.Pages." + sourcePage);
	}
}
