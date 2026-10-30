using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Voidstrap.UI.Elements.Base;

namespace Voidstrap.UI.Elements.Dialogs;

public partial class TextInputDialog : WpfUiWindow
{
    private readonly string? _requiredValue;

    public bool Confirmed { get; private set; }

    public string Value => ValueBox.Text;

    public string SecondValue => SecondValueBox.Text;

    public TextInputDialog(string prompt, string initial, string? requiredValue = null)
    {
        _requiredValue = requiredValue;
        InitializeComponent();

        PromptText.Text = prompt;
        RequiredValueBox.Text = requiredValue ?? string.Empty;
        RequiredValueBox.Visibility = requiredValue == null ? Visibility.Collapsed : Visibility.Visible;
        ValueBox.Text = initial;
        OkButton.IsEnabled = CanAccept;
        ValueBox.TextChanged += OnValueChanged;

        Loaded += OnDialogLoaded;
        Closed += OnDialogClosed;
    }

    private bool CanAccept => !string.IsNullOrWhiteSpace(ValueBox.Text)
        && (_requiredValue == null || string.Equals(ValueBox.Text, _requiredValue, StringComparison.Ordinal));

    private void OnValueChanged(object sender, TextChangedEventArgs e)
    {
        OkButton.IsEnabled = CanAccept;
    }

    private void OnDialogClosed(object? sender, EventArgs e)
    {
        ValueBox.TextChanged -= OnValueChanged;
        Loaded -= OnDialogLoaded;
        Closed -= OnDialogClosed;
    }

    public TextInputDialog(string prompt, string initial, string secondPrompt, string secondInitial)
        : this(prompt, initial)
    {
        SecondPromptText.Text = secondPrompt;
        SecondPromptText.Visibility = Visibility.Visible;
        SecondValueBox.Text = secondInitial;
        SecondValueBox.Visibility = Visibility.Visible;
    }

    private void OnDialogLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnDialogLoaded;
        ValueBox.Focus();
        ValueBox.SelectAll();
    }

    private void OnValueKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        Accept();
    }

    private void OnOkClicked(object sender, RoutedEventArgs e)
    {
        Accept();
    }

    private void Accept()
    {
        if (!CanAccept)
            return;

        Confirmed = true;
        Close();
    }
}
