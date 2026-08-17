using System.Windows;
using Voidstrap.Extensions;
using Voidstrap.Models.Attributes;
using Voidstrap.Resources;

namespace Voidstrap.UI.ViewModels.About;

public class AboutViewModel : NotifyPropertyChangedViewModel
{
	public string Version => string.Format(Strings.Menu_About_Version, App.Version);

	public BuildMetadataAttribute BuildMetadata => App.BuildMetadata;

	public string BuildTimestamp => BuildMetadata.Timestamp.ToFriendlyString();

	public string CommitHash => BuildMetadata.CommitHash;

	public string BuildCommitHashUrl => "https://github.com/KloBraticc/Voidstrap/commit/" + BuildMetadata.CommitHash;

	public Visibility BuildInformationVisibility
	{
		get
		{
			if (!App.IsProductionBuild)
			{
				return Visibility.Visible;
			}
			return Visibility.Collapsed;
		}
	}

	public Visibility BuildCommitVisibility
	{
		get
		{
			if (!App.IsActionBuild)
			{
				return Visibility.Collapsed;
			}
			return Visibility.Visible;
		}
	}
}
