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

	private readonly IProcessService _processes;

	public LinuxSoberProxyArming(IProcessService processes)
	{
		_processes = processes ?? throw new ArgumentNullException(nameof(processes));
	}

	public static string BypassList => string.Join(',', LoopbackBypass.Concat(PinnedHosts));

	public async Task<OperationResult> ArmAsync(Uri proxy, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(proxy);

		OperationResult validation = ValidateProxy(proxy);
		if (!validation.Succeeded)
		{
			return validation;
		}

		string address = proxy.GetLeftPart(UriPartial.Authority);
		List<string> arguments = ["override", "--user"];
		foreach (string variable in ProxyVariables)
		{
			arguments.Add("--env=" + variable + "=" + address);
		}

		foreach (string variable in BypassVariables)
		{
			arguments.Add("--env=" + variable + "=" + BypassList);
		}

		arguments.Add(SoberApplicationId);
		return await RunFlatpakAsync(arguments, "SoberProxyArmFailed", "Sober could not be pointed at the local asset proxy", cancellationToken).ConfigureAwait(false);
	}

	public async Task<OperationResult> DisarmAsync(CancellationToken cancellationToken = default)
	{
		List<string> arguments = ["override", "--user"];
		foreach (string variable in ProxyVariables.Concat(BypassVariables))
		{
			arguments.Add("--unset-env=" + variable);
		}

		arguments.Add(SoberApplicationId);
		return await RunFlatpakAsync(arguments, "SoberProxyDisarmFailed", "The Sober proxy settings could not be restored", cancellationToken).ConfigureAwait(false);
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

	private async Task<OperationResult> RunFlatpakAsync(
		IReadOnlyList<string> arguments,
		string failureCode,
		string failureMessage,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		string? flatpak = _processes.FindExecutable("flatpak");
		if (string.IsNullOrWhiteSpace(flatpak))
		{
			return OperationResult.Fail(
				"FlatpakMissing",
				"The flatpak command is unavailable",
				CapabilityState.RequiresExternalRuntime);
		}

		OperationResult<ProcessExecution> result = await _processes
			.ExecuteAsync(new ProcessCommand(flatpak, arguments), cancellationToken)
			.ConfigureAwait(false);
		if (!result.Succeeded || result.Value is null)
		{
			return OperationResult.Fail(failureCode, failureMessage);
		}

		if (result.Value.ExitCode != 0)
		{
			string detail = string.IsNullOrWhiteSpace(result.Value.StandardError)
				? result.Value.StandardOutput
				: result.Value.StandardError;
			return string.IsNullOrWhiteSpace(detail)
				? OperationResult.Fail(failureCode, failureMessage)
				: OperationResult.Fail(failureCode, failureMessage + ": " + detail.Trim());
		}

		return OperationResult.Success();
	}
}
