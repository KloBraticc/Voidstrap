using System.Collections.ObjectModel;
using System.Linq;
using Voidstrap.Resources;

namespace Voidstrap.UI.ViewModels;

public sealed class LanguageOption : NotifyPropertyChangedViewModel
{
	public const string AutoTranslateId = "auto";

	public LanguageOption(string id)
	{
		Id = id;
	}

	public string Id { get; }

	public string MetadataId => Id == AutoTranslateId ? Locale.DefaultLocale : Id;

	public string Name => Id == AutoTranslateId ? Strings.Dialog_LanguageSelector_AutoTranslate : Locale.GetDisplayName(Id);

	public string Status => LanguageMetadata.GetStatusText(MetadataId);

	public void RefreshName()
	{
		OnPropertyChanged(nameof(Name));
		OnPropertyChanged(nameof(Status));
	}

	public override string ToString()
	{
		return Name;
	}

	public static ObservableCollection<LanguageOption> CreateAll()
	{
		ObservableCollection<LanguageOption> options = new ObservableCollection<LanguageOption>
		{
			new LanguageOption(Locale.DefaultLocale),
			new LanguageOption(AutoTranslateId),
			new LanguageOption("en-US"),
		};
		foreach (
			string id in Locale
				.SupportedLocales.Keys.Where(key => key != Locale.DefaultLocale && key != "en-US")
				.OrderBy(key => Locale.SupportedLocales[key], System.StringComparer.CurrentCulture)
		)
		{
			options.Add(new LanguageOption(id));
		}
		return options;
	}

	public static LanguageOption Find(ObservableCollection<LanguageOption> options, bool autoTranslate, string? locale)
	{
		string id = autoTranslate
			? AutoTranslateId
			: (locale != null && Locale.SupportedLocales.ContainsKey(locale) ? locale : Locale.DefaultLocale);
		return options.First(option => option.Id == id);
	}
}
