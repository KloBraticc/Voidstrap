using System;
using System.Collections.Generic;

namespace Voidstrap.Models.Persistable;

public class State
{
	public bool TestModeWarningShown { get; set; }

	public bool ShowBloxshadeWarning { get; set; }

	public bool IgnoreOutdatedChannel { get; set; }

	public bool WatcherRunning { get; set; }

	public bool PromptWebView2Install { get; set; } = true;

	public int LastPage { get; set; } = 1;

	public AppState Player { get; set; } = new AppState();

	public AppState Studio { get; set; } = new AppState();

	public WindowState SettingsWindow { get; set; } = new WindowState();

	public List<string> ModManifest { get; set; } = new List<string>();

	public Dictionary<string, string> ModApplyCache { get; set; } = new Dictionary<string, string>();

	public Dictionary<string, List<string>> ManagedModManifest { get; set; } = new Dictionary<string, List<string>>();

	public string ModApplyVersion { get; set; } = string.Empty;

	public Dictionary<long, MatchmakerAttempt> MatchmakerAttempts { get; set; } = new Dictionary<long, MatchmakerAttempt>();

	public int PendingLaunchMode { get; set; }

	public DateTime LastLauncherUpdateCheckUtc { get; set; }
}
