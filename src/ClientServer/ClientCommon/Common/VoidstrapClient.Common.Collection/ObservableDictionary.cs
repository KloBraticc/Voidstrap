using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;

namespace VoidstrapClient.Common.Collection;

[Serializable]
public class ObservableDictionary<TKey, TValue> : Dictionary<TKey, TValue>, INotifyCollectionChanged, INotifyPropertyChanged where TKey : notnull
{
	public new TValue this[TKey key]
	{
		get
		{
			return base[key];
		}
		set
		{
			base[key] = value;
			NotifyChange();
		}
	}

	public event NotifyCollectionChangedEventHandler? CollectionChanged;

	public event PropertyChangedEventHandler? PropertyChanged;

	private void NotifyChange()
	{
		this.CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Count"));
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Keys"));
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Values"));
	}

	public new void Add(TKey key, TValue value)
	{
		base.Add(key, value);
		NotifyChange();
	}

	public new bool Remove(TKey key)
	{
		bool num = base.Remove(key);
		if (num)
		{
			NotifyChange();
		}
		return num;
	}

	public new void Clear()
	{
		base.Clear();
		NotifyChange();
	}

	public new bool TryAdd(TKey key, TValue value)
	{
		bool num = base.TryAdd(key, value);
		if (num)
		{
			NotifyChange();
		}
		return num;
	}
}
