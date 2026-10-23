using System.Diagnostics;
using System.Globalization;
using System.Text;
using Voidstrap.Core;

namespace Voidstrap.Platform.Linux;

public sealed record LinuxEqualizerBands(float LowGainDb, float MidGainDb, float HighGainDb);

public static class LinuxAudioEqualizer
{
	public const string SinkName = "voidstrap_roblox_eq";

	private static readonly SystemProcessService Processes = new();
	private static readonly object Gate = new();
	private static Process? _process;
	private static string? _signature;

	private static string ConfigDirectory
	{
		get
		{
			string home = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			string state = Environment.GetEnvironmentVariable("XDG_STATE_HOME") ?? Path.Combine(home, ".local", "state");
			return Path.Combine(state, "voidstrap", "runtime", "audio");
		}
	}

	public static bool IsRunning
	{
		get
		{
			lock (Gate)
				return _process is { HasExited: false };
		}
	}

	public static bool Ensure(LinuxEqualizerBands bands, string? outputSink)
	{
		string signature = string.Create(CultureInfo.InvariantCulture, $"{bands.LowGainDb}|{bands.MidGainDb}|{bands.HighGainDb}|{outputSink}");
		lock (Gate)
		{
			if (_process is { HasExited: false } && _signature == signature)
				return true;
			StopLocked();

			string? fileName;
			List<string> arguments = [];
			if (LinuxFlatpakHost.IsSandboxed)
			{
				fileName = Processes.FindExecutable("flatpak-spawn");
				arguments.Add("--host");
				arguments.Add("--watch-bus");
				arguments.Add("--directory=/");
				arguments.Add("pipewire");
			}
			else
			{
				fileName = Processes.FindExecutable("pipewire");
			}
			if (string.IsNullOrWhiteSpace(fileName))
				return false;

			Directory.CreateDirectory(ConfigDirectory);
			string configFile = Path.Combine(ConfigDirectory, "equalizer.conf");
			File.WriteAllText(configFile, BuildConfig(bands, outputSink));
			arguments.Add("-c");
			arguments.Add(configFile);

			ProcessStartInfo startInfo = new(fileName)
			{
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true
			};
			foreach (string argument in arguments)
				startInfo.ArgumentList.Add(argument);

			Process? process = Process.Start(startInfo);
			if (process == null)
				return false;
			process.OutputDataReceived += DiscardOutput;
			process.ErrorDataReceived += DiscardOutput;
			process.BeginOutputReadLine();
			process.BeginErrorReadLine();
			_process = process;
			_signature = signature;
			return true;
		}
	}

	public static void Stop()
	{
		lock (Gate)
			StopLocked();
	}

	private static void StopLocked()
	{
		Process? process = _process;
		_process = null;
		_signature = null;
		if (process == null)
			return;
		try
		{
			if (!process.HasExited)
			{
				process.Kill(true);
				process.WaitForExit(2000);
			}
		}
		catch (InvalidOperationException)
		{
		}
		catch (System.ComponentModel.Win32Exception)
		{
		}
		process.OutputDataReceived -= DiscardOutput;
		process.ErrorDataReceived -= DiscardOutput;
		process.Dispose();
	}

	private static void DiscardOutput(object sender, DataReceivedEventArgs e)
	{
	}

	public static string BuildConfig(LinuxEqualizerBands bands, string? outputSink)
	{
		StringBuilder builder = new();
		builder.AppendLine("context.properties = { log.level = 0 }");
		builder.AppendLine("context.spa-libs = {");
		builder.AppendLine("    audio.convert.* = audioconvert/libspa-audioconvert");
		builder.AppendLine("    support.* = support/libspa-support");
		builder.AppendLine("}");
		builder.AppendLine("context.modules = [");
		builder.AppendLine("    { name = libpipewire-module-rt flags = [ ifexists nofail ] }");
		builder.AppendLine("    { name = libpipewire-module-protocol-native }");
		builder.AppendLine("    { name = libpipewire-module-client-node }");
		builder.AppendLine("    { name = libpipewire-module-adapter }");
		builder.AppendLine("    { name = libpipewire-module-filter-chain");
		builder.AppendLine("        args = {");
		builder.AppendLine("            node.description = \"Voidstrap Roblox Equalizer\"");
		builder.AppendLine("            media.name = \"Voidstrap Roblox Equalizer\"");
		builder.AppendLine("            filter.graph = {");
		builder.AppendLine("                nodes = [");
		builder.AppendLine(Band("low", "bq_lowshelf", 120f, 0.7f, bands.LowGainDb));
		builder.AppendLine(Band("mid", "bq_peaking", 2500f, 1.0f, bands.MidGainDb));
		builder.AppendLine(Band("high", "bq_highshelf", 8000f, 0.7f, bands.HighGainDb));
		builder.AppendLine("                ]");
		builder.AppendLine("                links = [");
		builder.AppendLine("                    { output = \"low:Out\" input = \"mid:In\" }");
		builder.AppendLine("                    { output = \"mid:Out\" input = \"high:In\" }");
		builder.AppendLine("                ]");
		builder.AppendLine("            }");
		builder.AppendLine("            audio.channels = 2");
		builder.AppendLine("            audio.position = [ FL FR ]");
		builder.AppendLine("            capture.props = {");
		builder.AppendLine("                node.name = \"" + SinkName + "\"");
		builder.AppendLine("                media.class = Audio/Sink");
		builder.AppendLine("            }");
		builder.AppendLine("            playback.props = {");
		builder.AppendLine("                node.name = \"" + SinkName + ".output\"");
		builder.AppendLine("                node.passive = true");
		if (!string.IsNullOrEmpty(outputSink))
			builder.AppendLine("                target.object = \"" + outputSink.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"");
		builder.AppendLine("            }");
		builder.AppendLine("        }");
		builder.AppendLine("    }");
		builder.AppendLine("]");
		return builder.ToString();
	}

	private static string Band(string name, string label, float frequency, float q, float gainDb)
	{
		return string.Create(CultureInfo.InvariantCulture,
			$"                    {{ type = builtin name = {name} label = {label} control = {{ \"Freq\" = {frequency:0.0} \"Q\" = {q:0.00} \"Gain\" = {gainDb:0.0} }} }}");
	}
}
