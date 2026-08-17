using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Voidstrap.UI.Elements.Settings.Pages;

namespace Voidstrap.Models;

public class FastFlag : INotifyPropertyChanged
{
	private bool _enabled;

	private ImageSource? _preset;

	private string _name = string.Empty;

	private string _value = string.Empty;

	private bool _index = true;

	private IReadOnlyList<string>? _visibleTags;

	private const int MaxVisibleTags = 2;

	public bool Enabled
	{
		get
		{
			return _enabled;
		}
		set
		{
			if (_enabled != value)
			{
				_enabled = value;
				OnPropertyChanged(nameof(Enabled));
			}
		}
	}

	public ImageSource? Preset
	{
		get
		{
			return _preset;
		}
		set
		{
			if (_preset != value)
			{
				_preset = value;
				OnPropertyChanged(nameof(Preset));
			}
		}
	}

	public string Name
	{
		get
		{
			return _name;
		}
		set
		{
			if (_name != value)
			{
				_name = value;
				_visibleTags = null;
				OnPropertyChanged(nameof(Name));
				OnPropertyChanged(nameof(VisibleTags));
			}
		}
	}

	public string Value
	{
		get
		{
			return _value;
		}
		set
		{
			if (_value != value)
			{
				_value = value;
				OnPropertyChanged(nameof(Value));
			}
		}
	}

	public bool Index
	{
		get
		{
			return _index;
		}
		set
		{
			if (_index != value)
			{
				_index = value;
				OnPropertyChanged(nameof(Index));
			}
		}
	}

	public IReadOnlyList<string> VisibleTags => _visibleTags ??= BuildVisibleTags();

	private IReadOnlyList<string> BuildVisibleTags()
	{
		List<string> tags = FastFlagEditorPage.FastFlagTagHelper.GetTags(Name);
		if (tags.Count <= MaxVisibleTags)
		{
			return tags;
		}
		List<string> visible = tags.GetRange(0, MaxVisibleTags);
		visible.Add($"+{tags.Count - MaxVisibleTags}");
		return visible;
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
	{
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}
}
