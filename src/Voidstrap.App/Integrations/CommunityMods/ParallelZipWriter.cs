using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Integrations.CommunityMods;

internal static class ParallelZipWriter
{
	private static readonly int WorkerLimit = Math.Clamp(Environment.ProcessorCount, 2, 8);

	public static int Extract(string archivePath, IReadOnlyList<(int Index, string Target)> plan, CancellationToken token)
	{
		if (plan.Count == 0)
		{
			return 0;
		}
		HashSet<string> folders = new(StringComparer.OrdinalIgnoreCase);
		foreach ((int _, string target) in plan)
		{
			token.ThrowIfCancellationRequested();
			string? folder = Path.GetDirectoryName(target);
			if (folder != null && folders.Add(folder))
			{
				Directory.CreateDirectory(folder);
			}
		}
		int workers = Math.Clamp(plan.Count / 24, 1, WorkerLimit);
		try
		{
			Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = token }, (worker, state) =>
			{
				using ZipArchive archive = ZipFile.OpenRead(archivePath);
				var entries = archive.Entries;
				for (int i = worker; i < plan.Count && !state.ShouldExitCurrentIteration; i += workers)
				{
					token.ThrowIfCancellationRequested();
					(int index, string target) = plan[i];
					entries[index].ExtractToFile(target, overwrite: true);
				}
			});
		}
		catch (AggregateException ex) when (ex.InnerExceptions.Count > 0)
		{
			ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
		}
		return plan.Count;
	}

	public static List<(int Index, string Target)> LastWins(List<(int Index, string Target)> plan)
	{
		Dictionary<string, int> latest = new(StringComparer.OrdinalIgnoreCase);
		for (int i = 0; i < plan.Count; i++)
		{
			latest[plan[i].Target] = i;
		}
		if (latest.Count == plan.Count)
		{
			return plan;
		}
		List<(int Index, string Target)> unique = new(latest.Count);
		for (int i = 0; i < plan.Count; i++)
		{
			if (latest[plan[i].Target] == i)
			{
				unique.Add(plan[i]);
			}
		}
		return unique;
	}
}
