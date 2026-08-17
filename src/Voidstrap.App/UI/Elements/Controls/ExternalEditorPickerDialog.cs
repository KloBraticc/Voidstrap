using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Voidstrap.UI.Elements.Base;
using Voidstrap.Utility;

namespace Voidstrap.UI.Elements.Controls;

public sealed class EditorChoice
{
    public ExternalEditorInfo Editor { get; init; } = new();

    public string Name => Editor.Name;

    public ImageSource? Icon { get; init; }
}

public class ExternalEditorPickerDialog : WpfUiWindow
{
    private const string LOG_IDENT = "ExternalEditorPickerDialog";

    private readonly ListBox _list;

    public ExternalEditorInfo? SelectedEditor => (_list.SelectedItem as EditorChoice)?.Editor;

    public ExternalEditorPickerDialog(IReadOnlyList<ExternalEditorInfo> editors)
    {
        Title = "Open Editor";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ExtendsContentIntoTitleBar = true;
        SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");

        List<EditorChoice> choices = new();
        foreach (ExternalEditorInfo editor in editors)
            choices.Add(new EditorChoice { Editor = editor, Icon = LoadIcon(editor.Path) });

        _list = new ListBox
        {
            ItemsSource = choices,
            SelectedIndex = 0,
            MaxHeight = 340,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            ItemTemplate = BuildTemplate()
        };
        _list.MouseDoubleClick += List_MouseDoubleClick;

        TextBlock header = new()
        {
            Text = "Choose which editor to open the theme in",
            Margin = new Thickness(0, 0, 0, 12),
            TextWrapping = TextWrapping.Wrap
        };

        Button ok = new() { Content = "Open", MinWidth = 110, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        ok.SetResourceReference(StyleProperty, typeof(Button));
        ok.Click += Ok_Click;

        Button cancel = new() { Content = "Cancel", MinWidth = 110, IsCancel = true };

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        StackPanel body = new() { Margin = new Thickness(24, 12, 24, 20) };
        body.Children.Add(header);
        body.Children.Add(_list);
        body.Children.Add(buttons);

        Content = DialogChrome.Host(DialogChrome.TitleBar("Open Editor"), body);
        Closed += ExternalEditorPickerDialog_Closed;
    }

    private static DataTemplate BuildTemplate()
    {
        FrameworkElementFactory panel = new(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        panel.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 6, 2, 6));

        FrameworkElementFactory image = new(typeof(Image));
        image.SetValue(FrameworkElement.WidthProperty, 24.0);
        image.SetValue(FrameworkElement.HeightProperty, 24.0);
        image.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 12, 0));
        image.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding("Icon"));

        FrameworkElementFactory text = new(typeof(TextBlock));
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Name"));

        panel.AppendChild(image);
        panel.AppendChild(text);

        return new DataTemplate { VisualTree = panel };
    }

    private static ImageSource? LoadIcon(string path)
    {
        try
        {
            using System.Drawing.Icon? icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon == null)
                return null;
            ImageSource source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LOG_IDENT, "No icon for " + path + ": " + ex.Message);
            return null;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Accept();

    private void List_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => Accept();

    private void Accept()
    {
        if (_list.SelectedItem == null)
            return;
        DialogResult = true;
        Close();
    }

    private void ExternalEditorPickerDialog_Closed(object? sender, EventArgs e)
    {
        Closed -= ExternalEditorPickerDialog_Closed;
        _list.MouseDoubleClick -= List_MouseDoubleClick;
    }
}
