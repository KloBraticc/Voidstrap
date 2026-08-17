using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media.Animation;
using Voidstrap.UI.ViewModels.About;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.About.Pages;

public partial class AboutPage : UiPage{
	private readonly Queue<Key> _keys = new Queue<Key>();

	private readonly List<Key> _expectedKeys = new List<Key>
	{
		(Key)56,
		(Key)44,
		(Key)63,
		(Key)63,
		(Key)116,
		(Key)35
	};

	private bool _triggered;

	public AboutPage()
	{
		base.DataContext = new AboutViewModel();
		InitializeComponent();
	}

	private void UiPage_KeyDown(object sender, KeyEventArgs e)
	{
		//IL_0024: Unknown result type (might be due to invalid IL or missing references)
		//IL_0029: Unknown result type (might be due to invalid IL or missing references)
		//IL_002a: Unknown result type (might be due to invalid IL or missing references)
		//IL_002d: Invalid comparison between Unknown and I4
		//IL_001d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0038: Unknown result type (might be due to invalid IL or missing references)
		//IL_0031: Unknown result type (might be due to invalid IL or missing references)
		if (!_triggered)
		{
			if (_keys.Count >= 6)
			{
				_keys.Dequeue();
			}
			Key val = e.Key;
			if ((int)val == 117)
			{
				val = (Key)116;
			}
			_keys.Enqueue(val);
			if (_keys.SequenceEqual(_expectedKeys))
			{
				_triggered = true;
				(base.Resources["EggStoryboard"] as Storyboard).Begin();
			}
		}
	}

	private void Anchor_Click(object sender, RoutedEventArgs e)
	{
	}
}
