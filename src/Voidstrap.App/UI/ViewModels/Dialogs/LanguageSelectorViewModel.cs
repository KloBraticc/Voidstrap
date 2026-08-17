using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Voidstrap.Resources;
using Voidstrap.Utility;

namespace Voidstrap.UI.ViewModels.Dialogs;

internal class LanguageSelectorViewModel : NotifyPropertyChangedViewModel
{
	public static string AutoTranslateOption => Strings.Dialog_LanguageSelector_AutoTranslate;

	private readonly string _originalLocale;

	private readonly bool _originalAutoTranslate;

	private readonly string _originalAutoTranslateLanguage;

	private string _selectedLanguage;

	private string _selectedAutoTranslateLanguage;

	public ICommand SetLocaleCommand => new RelayCommand(SetLocale);

	public List<string> LanguageOptions
	{
		get
		{
			List<string> languages = Locale.GetLanguages();
			languages.Insert(Math.Min(1, languages.Count), AutoTranslateOption);
			return languages;
		}
	}

	public List<string> AutoTranslateLanguages => TranslationService.AvailableLanguages.Values.OrderBy(value => value).ToList();

	public string SelectedLanguage
	{
		get => _selectedLanguage;
		set
		{
			if (string.IsNullOrEmpty(value) || _selectedLanguage == value)
			{
				return;
			}
			_selectedLanguage = value;
			OnPropertyChanged(nameof(SelectedLanguage));
			if (value == AutoTranslateOption)
			{
				App.Settings.Prop.AutoTranslate = true;
				App.Settings.Prop.AutoTranslateLanguage = GetSelectedAutoTranslateCode();
				TranslationService.Initialize();
				LiveLanguageRefresher.Initialize();
				OnPropertyChanged(nameof(AutoTranslateVisibility));
				OnPropertyChanged(nameof(SelectedAutoTranslateLanguage));
				LiveLanguageRefresher.RefreshAllOpenWindows();
				return;
			}
			App.Settings.Prop.AutoTranslate = false;
			string identifier = Locale.GetIdentifierFromName(value);
			App.Settings.Prop.Locale = identifier;
			Locale.Set(identifier);
			OnPropertyChanged(nameof(AutoTranslateVisibility));
		}
	}

	public string SelectedAutoTranslateLanguage
	{
		get => _selectedAutoTranslateLanguage;
		set
		{
			if (string.IsNullOrEmpty(value) || _selectedAutoTranslateLanguage == value)
			{
				return;
			}
			_selectedAutoTranslateLanguage = value;
			OnPropertyChanged(nameof(SelectedAutoTranslateLanguage));
			KeyValuePair<string, string> match = TranslationService.AvailableLanguages.FirstOrDefault(pair => pair.Value == value);
			if (!string.IsNullOrEmpty(match.Key))
			{
				App.Settings.Prop.AutoTranslate = true;
				App.Settings.Prop.AutoTranslateLanguage = match.Key;
				TranslationService.Initialize();
				LiveLanguageRefresher.Initialize();
				LiveLanguageRefresher.RefreshAllOpenWindows();
			}
		}
	}

	public Visibility AutoTranslateVisibility => _selectedLanguage == AutoTranslateOption ? Visibility.Visible : Visibility.Collapsed;

	public event EventHandler<bool>? CloseRequestEvent;

	public LanguageSelectorViewModel()
	{
		_originalLocale = App.Settings.Prop.Locale;
		_originalAutoTranslate = App.Settings.Prop.AutoTranslate;
		_originalAutoTranslateLanguage = App.Settings.Prop.AutoTranslateLanguage ?? "";
		_selectedAutoTranslateLanguage = GetInitialAutoTranslateLanguageName();
		_selectedLanguage = _originalAutoTranslate
			? AutoTranslateOption
			: Locale.SupportedLocales.TryGetValue(_originalLocale, out string? name) ? name : Locale.SupportedLocales[Locale.DefaultLocale];
	}

	public void Cancel()
	{
		App.Settings.Prop.Locale = _originalLocale;
		App.Settings.Prop.AutoTranslate = _originalAutoTranslate;
		App.Settings.Prop.AutoTranslateLanguage = _originalAutoTranslateLanguage;
		Locale.Set(_originalLocale);
		if (_originalAutoTranslate)
		{
			LiveLanguageRefresher.RefreshAllOpenWindows();
		}
		else
		{
			LiveLanguageRefresher.RestoreAllOpenWindows();
		}
	}

	private void SetLocale()
	{
		if (_selectedLanguage == AutoTranslateOption)
		{
			App.Settings.Prop.AutoTranslate = true;
			App.Settings.Prop.AutoTranslateLanguage = GetSelectedAutoTranslateCode();
		}
		else
		{
			App.Settings.Prop.AutoTranslate = false;
			App.Settings.Prop.Locale = Locale.GetIdentifierFromName(_selectedLanguage);
			Locale.Set(App.Settings.Prop.Locale);
		}
		CloseRequestEvent?.Invoke(this, true);
	}

	private string GetInitialAutoTranslateLanguageName()
	{
		if (!string.IsNullOrEmpty(_originalAutoTranslateLanguage) && TranslationService.AvailableLanguages.TryGetValue(_originalAutoTranslateLanguage, out string? configured))
		{
			return configured;
		}
		string systemLanguage = System.Globalization.CultureInfo.CurrentUICulture.Name;
		string normalized = systemLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
			? systemLanguage
			: System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
		if (TranslationService.AvailableLanguages.TryGetValue(normalized, out string? detected))
		{
			return detected;
		}
		return TranslationService.AvailableLanguages["en"];
	}

	private string GetSelectedAutoTranslateCode()
	{
		KeyValuePair<string, string> match = TranslationService.AvailableLanguages.FirstOrDefault(pair => pair.Value == _selectedAutoTranslateLanguage);
		return string.IsNullOrEmpty(match.Key) ? "en" : match.Key;
	}
}
