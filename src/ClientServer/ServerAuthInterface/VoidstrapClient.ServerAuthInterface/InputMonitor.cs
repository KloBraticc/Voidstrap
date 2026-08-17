using System;
using System.Threading;
using System.Threading.Tasks;
using VoidstrapClient.Common;
using TextCopy;

namespace VoidstrapClient.ServerAuthInterface;

internal static class InputMonitor
{
	public static void DisplayHelpMenu()
	{
		lock (Logger.Instance)
		{
			Logger.Instance.Info("Commands:");
			Logger.Instance.Info("C: Display the commands menu");
			Logger.Instance.Info("K: Generate a one use key");
			Logger.Instance.Info("I: Generate an infinite use key");
			Logger.Instance.Info("Key generation commands copy the key to the clipboard.");
		}
	}

	public static void Start(CancellationToken token)
	{
		while (!token.IsCancellationRequested)
		{
			switch (Console.ReadKey(intercept: true).Key)
			{
			case ConsoleKey.C:
				DisplayHelpMenu();
				break;
			case ConsoleKey.K:
			{
				string text2 = KeyService.Instance.GenerateKey(infinite: false);
				Logger.Instance.Info("One use key generated and copied to the clipboard");
				_ = SetClipboardAsync(text2);
				break;
			}
			case ConsoleKey.I:
			{
				string text = KeyService.Instance.GenerateKey(infinite: true);
				Logger.Instance.Info("Infinite use key generated and copied to the clipboard");
				_ = SetClipboardAsync(text);
				break;
			}
			}
		}
	}

	private static async Task SetClipboardAsync(string value)
	{
		try
		{
			await ClipboardService.SetTextAsync(value);
		}
		catch (Exception error)
		{
			Logger.Instance.Warn("Failed to update the clipboard: " + error.Message);
		}
	}
}
