namespace Voidstrap.Integrations;

public sealed class ServerFriend
{
	public long UserId { get; set; }

	public string Username { get; set; } = "";

	public string DisplayName { get; set; } = "";

	public string Label
	{
		get
		{
			if (!string.IsNullOrWhiteSpace(DisplayName))
			{
				return DisplayName;
			}
			return Username;
		}
	}
}
