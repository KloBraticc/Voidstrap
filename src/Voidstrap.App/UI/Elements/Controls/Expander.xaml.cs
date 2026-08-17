using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
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
			((DependencyObject)this).SetValue(HeaderTextProperty, (object)value);
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
	}
}
