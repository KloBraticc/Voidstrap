namespace Voidstrap.Platform.Linux;

public sealed record LinuxSoberLauncher(IReadOnlyList<string> HostPrefix, IReadOnlyList<string> FlatpakArguments, bool MangoHud)
{
	public static LinuxSoberLauncher Empty { get; } = new([], [], false);

	public const string MangoHudCommand = "/usr/lib/extensions/vulkan/MangoHud/bin/mangohud";

	public static LinuxSoberLauncher Create(IReadOnlyList<string> words)
	{
		List<string> prefix = [];
		List<string> arguments = [];
		bool mangoHud = false;
		int index = 0;
		while (index < words.Count)
		{
			string word = words[index];
			string program = Path.GetFileName(word);
			if (IsAssignment(word))
			{
				arguments.Add("--env=" + word);
				if (word.StartsWith("MANGOHUD=", StringComparison.Ordinal))
					mangoHud = word == "MANGOHUD=1";
				prefix.Add(word);
				index++;
				continue;
			}
			if (program == "env")
			{
				prefix.Add(word);
				index++;
				while (index < words.Count)
				{
					if (words[index].StartsWith("--unset=", StringComparison.Ordinal))
					{
						arguments.Add("--unset-env=" + words[index]["--unset=".Length..]);
						prefix.Add(words[index++]);
					}
					else if (words[index] is "-u" or "--unset")
					{
						prefix.Add(words[index++]);
						if (index == words.Count)
							throw new FormatException("The Sober launcher is missing an environment variable name after --unset.");
						arguments.Add("--unset-env=" + words[index]);
						prefix.Add(words[index++]);
					}
					else
						break;
				}
				if (index < words.Count && words[index] == "--")
					prefix.Add(words[index++]);
				continue;
			}
			if (program == "mangohud")
			{
				mangoHud = true;
				arguments.Add("--env=MANGOHUD=1");
				arguments.Add("--env=DISABLE_MANGOHUD=0");
				index++;
				if (index < words.Count && words[index] == "--dlsym")
					index++;
				if (index < words.Count && words[index] == "--")
					index++;
				else if (index < words.Count && words[index].StartsWith('-'))
					throw new FormatException("Unsupported MangoHud launcher option: " + words[index]);
				continue;
			}
			if (program is "gamemoderun" or "prime-run")
			{
				prefix.Add(word);
				index++;
				continue;
			}
			prefix.AddRange(words.Skip(index));
			break;
		}
		if (prefix.Count > 0 && IsAssignment(prefix[0]))
			prefix.Insert(0, "env");
		return new(prefix, arguments, mangoHud);
	}

	public void AppendRunArguments(List<string> arguments, string reference, Uri deeplink, bool disableEffects = false)
	{
		arguments.AddRange(FlatpakArguments);
		if (disableEffects)
			arguments.AddRange(["--env=ENABLE_VKBASALT=0", "--env=DISABLE_VKBASALT=1", "--env=ENABLE_LSFG=0", "--env=MANGOHUD=0", "--env=DISABLE_MANGOHUD=1"]);
		else if (MangoHud)
			arguments.Add("--command=" + MangoHudCommand);
		arguments.Add(reference);
		if (MangoHud && !disableEffects)
			arguments.Add("sober");
		if (!SoberLaunchLink.IsHomeLaunch(deeplink))
			arguments.Add(deeplink.AbsoluteUri);
	}

	private static bool IsAssignment(string word)
	{
		int equals = word.IndexOf('=');
		return equals > 0 && word[..equals].All(static character => char.IsAsciiLetterOrDigit(character) || character == '_') && !char.IsAsciiDigit(word[0]);
	}
}
