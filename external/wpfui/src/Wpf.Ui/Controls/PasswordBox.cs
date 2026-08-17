// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;
using System.Windows;
using System.Windows.Controls;

namespace Wpf.Ui.Controls;

/**
 * TextProperty contains asterisks OR raw password if IsPasswordRevealed is set to true
 * PasswordProperty always contains raw password
 */

/// <summary>
/// The modified password control.
/// </summary>
public class PasswordBox : Wpf.Ui.Controls.TextBox
{
    private bool _lockUpdatingContents;

    /// <summary>
    /// Property for <see cref="Password"/>.
    /// </summary>
    public static readonly DependencyProperty PasswordProperty = DependencyProperty.Register(nameof(Password),
        typeof(string), typeof(PasswordBox), new PropertyMetadata(String.Empty, OnPasswordPropertyChanged));

    /// <summary>
    /// Property for <see cref="PasswordChar"/>.
    /// </summary>
    public static readonly DependencyProperty PasswordCharProperty = DependencyProperty.Register(nameof(PasswordChar),
        typeof(char), typeof(PasswordBox), new PropertyMetadata('*', OnPasswordCharPropertyChanged));

    /// <summary>
    /// Property for <see cref="IsPasswordRevealed"/>.
    /// </summary>
    public static readonly DependencyProperty IsPasswordRevealedProperty = DependencyProperty.Register(nameof(IsPasswordRevealed),
        typeof(bool), typeof(PasswordBox), new PropertyMetadata(false, OnPasswordRevealModePropertyChanged));

    /// <summary>
    /// Property for <see cref="RevealButtonEnabled"/>.
    /// </summary>
    public static readonly DependencyProperty RevealButtonEnabledProperty = DependencyProperty.Register(nameof(RevealButtonEnabled),
        typeof(bool), typeof(PasswordBox), new PropertyMetadata(true));

    /// <summary>
    /// Event for "Password has changed"
    /// </summary>
    public static readonly RoutedEvent PasswordChangedEvent = EventManager.RegisterRoutedEvent(
        nameof(PasswordChanged),
        RoutingStrategy.Bubble,
        typeof(RoutedEventHandler),
        typeof(PasswordBox));

    /// <summary>
    /// Gets or sets currently typed text represented by asterisks.
    /// </summary>
    public string Password
    {
        get => (string)GetValue(PasswordProperty);
        set => SetValue(PasswordProperty, value);
    }

    /// <summary>
    /// Gets or sets character used to mask the password.
    /// </summary>
    public char PasswordChar
    {
        get => (char)GetValue(PasswordCharProperty);
        set => SetValue(PasswordCharProperty, value);
    }

    /// <summary>
    /// Gets a value indicating whether the password is revealed.
    /// </summary>
    public bool IsPasswordRevealed
    {
        get => (bool)GetValue(IsPasswordRevealedProperty);
        private set => SetValue(IsPasswordRevealedProperty, value);
    }

    /// <summary>
    /// Gets or sets a value deciding whether to display the reveal password button.
    /// </summary>
    public bool RevealButtonEnabled
    {
        get => (bool)GetValue(RevealButtonEnabledProperty);
        set => SetValue(RevealButtonEnabledProperty, value);
    }

    /// <summary>
    /// Event fired from this text box when its inner content
    /// has been changed.
    /// </summary>
    /// <remarks>
    /// It is redirected from inner TextContainer.Changed event.
    /// </remarks>
    public event RoutedEventHandler PasswordChanged
    {
        add => AddHandler(PasswordChangedEvent, value);
        remove => RemoveHandler(PasswordChangedEvent, value);
    }

    public PasswordBox()
    {
        _lockUpdatingContents = false;
    }

    /// <inheritdoc />
    protected override void OnTextChanged(TextChangedEventArgs e)
    {
        UpdateTextContents(true, e);

        if (_lockUpdatingContents)
        {
            base.OnTextChanged(e);
        }
        else
        {
            if (PlaceholderEnabled && Text.Length > 0)
                PlaceholderEnabled = false;

            if (!PlaceholderEnabled && Text.Length < 1)
                PlaceholderEnabled = true;

            RevealClearButton();
        }
    }

    /// <summary>
    /// Is called when <see cref="Password"/> property is changing.
    /// </summary>
    protected virtual void OnPasswordChanged()
    {
        UpdateTextContents(false, null);
    }

    /// <summary>
    /// Is called when <see cref="PasswordChar"/> property is changing.
    /// </summary>
    protected virtual void OnPasswordCharChanged()
    {
        // If password is currently revealed,
        // do not replace displayed text with asterisks
        if (IsPasswordRevealed)
            return;

        _lockUpdatingContents = true;
        try
        {
            Text = new String(PasswordChar, Password.Length);
        }
        finally
        {
            _lockUpdatingContents = false;
        }
    }

    protected virtual void OnPasswordRevealModeChanged()
    {
        _lockUpdatingContents = true;
        try
        {
            Text = IsPasswordRevealed ? Password : new String(PasswordChar, Password.Length);
        }
        finally
        {
            _lockUpdatingContents = false;
        }
    }

    /// <summary>
    /// Triggered by clicking a button in the control template.
    /// </summary>
    /// <param name="sender">Sender of the click event.</param>
    /// <param name="parameter">Additional parameters.</param>
    protected override void OnTemplateButtonClick(object sender, object parameter)
    {
        base.OnTemplateButtonClick(sender, parameter);

        if (parameter is not string parameterString)
            return;

#if DEBUG
        System.Diagnostics.Debug.WriteLine($"INFO: {typeof(PasswordBox)} button clicked with param: {parameterString}", "Wpf.Ui.PasswordBox");
#endif

        switch (parameterString)
        {
            case "reveal":
                IsPasswordRevealed = !IsPasswordRevealed;
                Focus();
                CaretIndex = Text.Length;

                break;
        }
    }

    private void UpdateTextContents(bool isTriggeredByTextInput, TextChangedEventArgs? args)
    {
        if (_lockUpdatingContents)
            return;

        if (IsPasswordRevealed)
        {
            if (Password == Text)
                return;

            _lockUpdatingContents = true;
            try
            {
                if (isTriggeredByTextInput)
                {
                    Password = Text;
                }
                else
                {
                    Text = Password;
                    CaretIndex = Text.Length;
                }

                RaiseEvent(new RoutedEventArgs(PasswordChangedEvent));
            }
            finally
            {
                _lockUpdatingContents = false;
            }
            return;
        }

        var caretIndex = CaretIndex;
        var newPasswordValue = Password;

        if (isTriggeredByTextInput && args != null)
        {
            foreach (TextChange change in args.Changes)
            {
                int offset = Math.Clamp(change.Offset, 0, newPasswordValue.Length);
                int removed = Math.Min(change.RemovedLength, newPasswordValue.Length - offset);
                if (removed > 0)
                    newPasswordValue = newPasswordValue.Remove(offset, removed);
                int available = Math.Max(0, Text.Length - change.Offset);
                int added = Math.Min(change.AddedLength, available);
                if (added > 0)
                {
                    string inserted = Text.Substring(change.Offset, added).Replace(PasswordChar.ToString(), String.Empty);
                    if (inserted.Length > 0)
                        newPasswordValue = newPasswordValue.Insert(Math.Min(offset, newPasswordValue.Length), inserted);
                }
            }
        }

        _lockUpdatingContents = true;
        try
        {
            Text = new String(PasswordChar, newPasswordValue.Length);
            Password = newPasswordValue;
            CaretIndex = Math.Min(caretIndex, Text.Length);
            RaiseEvent(new RoutedEventArgs(PasswordChangedEvent));
        }
        finally
        {
            _lockUpdatingContents = false;
        }
    }

    /// <summary>
    /// Called when <see cref="Password"/> is changed.
    /// </summary>
    private static void OnPasswordPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox control)
            return;

        control.OnPasswordChanged();
    }

    /// <summary>
    /// Called if the character is changed in the during the run.
    /// </summary>
    private static void OnPasswordCharPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox control)
            return;

        control.OnPasswordCharChanged();
    }

    /// <summary>
    /// Called if the reveal mode is changed in the during the run.
    /// </summary>
    private static void OnPasswordRevealModePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox control)
            return;

        control.OnPasswordRevealModeChanged();
    }
}
