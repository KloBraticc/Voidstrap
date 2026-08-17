using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;

namespace Voidstrap.UI.Elements.Controls;

[ContentProperty("InnerContent")]
public partial class OptionControl : UserControl{
	private const double DescriptionChromeWidth = 62d;

	private const double MinimumDescriptionWidth = 80d;

	private static readonly bool ConstrainDescriptionWidth = Voidstrap.Utility.Platform.IsLinux;

	private string? _ownedAutomationHelpText;

	public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register("Header", typeof(string), typeof(OptionControl), (PropertyMetadata)(object)new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure, (PropertyChangedCallback)delegate(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		((OptionControl)(object)d).ApplyHeader(e.NewValue as string);
	}));

	public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register("Description", typeof(string), typeof(OptionControl), (PropertyMetadata)(object)new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure, (PropertyChangedCallback)delegate(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		((OptionControl)(object)d).ApplyDescription(e.NewValue as string);
	}));

	public static readonly DependencyProperty HelpLinkProperty = DependencyProperty.Register("HelpLink", typeof(string), typeof(OptionControl), (PropertyMetadata)(object)new FrameworkPropertyMetadata((object)string.Empty, (PropertyChangedCallback)delegate(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		((OptionControl)(object)d).ApplyHelpLink(e.NewValue as string);
	}));

	public static readonly DependencyProperty InnerContentProperty = DependencyProperty.Register("InnerContent", typeof(object), typeof(OptionControl), (PropertyMetadata)(object)new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange | FrameworkPropertyMetadataOptions.AffectsRender, (PropertyChangedCallback)delegate(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		((OptionControl)(object)d).ApplyInnerContent(e.NewValue);
	}));

	public string Header
	{
		get
		{
			return (string)((DependencyObject)this).GetValue(HeaderProperty);
		}
		set
		{
			((DependencyObject)this).SetValue(HeaderProperty, (object)value);
		}
	}

	public string Description
	{
		get
		{
			return (string)((DependencyObject)this).GetValue(DescriptionProperty);
		}
		set
		{
			((DependencyObject)this).SetValue(DescriptionProperty, (object)value);
		}
	}

	public string HelpLink
	{
		get
		{
			return (string)((DependencyObject)this).GetValue(HelpLinkProperty);
		}
		set
		{
			((DependencyObject)this).SetValue(HelpLinkProperty, (object)value);
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

	public OptionControl()
	{
		InitializeComponent();
		ApplyHeader(Header);
		ApplyDescription(Description);
		ApplyHelpLink(HelpLink);
		ApplyInnerContent(InnerContent);
		base.Loaded += OnLoaded;
		if (ConstrainDescriptionWidth)
		{
			base.SizeChanged += OnSizeChangedForDescription;
		}
	}

	private void OnLoaded(object sender, RoutedEventArgs e)
	{
		base.Loaded -= OnLoaded;
		UpdateDescriptionWidth();
		InvalidateMeasure();
		InvalidateArrange();
		InvalidateVisual();
	}

	private void OnSizeChangedForDescription(object sender, SizeChangedEventArgs e)
	{
		UpdateDescriptionWidth();
	}

	private void UpdateDescriptionWidth()
	{
		if (!ConstrainDescriptionWidth || DescriptionTextBlock is null)
		{
			return;
		}

		double reserved = InnerContentPresenter?.ActualWidth ?? 0d;
		double available = ActualWidth - reserved - DescriptionChromeWidth;
		double target = available >= MinimumDescriptionWidth ? available : double.PositiveInfinity;
		if (Math.Abs(DescriptionTextBlock.MaxWidth - target) > 0.5)
		{
			DescriptionTextBlock.MaxWidth = target;
		}
	}

	private void ApplyHeader(string? value)
	{
		if (HeaderTextBlock != null)
		{
			HeaderTextBlock.Text = value ?? string.Empty;
		}
		ApplyAutomationMetadata();
	}

	private void ApplyDescription(string? value)
	{
		if (DescriptionTextBlock != null)
		{
			DescriptionTextBlock.MarkdownText = value ?? string.Empty;
			DescriptionTextBlock.Visibility = (string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible);
		}
		ApplyAutomationMetadata();
	}

	private void ApplyHelpLink(string? value)
	{
		if (HelpLinkTextBlock != null && HelpLinkHyperlink != null)
		{
			HelpLinkHyperlink.CommandParameter = value;
			HelpLinkTextBlock.Visibility = (string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible);
		}
	}

	private void ApplyInnerContent(object? value)
	{
		if (InnerContentPresenter != null)
		{
			InnerContentPresenter.Content = value;
		}
		UpdateDescriptionWidth();
		ApplyAutomationMetadata();
	}

	private void ApplyAutomationMetadata()
	{
		if (InnerContent is not Wpf.Ui.Controls.ToggleSwitch toggleSwitch)
		{
			return;
		}

		bool hasContent = toggleSwitch.Content switch
		{
			null => false,
			string text => !string.IsNullOrWhiteSpace(text),
			_ => true
		};

		if (hasContent)
		{
			return;
		}

		if (string.IsNullOrWhiteSpace(AutomationProperties.GetName(toggleSwitch)) &&
			AutomationProperties.GetLabeledBy(toggleSwitch) is null &&
			HeaderTextBlock is not null)
		{
			AutomationProperties.SetLabeledBy(toggleSwitch, HeaderTextBlock);
		}

		string currentHelpText = AutomationProperties.GetHelpText(toggleSwitch);
		if (string.IsNullOrWhiteSpace(currentHelpText) || currentHelpText == _ownedAutomationHelpText)
		{
			string helpText = Description ?? string.Empty;
			AutomationProperties.SetHelpText(toggleSwitch, helpText);
			_ownedAutomationHelpText = helpText;
		}
	}
}
