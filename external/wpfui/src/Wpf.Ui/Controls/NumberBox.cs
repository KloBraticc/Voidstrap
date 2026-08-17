// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Wpf.Ui.Controls;

/// <summary>
/// Text field for entering numbers with the possibility of specifying a pattern.
/// </summary>
[ToolboxItem(true)]
[ToolboxBitmap(typeof(NumberBox), "NumberBox.bmp")]
public class NumberBox : Wpf.Ui.Controls.TextBox
{
    // In both expressions, we allow the lonely characters '-', '.' and ',' so the numbers can be typed in real-time.

    /// <summary>
    /// Accepts a string of digits separated by a comma or period. Allows for a leading minus sign.
    /// </summary>
    private static readonly Regex DecimalExpression = new(@"^\-?(\d+(?:[\.\,]|[\.\,]\d+)?)?$", RegexOptions.Compiled);

    /// <summary>
    /// Accepts a string of digits only. Allows for a leading minus sign.
    /// </summary>
    private static readonly Regex IntegerExpression = new(@"^\-?(\d+)*$", RegexOptions.Compiled);
    private bool _updatingText;

    /// <summary>
    /// Property for <see cref="Value"/>.
    /// </summary>
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value),
        typeof(double), typeof(NumberBox), new PropertyMetadata(0.0d, OnValuePropertyChanged));

    /// <summary>
    /// Property for <see cref="Step"/>.
    /// </summary>
    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(nameof(Step),
        typeof(double), typeof(NumberBox), new PropertyMetadata(1.0d));

    /// <summary>
    /// Property for <see cref="Max"/>.
    /// </summary>
    public static readonly DependencyProperty MaxProperty = DependencyProperty.Register(nameof(Max),
        typeof(double), typeof(NumberBox), new PropertyMetadata(Double.MaxValue));

    /// <summary>
    /// Property for <see cref="Min"/>.
    /// </summary>
    public static readonly DependencyProperty MinProperty = DependencyProperty.Register(nameof(Min),
        typeof(double), typeof(NumberBox), new PropertyMetadata(Double.MinValue));

    /// <summary>
    /// Property for <see cref="DecimalPlaces"/>.
    /// </summary>
    public static readonly DependencyProperty DecimalPlacesProperty = DependencyProperty.Register(nameof(DecimalPlaces),
        typeof(int), typeof(NumberBox), new PropertyMetadata(2, OnDecimalPlacesChanged));

    /// <summary>
    /// Property for <see cref="Mask"/>.
    /// </summary>
    public static readonly DependencyProperty MaskProperty = DependencyProperty.Register(nameof(Mask),
        typeof(string), typeof(NumberBox), new PropertyMetadata(String.Empty));

    /// <summary>
    /// Property for <see cref="SpinButtonsEnabled"/>.
    /// </summary>
    public static readonly DependencyProperty SpinButtonsEnabledProperty = DependencyProperty.Register(nameof(SpinButtonsEnabled),
        typeof(bool), typeof(NumberBox), new PropertyMetadata(true));

    /// <summary>
    /// Property for <see cref="IntegersOnly"/>.
    /// </summary>
    public static readonly DependencyProperty IntegersOnlyProperty = DependencyProperty.Register(nameof(IntegersOnly),
        typeof(bool), typeof(NumberBox), new PropertyMetadata(false));

    /// <summary>
    /// Routed event for <see cref="Incremented"/>.
    /// </summary>
    public static readonly RoutedEvent IncrementedEvent = EventManager.RegisterRoutedEvent(
        nameof(Incremented), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(NumberBox));

    /// <summary>
    /// Routed event for <see cref="Decremented"/>.
    /// </summary>
    public static readonly RoutedEvent DecrementedEvent = EventManager.RegisterRoutedEvent(
        nameof(Decremented), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(NumberBox));

    /// <summary>
    /// <see cref="NumberBox"/> does no accept returns.
    /// </summary>
    public new bool AcceptsReturn
    {
        get => false;
        set { }
    }

    /// <summary>
    /// <see cref="NumberBox"/> does not accept changes to the number of lines.
    /// </summary>
    public new int MaxLines
    {
        get => 1;
        set { }
    }

    /// <summary>
    /// <see cref="NumberBox"/> does not accept changes to the number of lines.
    /// </summary>
    public new int MinLines
    {
        get => 1;
        set { }
    }

    /// <summary>
    /// Gets or sets current numeric value.
    /// </summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>
    /// Gets or sets value by which the given number will be increased or decreased after pressing the button.
    /// </summary>
    public double Step
    {
        get => (double)GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    /// <summary>
    /// Maximum allowable value.
    /// </summary>
    public double Max
    {
        get => (double)GetValue(MaxProperty);
        set => SetValue(MaxProperty, value);
    }

    /// <summary>
    /// Minimum allowable value.
    /// </summary>
    public double Min
    {
        get => (double)GetValue(MinProperty);
        set => SetValue(MinProperty, value);
    }

    /// <summary>
    /// Number of decimal places.
    /// </summary>
    public int DecimalPlaces
    {
        get => (int)GetValue(DecimalPlacesProperty);
        set => SetValue(DecimalPlacesProperty, value);
    }

    /// <summary>
    /// Gets or sets numbers pattern.
    /// </summary>
    public string Mask
    {
        get => (string)GetValue(MaskProperty);
        set => SetValue(MaskProperty, value);
    }

    /// <summary>
    /// Gets or sets value determining whether to display the button controls.
    /// </summary>
    public bool SpinButtonsEnabled
    {
        get => (bool)GetValue(SpinButtonsEnabledProperty);
        set => SetValue(SpinButtonsEnabledProperty, value);
    }

    /// <summary>
    /// Gets or sets value which determines whether only integers can be entered.
    /// </summary>
    public bool IntegersOnly
    {
        get => (bool)GetValue(IntegersOnlyProperty);
        set => SetValue(IntegersOnlyProperty, value);
    }

    /// <summary>
    /// Event occurs when a value is incremented by button or arrow key.
    /// </summary>
    public event RoutedEventHandler Incremented
    {
        add => AddHandler(IncrementedEvent, value);
        remove => RemoveHandler(IncrementedEvent, value);
    }

    /// <summary>
    /// Event occurs when a value is decremented by button or arrow key.
    /// </summary>
    public event RoutedEventHandler Decremented
    {
        add => AddHandler(DecrementedEvent, value);
        remove => RemoveHandler(DecrementedEvent, value);
    }

    static NumberBox()
    {
        AcceptsReturnProperty.OverrideMetadata(typeof(NumberBox), new FrameworkPropertyMetadata(false));
        MaxLinesProperty.OverrideMetadata(typeof(NumberBox), new FrameworkPropertyMetadata(1));
        MinLinesProperty.OverrideMetadata(typeof(NumberBox), new FrameworkPropertyMetadata(1));
    }

    /// <summary>
    /// Creates new instance of <see cref="NumberBox"/>.
    /// </summary>
    public NumberBox()
    {
        DataObject.AddPastingHandler(this, OnClipboardPaste);

        Loaded += OnLoaded;
    }

    protected virtual void OnValueChanged()
    {
        if (_updatingText)
            return;
        SetText(FormatDoubleToString(Value));
    }

    /// <inheritdoc/>
    protected override void OnTemplateButtonClick(object sender, object parameter)
    {
        base.OnTemplateButtonClick(sender, parameter);

        if (sender is not NumberBox)
            return;

        if (parameter is not string parameterString)
            return;

        switch (parameterString)
        {
            case "increment":
                IncrementValue();
                break;

            case "decrement":
                DecrementValue();
                break;
        }
    }

    /// <summary>
    /// Updates <see cref="Value"/> and <see cref="System.Windows.Controls.TextBox.Text"/>.
    /// </summary>
    private void UpdateValue(double value, bool updateText)
    {
        double clamped = Math.Min(Max, Math.Max(Min, value));
        _updatingText = true;
        try
        {
            SetCurrentValue(ValueProperty, clamped);
            if (updateText)
                SetText(FormatDoubleToString(clamped));
        }
        finally
        {
            _updatingText = false;
        }
    }

    /// <summary>
    /// Increments current <see cref="Value"/>.
    /// </summary>
    private void IncrementValue()
    {
        double previous = Value;
        UpdateValue(Value + Step, true);
        if (!Value.Equals(previous))
            OnIncremented();
    }

    /// <summary>
    /// Decrements current <see cref="Value"/>.
    /// </summary>
    private void DecrementValue()
    {
        double previous = Value;
        UpdateValue(Value - Step, true);
        if (!Value.Equals(previous))
            OnDecremented();
    }

    /// <summary>
    /// Formats double number according to configuration.
    /// </summary>
    private string FormatDoubleToString(double number)
    {
        if (!String.IsNullOrWhiteSpace(Mask))
        {
            try
            {
                return number.ToString(Mask, CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
            }
        }

        if (IntegersOnly || DecimalPlaces < 1)
            return number.ToString("F0", CultureInfo.InvariantCulture);

        if (DecimalPlaces < 5)
            return number.ToString($"F{DecimalPlaces}", CultureInfo.InvariantCulture);

        return number.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Tests provided text with regular expression according to configuration.
    /// </summary>
    private bool IsNumberTextValid(string inputText)
    {
        var decimalPlaces = DecimalPlaces;

        if (IntegersOnly || decimalPlaces < 1)
            return IntegerExpression.IsMatch(inputText);

        if (!DecimalExpression.IsMatch(inputText))
            return false;

        int separator = inputText.IndexOfAny(new[] { '.', ',' });
        return separator < 0 || inputText.Length - separator - 1 <= decimalPlaces;
    }

    /// <summary>
    /// Tries to parse provided string to double with invariant culture.
    /// </summary>
    private double ParseStringToDouble(string inputText)
    {
        Double.TryParse(inputText.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double number);

        return number;
    }

    /// <summary>
    /// Occurs when controls is loaded.
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SetText(FormatDoubleToString(Value));
    }

    /// <inheritdoc />
    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.Key == Key.Up)
        {
            IncrementValue();

            e.Handled = true;
        }

        if (e.Key == Key.Down)
        {
            DecrementValue();

            e.Handled = true;
        }

        base.OnKeyUp(e);
    }

    /// <inheritdoc />
    protected override void OnTextChanged(TextChangedEventArgs e)
    {
        base.OnTextChanged(e);

        if (_updatingText)
            return;

        var currentText = Text;
        PlaceholderEnabled = currentText.Length < 1;

        if (String.IsNullOrWhiteSpace(currentText) || currentText == "-" || currentText == "." || currentText == ",")
            return;

        if (Double.TryParse(currentText.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedNumber))
            UpdateValue(parsedNumber, false);
    }

    /// <inheritdoc />
    protected override void OnPreviewTextInput(TextCompositionEventArgs e)
    {
        var newText = Text.Remove(SelectionStart, SelectionLength).Insert(SelectionStart, e.Text ?? String.Empty);

        if (!String.IsNullOrEmpty(newText))
            e.Handled = !IsNumberTextValid(newText);

        // Do not allow a leading minus sign if the min value is greater than zero.
        if (Min >= 0 && newText.StartsWith("-"))
            e.Handled = true;


        base.OnPreviewTextInput(e);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        SetText(FormatDoubleToString(Value));
    }

    /// <summary>
    /// This virtual method is called after incrementing a value using button or arrow key.
    /// </summary>
    protected virtual void OnIncremented()
    {
        RaiseEvent(new RoutedEventArgs(IncrementedEvent, this));
    }

    /// <summary>
    /// This virtual method is called after decrementing a value using button or arrow key.
    /// </summary>
    protected virtual void OnDecremented()
    {
        RaiseEvent(new RoutedEventArgs(DecrementedEvent, this));
    }

    /// <summary>
    /// This virtual method is called after <see cref="DecimalPlaces"/> is changed.
    /// </summary>
    protected virtual void OnDecimalPlacesChanged(int decimalPlaces)
    {
        if (decimalPlaces < 0)
            DecimalPlaces = 0;
    }



    private void OnClipboardPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (sender is not NumberBox control)
            return;

        var clipboardText = e.DataObject.GetData(typeof(string)) as string;
        if (clipboardText == null)
        {
            e.CancelCommand();
            return;
        }

        string candidate = Text.Remove(SelectionStart, SelectionLength).Insert(SelectionStart, clipboardText);

        if (!IsNumberTextValid(candidate) || (Min >= 0 && candidate.StartsWith("-")))
            e.CancelCommand();
    }

    private void SetText(string value)
    {
        bool wasUpdatingText = _updatingText;
        _updatingText = true;
        try
        {
            Text = value;
            CaretIndex = value.Length;
        }
        finally
        {
            _updatingText = wasUpdatingText;
        }
    }

    private static void OnValuePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not NumberBox numberBox)
            return;

        numberBox.OnValueChanged();
    }

    private static void OnDecimalPlacesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not NumberBox control)
            return;

        if (e.NewValue is not int newValue)
            return;

        control.OnDecimalPlacesChanged(newValue);
    }
}
