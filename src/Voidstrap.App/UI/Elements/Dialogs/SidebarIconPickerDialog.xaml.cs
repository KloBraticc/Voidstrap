using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Voidstrap.UI.Elements.Base;
using Wpf.Ui.Common;

namespace Voidstrap.UI.Elements.Dialogs;

public partial class SidebarIconPickerDialog : WpfUiWindow
{
    public enum PickerChoice
    {
        None,
        Symbol,
        Image,
        Default
    }

    public sealed record SymbolOption(string Name, SymbolRegular Icon);

    private static readonly Lazy<HashSet<int>?> FontCodepoints = new Lazy<HashSet<int>?>(LoadFontCodepoints);

    private static readonly Lazy<List<SymbolOption>> RenderableSymbols = new Lazy<List<SymbolOption>>(() => Enum.GetNames<SymbolRegular>()
        .Where(name => name != nameof(SymbolRegular.Empty))
        .Select(name => new SymbolOption(name, Enum.Parse<SymbolRegular>(name)))
        .Where(option => CanRender(option.Icon))
        .OrderBy(option => option.Name, StringComparer.OrdinalIgnoreCase)
        .ToList());

    private static List<SymbolOption> AllSymbols => RenderableSymbols.Value;

    public static bool CanRender(SymbolRegular icon)
    {
        if (icon == SymbolRegular.Empty)
        {
            return false;
        }
        HashSet<int>? codepoints = FontCodepoints.Value;
        return codepoints == null || codepoints.Contains((int)icon);
    }

    public const string IconFontResourceKey = "FluentSystemIcons";

    public static System.Windows.Media.FontFamily FullIconFont { get; } = Voidstrap.Utility.Platform.IsLinux
        ? Voidstrap.Utility.IconFontLoader.Resolve("FluentSystemIcons-Regular") ?? new System.Windows.Media.FontFamily(new Uri("pack://application:,,,/Resources/Fonts/SymbolIcons/"), "./#FluentSystemIcons-Regular")
        : new System.Windows.Media.FontFamily(new Uri("pack://application:,,,/Resources/Fonts/SymbolIcons/"), "./#FluentSystemIcons-Regular");

    private static HashSet<int>? LoadFontCodepoints()
    {
        try
        {
            foreach (Typeface typeface in FullIconFont.GetTypefaces())
            {
                if (typeface.TryGetGlyphTypeface(out GlyphTypeface glyphs))
                {
                    return new HashSet<int>(glyphs.CharacterToGlyphMap.Keys);
                }
            }
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine("SidebarIconPickerDialog", "The icon font could not be read: " + ex.Message);
        }
        return null;
    }

    private readonly ListCollectionView _symbols = new ListCollectionView(AllSymbols);

    private string _query = "";

    public PickerChoice Choice { get; private set; }

    public SymbolRegular SelectedIcon { get; private set; }

    public SidebarIconPickerDialog(SymbolRegular currentIcon)
    {
        Resources[IconFontResourceKey] = FullIconFont;
        InitializeComponent();
        _symbols.Filter = FilterSymbol;
        IconList.ItemsSource = _symbols;
        IconList.SelectedItem = AllSymbols.FirstOrDefault(item => item.Icon == currentIcon) ?? AllSymbols.FirstOrDefault();
    }

    private bool FilterSymbol(object item)
    {
        return item is SymbolOption option
            && (_query.Length == 0 || option.Name.Contains(_query, StringComparison.OrdinalIgnoreCase));
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        IconList.ScrollIntoView(IconList.SelectedItem);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _query = SearchBox.Text.Trim();
        _symbols.Refresh();
        if (IconList.SelectedItem == null && _symbols.Count > 0)
        {
            IconList.SelectedIndex = 0;
        }
    }

    private void UseIconButton_Click(object sender, RoutedEventArgs e)
    {
        if (IconList.SelectedItem is not SymbolOption option)
        {
            return;
        }
        SelectedIcon = option.Icon;
        Choice = PickerChoice.Symbol;
        DialogResult = true;
    }

    private void CustomImageButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = PickerChoice.Image;
        DialogResult = true;
    }

    private void DefaultButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = PickerChoice.Default;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
