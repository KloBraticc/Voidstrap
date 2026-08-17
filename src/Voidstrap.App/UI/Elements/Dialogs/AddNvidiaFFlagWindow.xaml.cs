using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using Microsoft.Win32;
using Voidstrap.Models;
using Voidstrap.UI.Elements.Base;

namespace Voidstrap.UI.Elements.Dialogs;

public partial class AddNvidiaFFlagWindow : WpfUiWindow
{
    private const long MaxNipBytes = 16L * 1024L * 1024L;
    private const int MaxNipCharacters = 8 * 1024 * 1024;
    private readonly HashSet<uint> _existingSettingIds;

    public List<NvidiaEditorEntry> ResultEntries { get; } = [];

    public AddNvidiaFFlagWindow(IEnumerable<string>? existingSettingIds = null)
    {
        _existingSettingIds = new HashSet<uint>();
        foreach (string rawId in existingSettingIds ?? [])
        {
            if (TryParseSettingId(rawId, out uint id))
                _existingSettingIds.Add(id);
        }

        InitializeComponent();
        UpdateState();
        NameBox.Focus();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        ResultEntries.Clear();
        SingleStatusText.Text = string.Empty;

        bool valid = Tabs.SelectedIndex switch
        {
            0 => TryAddSingle(),
            1 => TryImportNip(),
            2 => TryAddRows(),
            _ => false,
        };

        if (!valid)
            return;

        DialogResult = true;
        Close();
    }

    private bool TryAddSingle()
    {
        string name = NameBox.Text.Trim();
        string rawId = SettingIdBox.Text.Trim();
        string rawValue = ValueBox.Text.Trim();
        string type = GetSelectedValueType();

        if (name.Length == 0)
            return ShowSingleError("Enter a setting name.");

        if (!TryParseSettingId(rawId, out uint id))
            return ShowSingleError("Enter a valid decimal or hex setting ID.");

        if (_existingSettingIds.Contains(id))
            return ShowSingleError("That setting is already in the editor.");

        if (!TryNormalizeValue(rawValue, type, out string value))
            return ShowSingleError("Enter a valid value for the selected type.");

        ResultEntries.Add(CreateEntry(name, id, value, type));
        return true;
    }

    private bool TryImportNip()
    {
        string content = NipTextBox.Text.Trim();
        if (content.Length == 0)
        {
            Frontend.ShowMessageBox("Choose a NIP file or paste its contents.");
            return false;
        }

        if (content.Length > MaxNipCharacters)
        {
            Frontend.ShowMessageBox("That profile is too large to import.");
            return false;
        }

        try
        {
            if (!content.StartsWith('<'))
            {
                string? decoded = TryDecodeNip(content);
                if (decoded == null)
                {
                    Frontend.ShowMessageBox("Paste a valid NIP profile or Base64 NIP.");
                    return false;
                }
                content = decoded;
            }

            XDocument document = XDocument.Parse(content);
            int skipped = 0;
            HashSet<uint> addedIds = new HashSet<uint>(_existingSettingIds);

            foreach (XElement node in document.Descendants().Where(element => element.Name.LocalName.Equals("ProfileSetting", StringComparison.OrdinalIgnoreCase)))
            {
                string rawId = ChildValue(node, "SettingID");
                if (!TryParseSettingId(rawId, out uint id) || !addedIds.Add(id))
                {
                    skipped++;
                    continue;
                }

                string name = ChildValue(node, "SettingNameInfo");
                string type = NormalizeValueType(ChildValue(node, "ValueType"));
                string value = ChildValue(node, "SettingValue");

                ResultEntries.Add(CreateEntry(
                    name.Length == 0 ? "Setting " + id.ToString(CultureInfo.InvariantCulture) : name,
                    id,
                    value.Length == 0 ? "0" : value,
                    type));
            }

            if (ResultEntries.Count == 0)
            {
                Frontend.ShowMessageBox(skipped > 0
                    ? "Every setting in this profile is already in the editor or invalid."
                    : "No valid NVIDIA settings were found.");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Frontend.ShowMessageBox("This NIP profile could not be read:\n\n" + ex.Message);
            return false;
        }
    }

    private bool TryAddRows()
    {
        string input = FullValueBox.Text.Trim();
        if (input.Length == 0)
        {
            Frontend.ShowMessageBox("Paste at least one setting row.");
            return false;
        }

        string[] lines = input.Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        HashSet<uint> addedIds = new HashSet<uint>(_existingSettingIds);

        for (int index = 0; index < lines.Length; index++)
        {
            string[] parts = lines[index].Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length != 4 || parts[0].Length == 0)
                return ShowRowError(index, "Use Name | ID | Value | Type.");

            if (!TryParseSettingId(parts[1], out uint id))
                return ShowRowError(index, "The setting ID is invalid.");

            if (!addedIds.Add(id))
                return ShowRowError(index, "The setting is duplicated or already exists.");

            string type = NormalizeEditableValueType(parts[3]);
            if (type.Length == 0)
                return ShowRowError(index, "The value type must be Dword, Boolean, or Hex.");

            if (!TryNormalizeValue(parts[2], type, out string value))
                return ShowRowError(index, "The value is invalid for its type.");

            ResultEntries.Add(CreateEntry(parts[0], id, value, type));
        }

        return ResultEntries.Count > 0;
    }

    private void ImportFromFile_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new OpenFileDialog
        {
            Title = "Choose NVIDIA Profile",
            Filter = "NVIDIA Profile (*.nip)|*.nip",
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            if (new FileInfo(dialog.FileName).Length > MaxNipBytes)
            {
                Frontend.ShowMessageBox("That profile is too large to import.");
                return;
            }

            NipTextBox.Text = File.ReadAllText(dialog.FileName);
            ImportStatusText.Text = Path.GetFileName(dialog.FileName);
        }
        catch (Exception ex)
        {
            Frontend.ShowMessageBox("This profile could not be opened:\n\n" + ex.Message);
        }
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateState();
    }

    private void Input_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SingleStatusText != null)
            SingleStatusText.Text = string.Empty;
        UpdateState();
    }

    private void Input_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SingleStatusText != null)
            SingleStatusText.Text = string.Empty;
        UpdateState();
    }

    private void UpdateState()
    {
        if (AddButton == null || Tabs == null)
            return;

        AddButton.Content = Tabs.SelectedIndex == 0 ? "Add Setting" : "Import Settings";
        AddButton.IsEnabled = true;

        if (Tabs.SelectedIndex != 1)
            ImportStatusText.Text = string.Empty;
    }

    private bool ShowSingleError(string message)
    {
        SingleStatusText.Text = message;
        return false;
    }

    private static bool ShowRowError(int index, string message)
    {
        Frontend.ShowMessageBox("Row " + (index + 1).ToString(CultureInfo.InvariantCulture) + ": " + message);
        return false;
    }

    private string GetSelectedValueType()
    {
        return NormalizeEditableValueType((ValueTypeBox.SelectedItem as ComboBoxItem)?.Content?.ToString()) switch
        {
            "" => "Dword",
            string type => type,
        };
    }

    private static NvidiaEditorEntry CreateEntry(string name, uint id, string value, string type)
    {
        return new NvidiaEditorEntry
        {
            Name = name.Trim(),
            SettingId = id.ToString(CultureInfo.InvariantCulture),
            Value = value,
            ValueType = type,
        };
    }

    private static string ChildValue(XElement node, string name)
    {
        return node.Elements().FirstOrDefault(element => element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value.Trim() ?? string.Empty;
    }

    private static string? TryDecodeNip(string input)
    {
        try
        {
            string compact = string.Concat(input.Where(character => !char.IsWhiteSpace(character)));
            string decoded = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(compact)).TrimStart('\uFEFF').Trim();
            return decoded.StartsWith('<') ? decoded : null;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string NormalizeValueType(string? type)
    {
        return type?.Trim().ToLowerInvariant() switch
        {
            "string" or "ansistring" => "String",
            "binary" => "Binary",
            "boolean" => "Boolean",
            "hex" => "Hex",
            _ => "Dword",
        };
    }

    private static string NormalizeEditableValueType(string? type)
    {
        return type?.Trim().ToLowerInvariant() switch
        {
            "dword" => "Dword",
            "boolean" or "bool" => "Boolean",
            "hex" => "Hex",
            _ => string.Empty,
        };
    }

    private static bool TryParseSettingId(string? raw, out uint id)
    {
        string value = (raw ?? string.Empty).Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id);

        return uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
    }

    private static bool TryNormalizeValue(string? raw, string type, out string value)
    {
        string input = string.IsNullOrWhiteSpace(raw) ? "0" : raw.Trim();

        if (type == "Boolean")
        {
            if (bool.TryParse(input, out bool boolean))
            {
                value = boolean ? "1" : "0";
                return true;
            }

            if (input is "0" or "1")
            {
                value = input;
                return true;
            }

            value = string.Empty;
            return false;
        }

        if (type == "Hex")
        {
            string digits = input.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? input[2..] : input;
            if (uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hexValue))
            {
                value = "0x" + hexValue.ToString("X8", CultureInfo.InvariantCulture);
                return true;
            }

            value = string.Empty;
            return false;
        }

        if (uint.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint unsignedValue))
        {
            value = unsignedValue.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        if (int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out int signedValue))
        {
            value = unchecked((uint)signedValue).ToString(CultureInfo.InvariantCulture);
            return true;
        }

        if (input.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(input.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint parsedHexValue))
        {
            value = parsedHexValue.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        value = string.Empty;
        return false;
    }
}
