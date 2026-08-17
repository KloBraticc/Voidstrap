using System;
using System.Collections.ObjectModel;
using System.Linq;
using Voidstrap.RobloxInterfaces;

namespace Voidstrap.UI.ViewModels.Installer;

public class MirrorOption
{
    public MirrorOption(string display, string url)
    {
        Display = display;
        Url = url;
    }

    public string Display { get; }

    public string Url { get; }

}

public class ChannelViewModel : NotifyPropertyChangedViewModel
{
    private const string AutoDisplay = "Auto (fastest responding server)";

    private MirrorOption _selectedMirror;
    private string _channel;

    public ChannelViewModel()
    {
        Mirrors = new ObservableCollection<MirrorOption>
        {
            new MirrorOption(AutoDisplay, string.Empty),
        };
        foreach (string url in Deployment.Mirrors)
            Mirrors.Add(new MirrorOption(Describe(url), url));

        string saved = App.Settings.Prop.PreferredMirror ?? string.Empty;
		_selectedMirror = Mirrors.FirstOrDefault(option => string.Equals(option.Url, saved, StringComparison.OrdinalIgnoreCase)) ?? Mirrors[0];
		if (!string.Equals(_selectedMirror.Url, saved, StringComparison.Ordinal))
		{
			App.Settings.Prop.PreferredMirror = _selectedMirror.Url;
			Deployment.PreferredBaseUrl = _selectedMirror.Url;
			App.Settings.SaveDeferred();
		}

        string savedChannel = App.Settings.Prop.Channel;
        _channel = string.IsNullOrWhiteSpace(savedChannel) ? Deployment.DefaultChannel : savedChannel;
    }

    public ObservableCollection<MirrorOption> Mirrors { get; }

    public MirrorOption SelectedMirror
    {
        get => _selectedMirror;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedMirror))
                return;
            _selectedMirror = value;
            OnPropertyChanged(nameof(SelectedMirror));
            App.Settings.Prop.PreferredMirror = value.Url;
            Deployment.PreferredBaseUrl = value.Url;
            App.Settings.SaveDeferred();
        }
    }

    public string Channel
    {
        get => _channel;
        set
        {
            string incoming = value ?? string.Empty;
            if (incoming == _channel)
                return;
            _channel = incoming;
            OnPropertyChanged(nameof(Channel));
        }
    }

    public void Apply()
    {
        string channel = _channel.Trim();
        if (channel.Length == 0)
            channel = Deployment.DefaultChannel;
        App.Settings.Prop.Channel = channel;
        App.Settings.Prop.PreferredMirror = _selectedMirror.Url;
        Deployment.PreferredBaseUrl = _selectedMirror.Url;
        App.Settings.SaveDeferred();
    }

    private static string Describe(string url)
    {
        if (string.IsNullOrEmpty(url))
            return AutoDisplay;
        try
        {
            return new Uri(url).Host;
        }
        catch
        {
            return url;
        }
    }
}
