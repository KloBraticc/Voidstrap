using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace Voidstrap.Integrations;

public static class LaunchInterceptor
{
	private const string LOG_IDENT = "LaunchInterceptor";

	public static async Task<string?> MaybeRewriteForClosestAsync(string? robloxLaunchArgs, CancellationToken token = default(CancellationToken))
	{
		App.Logger.WriteLine("LaunchInterceptor", "MaybeRewriteForClosestAsync entered.");
		if (string.IsNullOrWhiteSpace(robloxLaunchArgs))
		{
			App.Logger.WriteLine("LaunchInterceptor", "Launch arguments are empty, aborting");
			return null;
		}
		App.Logger.WriteLine("LaunchInterceptor", "Launch argument length: " + robloxLaunchArgs.Length);
		try
		{
			long placeId = ExtractPlaceId(robloxLaunchArgs);
			App.Logger.WriteLine("LaunchInterceptor", $"Extracted placeId={placeId}");
			if (placeId == 0L)
			{
				App.Logger.WriteLine("LaunchInterceptor", "No placeId in launch args; skipping rewrite.");
				return null;
			}
			if (!App.Settings.Prop.VoidstrapMatchmakerEnabled && !ServerMatchmaker.HasPerGamePreference(placeId))
			{
				App.Logger.WriteLine("LaunchInterceptor", "Matchmaker is disabled and this game has no datacenter preference, skipping rewrite");
				return null;
			}
			if (ContainsSpecificGameInstance(robloxLaunchArgs))
			{
				App.Logger.WriteLine("LaunchInterceptor", "Launch already targets a specific server (gameInstanceId / gameId present); not rewriting.");
				return null;
			}
			if (string.IsNullOrEmpty(RobloxAuthLauncher.TryGetRobloxSecurityCookie()))
			{
				App.Logger.WriteLine("LaunchInterceptor", "No Roblox cookie is available, sign in through Settings > Server Matchmaker");
				return null;
			}
			App.Logger.WriteLine("LaunchInterceptor", "Cookie present, geo + matchmaker proceeding.");
			string preferredKey = ServerMatchmaker.ResolvePreferredDatacenterKey(placeId);
			MatchmakerCandidate matchmakerCandidate = await VoidstrapMatchmaker.PickBestJobIdAsync(placeId, null, VoidstrapMatchmaker.ResolveEffectiveCandidateCount(), token, preferredKey).ConfigureAwait(continueOnCapturedContext: false);
			if (matchmakerCandidate == null || string.IsNullOrEmpty(matchmakerCandidate.JobId))
			{
				App.Logger.WriteLine("LaunchInterceptor", "Matchmaker returned no candidate; using original launch args.");
				return null;
			}
			string text = RewriteLauncherUrl(robloxLaunchArgs, placeId, matchmakerCandidate.JobId);
			if (text == null)
			{
				App.Logger.WriteLine("LaunchInterceptor", "RewriteLauncherUrl returned null; using original.");
				return null;
			}
			App.Logger.WriteLine("LaunchInterceptor", $"Rewrote launch for {matchmakerCandidate.Datacenter?.City ?? "?"}, {matchmakerCandidate.DistanceKm:F0}km away");
			return text;
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("LaunchInterceptor", "MaybeRewriteForClosestAsync failed: " + ex.GetType().Name + ", " + ex.Message);
			return null;
		}
	}

	public static long ExtractPlaceId(string args)
	{
		Match match = Regex.Match(args, "placeId%3D(\\d+)|placeId=(\\d+)|placeid%3d(\\d+)", RegexOptions.IgnoreCase);
		if (match.Success)
		{
			for (int i = 1; i <= 3; i++)
			{
				if (!string.IsNullOrEmpty(match.Groups[i].Value) && long.TryParse(match.Groups[i].Value, out var result))
				{
					return result;
				}
			}
		}
		return 0L;
	}

	public static bool ContainsSpecificGameInstance(string args)
	{
		if (Regex.IsMatch(args, "(?:accessCode|privateServerLinkCode)(?:=|%3D)[^&+%\\s]+", RegexOptions.IgnoreCase))
		{
			return true;
		}
		if (Regex.IsMatch(args, "gameInstanceId%3D[0-9a-fA-F-]+", RegexOptions.IgnoreCase))
		{
			return true;
		}
		if (Regex.IsMatch(args, "gameInstanceId=[0-9a-fA-F-]+", RegexOptions.IgnoreCase))
		{
			return true;
		}
		if (Regex.IsMatch(args, "gameId%3D[0-9a-fA-F-]+", RegexOptions.IgnoreCase))
		{
			return true;
		}
		if (Regex.IsMatch(args, "[?&]gameId=[0-9a-fA-F-]+", RegexOptions.IgnoreCase))
		{
			return true;
		}
		return false;
	}

	public static string? RewriteLauncherUrl(string args, long placeId, string jobId)
	{
		try
		{
			Match match = Regex.Match(args, "placelauncherurl:([^\\+]+)", RegexOptions.IgnoreCase);
			if (match.Success)
			{
				string text = HttpUtility.UrlEncode(RewriteSinglePlaceLauncherUrl(HttpUtility.UrlDecode(match.Groups[1].Value), placeId, jobId));
				return args.Substring(0, match.Index) + "placelauncherurl:" + text + args.Substring(match.Index + match.Length);
			}
			if (args.StartsWith("roblox://", StringComparison.OrdinalIgnoreCase) || (!args.StartsWith("roblox-player:1+launchmode:play", StringComparison.OrdinalIgnoreCase) && args.Contains($"placeId={placeId}", StringComparison.OrdinalIgnoreCase)))
			{
				if (args.Contains("gameInstanceId=", StringComparison.OrdinalIgnoreCase))
				{
					return Regex.Replace(args, "gameInstanceId=[^&]*", "gameInstanceId=" + jobId, RegexOptions.IgnoreCase);
				}
				int fragmentIndex = args.IndexOf('#');
				string fragment = fragmentIndex >= 0 ? args[fragmentIndex..] : "";
				string baseValue = fragmentIndex >= 0 ? args[..fragmentIndex] : args;
				string text2 = baseValue.Contains('?') ? "&" : "?";
				return baseValue + text2 + "gameInstanceId=" + Uri.EscapeDataString(jobId) + fragment;
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("LaunchInterceptor", "RewriteLauncherUrl threw: " + ex.Message);
		}
		return null;
	}

	private static string RewriteSinglePlaceLauncherUrl(string url, long placeId, string jobId)
	{
		int num = url.IndexOf('?');
		if (num < 0)
		{
			return url;
		}
		string text = url.Substring(0, num);
		string text2 = url.Substring(num + 1);
		List<(string Key, string Value)> pairs = text2.Split('&').Select(delegate(string p)
		{
			int num2 = p.IndexOf('=');
			return (num2 >= 0) ? (Key: p.Substring(0, num2), Value: p.Substring(num2 + 1)) : (Key: p, Value: "");
		}).ToList();
		SetPair("request", "RequestGameJob");
		SetPair("placeId", placeId.ToString());
		SetPair("gameId", jobId);
		string text3 = string.Join("&", pairs.Select(((string Key, string Value) p) => p.Key + "=" + p.Value));
		return text + "?" + text3;
		void SetPair(string key, string value)
		{
			int num2 = pairs.FindIndex(((string Key, string Value) p) => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
			if (num2 >= 0)
			{
				pairs[num2] = (key, value);
			}
			else
			{
				pairs.Add((key, value));
			}
		}
	}
}
