using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using Voidstrap.Resources;
using Voidstrap.UI.Elements.Base;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Dialogs;

public partial class AddFastFlagDialog : WpfUiWindow{
	private const long MaximumImportBytes = 4_000_000;

	private const int MaximumImportedFlags = 20_000;

	private const int MaximumBase64Characters = 5_500_000;

	public MessageBoxResult Result = MessageBoxResult.Cancel;

	public List<FastFlagItem> ImportedFlags { get; private set; } = new List<FastFlagItem>();

	public AddFastFlagDialog()
	{
		InitializeComponent();
	}

	private async void ImportButton_Click(object sender, RoutedEventArgs e)
	{
		OpenFileDialog openFileDialog = new OpenFileDialog
		{
			Filter = Strings.FileTypes_JSONFiles + " (*.json;*.txt;*.md)|*.json;*.txt;*.md"
		};
		if (openFileDialog.ShowDialog() == true)
		{
			try
			{
				FileInfo file = new FileInfo(openFileDialog.FileName);
				if (!file.Exists || file.Length <= 0 || file.Length > MaximumImportBytes)
				{
					Frontend.ShowMessageBox("That flag file is too large to import.");
					return;
				}
				string text = await ReadImportTextAsync(openFileDialog.FileName);
				if (text.Length > MaximumImportBytes)
				{
					Frontend.ShowMessageBox("That flag file is too large to import.");
					return;
				}
				JsonTextBox.Text = text;
				ParseJsonToFlags(text);
			}
			catch (Exception ex)
			{
				Frontend.ShowMessageBox("That flag file could not be imported: " + ex.Message);
			}
		}
	}

	private static async Task<string> ReadImportTextAsync(string path)
	{
		await using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
		if (stream.Length <= 0 || stream.Length > MaximumImportBytes)
			throw new InvalidDataException("The flag file size is invalid");
		byte[] data = new byte[checked((int)stream.Length)];
		int offset = 0;
		while (offset < data.Length)
		{
			int read = await stream.ReadAsync(data.AsMemory(offset), CancellationToken.None);
			if (read == 0)
				throw new EndOfStreamException();
			offset += read;
		}
		if (await stream.ReadAsync(new byte[1], CancellationToken.None) != 0)
			throw new InvalidDataException("The flag file changed while it was being read");
		using MemoryStream memory = new MemoryStream(data, writable: false);
		using StreamReader reader = new StreamReader(memory, Encoding.UTF8, true);
		return await reader.ReadToEndAsync();
	}

	private void JsonTextBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		ParseJsonToFlags(JsonTextBox.Text);
	}

	private void ParseJsonToFlags(string json)
	{
		ImportedFlags.Clear();
		if (string.IsNullOrWhiteSpace(json) || json.Length > 4_000_000)
		{
			return;
		}
		try
		{
			List<JProperty> properties = JObject.Parse(json).Properties().Take(MaximumImportedFlags + 1).ToList();
			if (properties.Count > MaximumImportedFlags)
				return;
			Dictionary<string, string> source = properties.ToDictionary((JProperty p) => p.Name, (JProperty p) => p.Value.ToString());
			ImportedFlags = source.Select(delegate(KeyValuePair<string, string> kvp)
			{
				string item = "Unknown";
				if (kvp.Key.StartsWith("FFlag"))
				{
					item = "FFlag";
				}
				else if (kvp.Key.StartsWith("DFFlag"))
				{
					item = "DFFlag";
				}
				else if (kvp.Key.StartsWith("FInt"))
				{
					item = "FInt";
				}
				else if (kvp.Key.StartsWith("DFInt"))
				{
					item = "DFInt";
				}
				else if (kvp.Key.StartsWith("FString"))
				{
					item = "FString";
				}
				else if (kvp.Key.StartsWith("FDouble"))
				{
					item = "FDouble";
				}
				return new FastFlagItem
				{
					Name = kvp.Key,
					Value = kvp.Value,
					VisibleTags = new List<string> { item }
				};
			}).ToList();
			UpdateBase64Tab();
		}
		catch
		{
		}
	}

	private void UpdateBase64Tab()
	{
		if (ImportedFlags.Any())
		{
			string s = JObject.FromObject(ImportedFlags.ToDictionary((FastFlagItem f) => f.Name, (FastFlagItem f) => f.Value)).ToString();
			string text = Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
			Base64TextBox.Text = text;
		}
	}

	public static string? TryDecodeBase64(string input)
	{
		if (string.IsNullOrWhiteSpace(input) || input.Length > MaximumBase64Characters)
		{
			return null;
		}
		StringBuilder sb = new StringBuilder(input.Length);
		foreach (char c in input)
		{
			if (char.IsWhiteSpace(c))
			{
				continue;
			}
			if (c == '-')
			{
				sb.Append('+');
			}
			else if (c == '_')
			{
				sb.Append('/');
			}
			else
			{
				sb.Append(c);
			}
		}
		string s = sb.ToString();
		int remainder = s.Length % 4;
		if (remainder == 1)
		{
			return null;
		}
		if (remainder == 2)
		{
			s += "==";
		}
		else if (remainder == 3)
		{
			s += "=";
		}
		try
		{
			string json = Encoding.UTF8.GetString(Convert.FromBase64String(s));
			return json.TrimStart().StartsWith('{') ? json : null;
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

	private void PasteBase64Button_Click(object sender, RoutedEventArgs e)
	{
		string text = Base64TextBox.Text;
		if (string.IsNullOrWhiteSpace(text))
		{
			try
			{
				text = Clipboard.GetText();
			}
			catch
			{
				text = string.Empty;
			}
			if (string.IsNullOrWhiteSpace(text))
			{
				return;
			}
			Base64TextBox.Text = text;
		}
		if (text.TrimStart().StartsWith('{'))
		{
			JsonTextBox.Text = text;
			Tabs.SelectedIndex = 1;
			return;
		}
		string? json = TryDecodeBase64(text);
		if (json == null)
		{
			Frontend.ShowMessageBox("Invalid Base64 string!");
			return;
		}
		JsonTextBox.Text = json;
		Tabs.SelectedIndex = 1;
	}

	private void PresetValuesButton_Click(object sender, RoutedEventArgs e)
	{
		FFlagPresetsDialog fFlagPresetsDialog = new FFlagPresetsDialog();
		if (fFlagPresetsDialog.ShowDialog() == true && !string.IsNullOrEmpty(fFlagPresetsDialog.SelectedValue))
		{
			FlagValueTextBox.Text = fFlagPresetsDialog.SelectedValue;
		}
	}

	private void OKButton_Click(object sender, RoutedEventArgs e)
	{
		Result = MessageBoxResult.OK;
		Close();
	}
}
