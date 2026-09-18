using System.Net;

namespace Voidstrap.Platform.Linux;

public sealed class LinuxSoberProxyArming
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

	private static readonly string[] LoopbackBypass =
	[
		"localhost",
		"127.0.0.1",
		"::1"
	];

	private static readonly string[] PinnedHosts =
	[
		"sober.vinegarhq.org",
		"raw.githubusercontent.com"
	];

	public LinuxSoberProxyArming(IProcessService processes)
	{
		ArgumentNullException.ThrowIfNull(processes);
	}

	public static string BypassList => string.Join(',', LoopbackBypass.Concat(PinnedHosts));

	public Task<OperationResult> ArmAsync(Uri proxy, CancellationToken cancellationToken = default)
	{
		return ArmAsync(proxy, null, cancellationToken);
	}

	public Task<OperationResult> ArmAsync(Uri proxy, string? certificateBundlePath, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(proxy);
		cancellationToken.ThrowIfCancellationRequested();

		OperationResult validation = ValidateProxy(proxy);
		if (!validation.Succeeded)
		{
			return Task.FromResult(validation);
		}

		string address = proxy.GetLeftPart(UriPartial.Authority);
		List<string> arguments = [];
		foreach (string variable in ProxyVariables)
		{
			arguments.Add("--env=" + variable + "=" + address);
		}

		foreach (string variable in BypassVariables)
		{
			arguments.Add("--env=" + variable + "=" + BypassList);
		}

		if (!string.IsNullOrWhiteSpace(certificateBundlePath))
		{
			foreach (string variable in TrustVariables)
			{
				arguments.Add("--env=" + variable + "=" + certificateBundlePath);
			}
		}

		CleanupLegacyOverride();
		LinuxSoberRuntimeProvider.ProxyArguments = arguments;
		return Task.FromResult(OperationResult.Success());
	}

	public Task<OperationResult> DisarmAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		LinuxSoberRuntimeProvider.ProxyArguments = [];
		CleanupLegacyOverride();
		return Task.FromResult(OperationResult.Success());
	}

	private static void CleanupLegacyOverride()
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

	private static OperationResult ValidateProxy(Uri proxy)
	{
		if (!proxy.IsAbsoluteUri || !string.Equals(proxy.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal))
		{
			return OperationResult.Fail("SoberProxyAddressInvalid", "The asset proxy address must be an http address");
		}

		if (!IPAddress.TryParse(proxy.Host, out IPAddress? address) || !IPAddress.IsLoopback(address))
		{
			return OperationResult.Fail("SoberProxyAddressInvalid", "The asset proxy address must be a loopback address");
		}

		return OperationResult.Success();
	}

}
