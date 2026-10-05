using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Platform;
using Voidstrap.Platform.Linux;

namespace Voidstrap.Utility;

internal static class SoberLauncherCommand
{
	private const string LogIdent = "SoberLauncherCommand";

	public static async Task<IReadOnlyList<string>> ResolveAsync(string? text, CancellationToken token)
	{
		List<string> words = Split(text);
		words.RemoveAll(static word => string.Equals(word, "%command%", StringComparison.Ordinal));
		if (words.Count == 0)
			return [];

		if (IsAssignment(words[0]))
			words.Insert(0, "env");

		string program = words[0];
		if (!await ExistsOnHostAsync(program, token).ConfigureAwait(false))
		{
			App.Logger.WriteLine(LogIdent, "The Sober launcher '" + program + "' was not found, starting Sober without it");
			return [];
		}

		App.Logger.WriteLine(LogIdent, "Starting Sober through: " + string.Join(' ', words));
		return words;
	}

	internal static List<string> Split(string? text)
	{
		List<string> words = [];
		if (string.IsNullOrWhiteSpace(text))
			return words;

		StringBuilder word = new();
		bool started = false;
		char quote = '\0';
		for (int index = 0; index < text.Length; index++)
		{
			char character = text[index];
			if (quote != '\0')
			{
				if (character == quote)
					quote = '\0';
				else if (character == '\\' && quote == '"' && index + 1 < text.Length && text[index + 1] is '"' or '\\')
					word.Append(text[++index]);
				else
					word.Append(character);
				continue;
			}

			if (character is '"' or '\'')
			{
				quote = character;
				started = true;
				continue;
			}

			if (character == '\\' && index + 1 < text.Length)
			{
				word.Append(text[++index]);
				started = true;
				continue;
			}

			if (char.IsWhiteSpace(character))
			{
				if (started)
				{
					words.Add(word.ToString());
					word.Clear();
					started = false;
				}
				continue;
			}

			word.Append(character);
			started = true;
		}

		if (started)
			words.Add(word.ToString());
		return words;
	}

	private static bool IsAssignment(string word)
	{
		int equals = word.IndexOf('=');
		return equals > 0 && word[..equals].All(static character => char.IsAsciiLetterOrDigit(character) || character == '_') && !char.IsAsciiDigit(word[0]);
	}

	private static async Task<bool> ExistsOnHostAsync(string program, CancellationToken token)
	{
		Voidstrap.Core.SystemProcessService processes = new();
		if (!LinuxFlatpakHost.TryCreateHostCommand(processes, "sh", ["-c", "command -v \"$1\" >/dev/null 2>&1", "sh", program], out ProcessCommand command))
			return false;
		OperationResult<ProcessExecution> result = await processes.ExecuteAsync(command, token).ConfigureAwait(false);
		token.ThrowIfCancellationRequested();
		return result.Succeeded && result.Value is { ExitCode: 0 };
	}
}
