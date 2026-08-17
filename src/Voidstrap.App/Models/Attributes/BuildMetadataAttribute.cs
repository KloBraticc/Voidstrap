using System;
using System.Globalization;

namespace Voidstrap.Models.Attributes;

[AttributeUsage(AttributeTargets.Assembly)]
public class BuildMetadataAttribute : Attribute
{
	public DateTime Timestamp { get; set; }

	public string Machine { get; set; }

	public string CommitHash { get; set; }

	public string CommitRef { get; set; }

	public BuildMetadataAttribute(string timestamp, string machine, string commitHash, string commitRef)
	{
		Timestamp = (DateTime.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime parsed) ? parsed : DateTime.UnixEpoch).ToLocalTime();
		Machine = machine;
		CommitHash = commitHash;
		CommitRef = commitRef;
	}
}
