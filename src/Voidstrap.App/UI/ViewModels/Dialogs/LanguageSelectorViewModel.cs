using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Voidstrap.Resources;
using Voidstrap.Utility;

namespace Voidstrap.UI.ViewModels.Dialogs;

public class LanguageSelectorViewModel : NotifyPropertyChangedViewModel
{
	private readonly string _originalLocale;

	private readonly bool _originalAutoTranslate;

	private readonly string _originalAutoTranslateLanguage;

	private LanguageOption _selectedLanguage;

	private string _selectedAutoTranslateLanguage;

	public ICommand SetLocaleCommand => new RelayCommand(SetLocale);

	public ObservableCollection<LanguageOption> LanguageOptions { get; } = LanguageOption.CreateAll();

	public List<string> AutoTranslateLanguages => TranslationService.AvailableLanguages.Values.OrderBy(value => value).ToList();

	public LanguageOption SelectedLanguage
	{
		get => _selectedLanguage;
		set
		{
			if (value == null || ReferenceEquals(_selectedLanguage, value))
			{
				return;
			}
			_selectedLanguage = value;
			OnPropertyChanged(nameof(SelectedLanguage));
			if (value.Id == LanguageOption.AutoTranslateId)
			{
				App.Settings.Prop.AutoTranslate = true;
				App.Settings.Prop.AutoTranslateLanguage = GetSelectedAutoTranslateCode();
				TranslationService.Initialize();
				LiveLanguageRefresher.Initialize();
				OnPropertyChanged(nameof(AutoTranslateVisibility));
				OnPropertyChanged(nameof(SelectedAutoTranslateLanguage));
				LiveLanguageRefresher.RefreshAllOpenWindows();
				ScheduleLanguageLabelRefresh();
				return;
			}
			App.Settings.Prop.AutoTranslate = false;
			App.Settings.Prop.Locale = value.Id;
			Locale.Set(value.Id);
			OnPropertyChanged(nameof(AutoTranslateVisibility));
			ScheduleLanguageLabelRefresh();
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
				ScheduleLanguageLabelRefresh();
			}
		}
	}

	private void ScheduleLanguageLabelRefresh()
	{
		Application.Current?.Dispatcher.BeginInvoke((Action)RefreshLanguageLabels, System.Windows.Threading.DispatcherPriority.Background);
	}

	private void RefreshLanguageLabels()
	{
		foreach (LanguageOption option in LanguageOptions)
		{
			option.RefreshName();
		}
	}

	public Visibility AutoTranslateVisibility =>
		_selectedLanguage?.Id == LanguageOption.AutoTranslateId ? Visibility.Visible : Visibility.Collapsed;

	public event EventHandler<bool>? CloseRequestEvent;

	public LanguageSelectorViewModel()
	{
		_originalLocale = App.Settings.Prop.Locale;
		_originalAutoTranslate = App.Settings.Prop.AutoTranslate;
		_originalAutoTranslateLanguage = App.Settings.Prop.AutoTranslateLanguage ?? "";
		_selectedAutoTranslateLanguage = GetInitialAutoTranslateLanguageName();
		_selectedLanguage = LanguageOption.Find(LanguageOptions, _originalAutoTranslate, _originalLocale);
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
		if (_selectedLanguage.Id == LanguageOption.AutoTranslateId)
		{
			App.Settings.Prop.AutoTranslate = true;
			App.Settings.Prop.AutoTranslateLanguage = GetSelectedAutoTranslateCode();
		}
		else
		{
			App.Settings.Prop.AutoTranslate = false;
			App.Settings.Prop.Locale = _selectedLanguage.Id;
			Locale.Set(App.Settings.Prop.Locale);
		}
		CloseRequestEvent?.Invoke(this, true);
	}

	private string GetInitialAutoTranslateLanguageName()
	{
		if (
			!string.IsNullOrEmpty(_originalAutoTranslateLanguage)
			&& TranslationService.AvailableLanguages.TryGetValue(_originalAutoTranslateLanguage, out string? configured)
		)
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
		KeyValuePair<string, string> match = TranslationService.AvailableLanguages.FirstOrDefault(pair =>
			pair.Value == _selectedAutoTranslateLanguage
		);
		return string.IsNullOrEmpty(match.Key) ? "en" : match.Key;
	}
}
