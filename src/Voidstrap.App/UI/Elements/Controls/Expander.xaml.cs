using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Threading;
using Wpf.Ui.Common;

namespace Voidstrap.UI.Elements.Controls;

[ContentProperty("InnerContent")]
public partial class Expander : UserControl{
	public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register("IsExpanded", typeof(bool), typeof(Expander));

	public static readonly DependencyProperty HeaderIconProperty = DependencyProperty.Register("HeaderIcon", typeof(SymbolRegular), typeof(Expander));

	public static readonly DependencyProperty HeaderTextProperty = DependencyProperty.Register("HeaderText", typeof(string), typeof(Expander));

	public static readonly DependencyProperty InnerContentProperty = DependencyProperty.Register("InnerContent", typeof(object), typeof(Expander));

	public bool IsExpanded
	{
		get
		{
			return (bool)((DependencyObject)this).GetValue(IsExpandedProperty);
		}
		set
		{
			((DependencyObject)this).SetValue(IsExpandedProperty, (object)value);
		}
	}

	public string HeaderText
	{
		get
		{
			return (string)((DependencyObject)this).GetValue(HeaderTextProperty);
		}
		set
		{
			((DependencyObject)this).SetValue(HeaderTextProperty, (object)value);
		}
	}

	public SymbolRegular HeaderIcon
	{
		get
		{
			return (SymbolRegular)((DependencyObject)this).GetValue(HeaderIconProperty);
		}
		set
		{
			((DependencyObject)this).SetValue(HeaderIconProperty, (object)value);
		}
	}

	public object InnerContent
	{
		get
		{
			return ((DependencyObject)this).GetValue(InnerContentProperty);
		}
		set
		{
			((DependencyObject)this).SetValue(InnerContentProperty, value);
		}
	}

	public Expander()
	{
		InitializeComponent();
		Loaded += OnExpanderLoaded;
	}

	protected override void OnInitialized(EventArgs e)
	{
		base.OnInitialized(e);
		QueueStableLinuxChevron();
	}

	private void OnExpanderLoaded(object sender, RoutedEventArgs e)
	{
		Loaded -= OnExpanderLoaded;
		QueueStableLinuxChevron();
	}

	private void QueueStableLinuxChevron()
	{
		if (OperatingSystem.IsLinux() && !Wpf.Ui.Controls.ExpanderMotion.GetUseLinuxAnimationClock(this))
		{
			Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ApplyStableLinuxChevron));
		}
	}

	private void ApplyStableLinuxChevron()
	{
		RootExpander.ApplyTemplate();
		if (RootExpander.Template.FindName("ExpanderToggleButton", RootExpander) is ToggleButton toggle
			&& Resources["StableLinuxExpanderToggleButtonStyle"] is ControlTemplate template)
		{
			toggle.Template = template;
		}
	}
}
