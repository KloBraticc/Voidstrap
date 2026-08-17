using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Platform;

namespace Voidstrap.Core;

public sealed class SystemProcessService : IProcessService
{
	private const int MaxCapturedCharacters = 4 * 1024 * 1024;
	private const int LinuxCurrentWorkingDirectory = -100;
	private const int LinuxDoNotFollowLinks = 0x100;
	private const uint LinuxFileTypeMaskRequest = 0x1;
	private const ushort LinuxFileTypeMask = 0xf000;
	private const ushort LinuxRegularFileType = 0x8000;

	[StructLayout(LayoutKind.Explicit, Size = 256)]
	private struct LinuxFileStatus
	{
		[FieldOffset(28)]
		public ushort Mode;
	}

	[DllImport("libc", EntryPoint = "statx", SetLastError = true)]
	private static extern int GetLinuxFileStatus(
		int directoryFileDescriptor,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string path,
		int flags,
		uint mask,
		out LinuxFileStatus status);

	public string? FindExecutable(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			return null;
		}

		if (Path.IsPathRooted(name))
		{
			return IsExecutableFile(name) ? name : null;
		}

		string? pathValue = Environment.GetEnvironmentVariable("PATH");
		if (string.IsNullOrWhiteSpace(pathValue))
		{
			return null;
		}

		IEnumerable<string> candidates = GetCandidateNames(name);
		foreach (string directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			foreach (string candidate in candidates)
			{
				try
				{
					string executable = Path.Combine(directory, candidate);
					if (IsExecutableFile(executable))
					{
						return executable;
					}
				}
				catch (Exception)
				{
				}
			}
		}

		return null;
	}

	private static bool IsExecutableFile(string path)
	{
		if (!File.Exists(path))
		{
			return false;
		}

		if (OperatingSystem.IsWindows())
		{
			return true;
		}

		try
		{
			if (OperatingSystem.IsLinux()
				&& (GetLinuxFileStatus(
					LinuxCurrentWorkingDirectory,
					Path.GetFullPath(path),
					LinuxDoNotFollowLinks,
					LinuxFileTypeMaskRequest,
					out LinuxFileStatus status) != 0
					|| (status.Mode & LinuxFileTypeMask) != LinuxRegularFileType))
			{
				return false;
			}
			UnixFileMode mode = File.GetUnixFileMode(path);
			const UnixFileMode executeBits = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
			return (mode & executeBits) != 0;
		}
		catch (Exception)
		{
			return false;
		}
	}

	public async Task<OperationResult<ProcessExecution>> ExecuteAsync(ProcessCommand command, CancellationToken cancellationToken = default)
	{
		Process? process = null;
		Task<string> standardOutputTask = Task.FromResult(string.Empty);
		Task<string> standardErrorTask = Task.FromResult(string.Empty);
		try
		{
			process = CreateProcess(command);
			if (!process.Start())
			{
				return OperationResult<ProcessExecution>.Fail("ProcessDidNotStart", "The requested process did not start");
			}

			standardOutputTask = command.CaptureOutput
				? ReadBoundedAsync(process.StandardOutput, cancellationToken)
				: Task.FromResult(string.Empty);
			standardErrorTask = command.CaptureOutput
				? ReadBoundedAsync(process.StandardError, cancellationToken)
				: Task.FromResult(string.Empty);

			if (command.StandardInput is not null)
			{
				await process.StandardInput.WriteAsync(command.StandardInput.AsMemory(), cancellationToken);
				await process.StandardInput.FlushAsync(cancellationToken);
				process.StandardInput.Close();
			}

			await process.WaitForExitAsync(cancellationToken);
			string standardOutput = await standardOutputTask;
			string standardError = await standardErrorTask;

			return OperationResult<ProcessExecution>.Success(new ProcessExecution(process.ExitCode, standardOutput, standardError));
		}
		catch (OperationCanceledException)
		{
			await TryTerminateAsync(process, standardOutputTask, standardErrorTask);
			return OperationResult<ProcessExecution>.Fail("OperationCanceled", "The requested process was canceled");
		}
		catch (Exception exception)
		{
			await TryTerminateAsync(process, standardOutputTask, standardErrorTask);
			return OperationResult<ProcessExecution>.Fail("ProcessExecutionFailed", exception.Message);
		}
		finally
		{
			process?.Dispose();
		}
	}

	private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
	{
		StringBuilder output = new StringBuilder();
		char[] buffer = new char[8192];
		bool exceeded = false;
		while (true)
		{
			int read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
			if (read == 0)
				break;
			int remaining = MaxCapturedCharacters - output.Length;
			if (remaining > 0)
				output.Append(buffer, 0, Math.Min(read, remaining));
			if (read > remaining)
				exceeded = true;
		}
		if (exceeded)
			throw new InvalidDataException("Process output exceeded the capture limit");
		return output.ToString();
	}

	public Task<OperationResult<ProcessStartResult>> StartAsync(ProcessCommand command, CancellationToken cancellationToken = default)
	{
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			using Process process = CreateProcess(command);
			if (!process.Start())
			{
				return Task.FromResult(OperationResult<ProcessStartResult>.Fail("ProcessDidNotStart", "The requested process did not start"));
			}

			return Task.FromResult(OperationResult<ProcessStartResult>.Success(new ProcessStartResult(process.Id)));
		}
		catch (OperationCanceledException)
		{
			return Task.FromResult(OperationResult<ProcessStartResult>.Fail("OperationCanceled", "The requested process was canceled"));
		}
		catch (Exception exception)
		{
			return Task.FromResult(OperationResult<ProcessStartResult>.Fail("ProcessStartFailed", exception.Message));
		}
	}

	private static Process CreateProcess(ProcessCommand command)
	{
		ProcessStartInfo startInfo = new ProcessStartInfo
		{
			FileName = command.FileName,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = command.StandardInput is not null,
			RedirectStandardOutput = command.CaptureOutput,
			RedirectStandardError = command.CaptureOutput
		};

		if (!string.IsNullOrWhiteSpace(command.WorkingDirectory))
		{
			startInfo.WorkingDirectory = command.WorkingDirectory;
		}

		foreach (string argument in command.Arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}

		return new Process
		{
			StartInfo = startInfo,
			EnableRaisingEvents = false
		};
	}

	private static IEnumerable<string> GetCandidateNames(string name)
	{
		yield return name;

		if (!OperatingSystem.IsWindows() || Path.HasExtension(name))
		{
			yield break;
		}

		string extensions = Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM";
		foreach (string extension in extensions.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			yield return name + extension;
		}
	}

	private static async Task TryTerminateAsync(Process? process, Task<string> standardOutputTask, Task<string> standardErrorTask)
	{
		if (process is null)
		{
			return;
		}

		try
		{
			if (!process.HasExited)
			{
				process.Kill(true);
			}
			await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
		}
		catch
		{
		}

		try
		{
			await Task.WhenAll(standardOutputTask, standardErrorTask).WaitAsync(TimeSpan.FromSeconds(5));
		}
		catch
		{
		}
	}
}
