using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Voidstrap.UI.Elements.Controls;

[TemplatePart(Name = ClearButtonPart, Type = typeof(ButtonBase))]
public class SearchFilterBox : TextBox
{
    private const string ClearButtonPart = "PART_ClearButton";

    public static readonly DependencyProperty FilterProperty = DependencyProperty.Register(
        nameof(Filter),
        typeof(object),
        typeof(SearchFilterBox),
        new PropertyMetadata(null)
    );

    public static readonly DependencyProperty PlaceholderTextProperty = DependencyProperty.Register(
        nameof(PlaceholderText),
        typeof(string),
        typeof(SearchFilterBox),
        new PropertyMetadata(string.Empty)
    );

    private static readonly DependencyPropertyKey IsEmptyPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsEmpty),
        typeof(bool),
        typeof(SearchFilterBox),
        new PropertyMetadata(true)
    );

    public static readonly DependencyProperty IsEmptyProperty = IsEmptyPropertyKey.DependencyProperty;

    private ButtonBase? _clearButton;

    public object? Filter
    {
        get => GetValue(FilterProperty);
        set => SetValue(FilterProperty, value);
    }

    public string PlaceholderText
    {
        get => (string)GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    public bool IsEmpty => (bool)GetValue(IsEmptyProperty);

    public override void OnApplyTemplate()
    {
        if (_clearButton != null)
            _clearButton.Click -= OnClearClick;
        base.OnApplyTemplate();
        _clearButton = GetTemplateChild(ClearButtonPart) as ButtonBase;
        if (_clearButton != null)
            _clearButton.Click += OnClearClick;
    }

    protected override void OnTextChanged(TextChangedEventArgs e)
    {
        SetValue(IsEmptyPropertyKey, string.IsNullOrEmpty(Text));
        base.OnTextChanged(e);
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        Clear();
        Focus();
    }
}
