using System.Windows;
using System.Windows.Input;
using Voidstrap.UI.Elements.Base;

namespace Voidstrap.UI.Elements.Dialogs;

public partial class TextInputDialog : WpfUiWindow
{
	public bool Confirmed { get; private set; }

	public string Value => ValueBox.Text;

	public string SecondValue => SecondValueBox.Text;

	public TextInputDialog(string prompt, string initial)
	{
		InitializeComponent();

		PromptText.Text = prompt;
		ValueBox.Text = initial;

		Loaded += OnDialogLoaded;
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
		if (string.IsNullOrWhiteSpace(ValueBox.Text))
			return;

		Confirmed = true;
		Close();
	}
}
