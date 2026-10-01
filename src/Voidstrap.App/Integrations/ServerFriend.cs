namespace Voidstrap.Integrations;

public sealed class ServerFriend
{
	public long UserId { get; set; }

	public string Username { get; set; } = "";

	public string DisplayName { get; set; } = "";

	public string HeadshotUrl { get; set; } = "";

	public string Label
	{
		get
		{
			if (!string.IsNullOrWhiteSpace(DisplayName))
			{
				return DisplayName;
			}
			if (!string.IsNullOrWhiteSpace(Username))
			{
				return Username;
			}
			return "User " + UserId;
		}
	}

	public string ToolTipText => Handle.Length > 0 ? Label + " (" + Handle + ")" : Label;

	public string Handle => string.IsNullOrWhiteSpace(Username) || string.Equals(Username, DisplayName, System.StringComparison.Ordinal) ? string.Empty : "@" + Username;
}
