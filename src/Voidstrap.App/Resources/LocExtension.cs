using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace Voidstrap.Resources;

public sealed class LocalizedStrings : INotifyPropertyChanged
{
	public static LocalizedStrings Instance { get; } = new LocalizedStrings();

	private static readonly ConcurrentDictionary<string, PropertyInfo?> _properties = new(StringComparer.Ordinal);

	private static readonly PropertyChangedEventArgs _indexerChanged = new("Item[]");

	public event PropertyChangedEventHandler? PropertyChanged;

	private LocalizedStrings()
	{
	}

	public string this[string name] => Find(name)?.GetValue(null) as string ?? string.Empty;

	internal static PropertyInfo? Find(string name)
	{
		return _properties.GetOrAdd(name, static key =>
		{
			PropertyInfo? property = typeof(Strings).GetProperty(key, BindingFlags.Public | BindingFlags.Static);
			return property?.PropertyType == typeof(string) ? property : null;
		});
	}

	public void Refresh()
	{
		PropertyChanged?.Invoke(this, _indexerChanged);
	}
}

[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
	public LocExtension()
	{
	}

	public LocExtension(string key)
	{
		Key = key;
	}

	[ConstructorArgument("key")]
	public string Key { get; set; } = string.Empty;

	public override object ProvideValue(IServiceProvider serviceProvider)
	{
		if (LocalizedStrings.Find(Key) == null)
		{
			throw new ArgumentException($"Unknown string resource {Key}");
		}
		IProvideValueTarget? target = serviceProvider.GetService(typeof(IProvideValueTarget)) as IProvideValueTarget;
		object? targetObject = target?.TargetObject;
		bool bindable = target?.TargetProperty is DependencyProperty
			|| targetObject is Setter
			|| (targetObject != null && targetObject.GetType().FullName == "System.Windows.SharedDp");
		if (!bindable)
		{
			return LocalizedStrings.Instance[Key];
		}
		Binding binding = new Binding($"[{Key}]")
		{
			Source = LocalizedStrings.Instance,
			Mode = BindingMode.OneWay
		};
		return binding.ProvideValue(serviceProvider);
	}
}
