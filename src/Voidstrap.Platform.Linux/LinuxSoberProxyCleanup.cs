namespace Voidstrap.Platform.Linux;

public static class LinuxSoberProxyCleanup
{
	private const string SoberApplicationId = "org.vinegarhq.Sober";

	private static readonly string[] ProxyVariables =
	[
		"ALL_PROXY",
		"HTTPS_PROXY",
		"HTTP_PROXY",
		"all_proxy",
		"https_proxy",
		"http_proxy"
	];

	private static readonly string[] TrustVariables =
	[
		"SSL_CERT_FILE",
		"CURL_CA_BUNDLE",
		"REQUESTS_CA_BUNDLE",
		"NODE_EXTRA_CA_CERTS"
	];

	private static readonly string[] BypassVariables =
	[
		"NO_PROXY",
		"no_proxy"
	];

	public static void RemoveLegacyOverride()
	{
		try
		{
			string dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? "";
			if (string.IsNullOrWhiteSpace(dataHome) || !Path.IsPathRooted(dataHome))
			{
				string home = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
				dataHome = Path.Combine(home, ".local", "share");
			}

			string path = Path.Combine(dataHome, "flatpak", "overrides", SoberApplicationId);
			if (!File.Exists(path))
				return;

			HashSet<string> variables = new(
				ProxyVariables.Concat(BypassVariables).Concat(TrustVariables),
				StringComparer.Ordinal);
			string section = "";
			List<string> output = [];
			foreach (string line in File.ReadAllLines(path))
			{
				string trimmed = line.Trim();
				if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
				{
					section = trimmed;
					output.Add(line);
					continue;
				}

				int separator = line.IndexOf('=');
				if (section.Equals("[Environment]", StringComparison.Ordinal) && separator > 0 && variables.Contains(line[..separator].Trim()))
					continue;

				if (section.Equals("[Context]", StringComparison.Ordinal)
					&& separator > 0
					&& line[..separator].Trim().Equals("unset-environment", StringComparison.Ordinal))
				{
					string retained = string.Join(';', line[(separator + 1)..]
						.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
						.Where(value => !variables.Contains(value)));
					if (retained.Length > 0)
						output.Add(line[..(separator + 1)] + retained + ";");
					continue;
				}

				output.Add(line);
			}

			string temporary = path + ".voidstrap.tmp";
			File.WriteAllLines(temporary, output);
			File.Move(temporary, path, true);
		}
		catch
		{
		}
	}
}
