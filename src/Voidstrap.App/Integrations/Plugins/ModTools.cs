using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace Voidstrap.Integrations.Plugins;

internal static class ModTypes
{
	private const string TsConfig = """
		{
		  "compilerOptions": {
		    "target": "ES2022",
		    "module": "ESNext",
		    "moduleResolution": "Bundler",
		    "jsx": "react",
		    "jsxFactory": "h",
		    "jsxFragmentFactory": "Fragment",
		    "strict": true,
		    "noEmit": true,
		    "allowImportingTsExtensions": true,
		    "lib": ["ES2022"],
		    "types": []
		  },
		  "files": ["./voidstrap/index.d.ts"]
		}
		""";

	public static void Write(string modDirectory)
	{
		try
		{
			string folder = Path.Combine(modDirectory, PluginManifest.ManifestFolder, "types");
			WriteIfChanged(Path.Combine(folder, "voidstrap", "index.d.ts"), Resource.GetString("ModTypes.d.ts"));
			WriteIfChanged(Path.Combine(folder, "tsconfig.json"), TsConfig);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			App.Logger.WriteLine("VoidstrapMods", "The type files could not be written: " + ex.Message);
		}
	}

	private static void WriteIfChanged(string path, string content)
	{
		if (File.Exists(path) && File.ReadAllText(path) == content)
			return;
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, content);
	}
}

internal static class ModTools
{
	public static int Validate(string folder, TextWriter output)
	{
		if (!PluginManifest.TryRead(Path.GetFullPath(folder), out PluginManifest? manifest, out string? error) || manifest == null)
		{
			output.WriteLine(error != null ? "Refused: " + error : "Refused: no " + PluginManifest.ManifestFolder + "/plugin.json in " + folder);
			return 1;
		}
		try
		{
			using PluginHost.LoadedPlugin plugin = PluginHost.LoadedPlugin.Load(manifest, sandbox: true);
			output.WriteLine(manifest.Name + " " + manifest.Version + " loads");
			IReadOnlyList<string> hooks = plugin.DescribeHooks();
			output.WriteLine(hooks.Count == 0 ? "It registers no hooks" : "Hooks:");
			foreach (string hook in hooks)
				output.WriteLine("  " + hook);
			foreach (string warning in plugin.Warnings)
				output.WriteLine("Warning: " + warning);
			if (manifest.NetworkHosts.Count > 0)
				output.WriteLine("Network: " + string.Join(", ", manifest.NetworkHosts));
			return 0;
		}
		catch (Exception ex)
		{
			output.WriteLine("Refused: the module did not load: " + PluginHost.Describe(ex));
			return 1;
		}
	}

	public static int Test(string folder, TextWriter output)
	{
		string root = Path.GetFullPath(folder);
		if (!PluginManifest.TryRead(root, out PluginManifest? manifest, out string? error) || manifest == null)
		{
			output.WriteLine(error != null ? "Refused: " + error : "Refused: no " + PluginManifest.ManifestFolder + "/plugin.json in " + folder);
			return 1;
		}
		List<string> files = Directory.EnumerateFiles(root, "*.test.*", SearchOption.AllDirectories)
			.Where(path => !path.Contains(Path.DirectorySeparatorChar + PluginManifest.ManifestFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
			.Where(path => Path.GetExtension(path).ToLowerInvariant() is ".js" or ".mjs" or ".ts" or ".tsx" or ".jsx")
			.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
			.ToList();
		if (files.Count == 0)
		{
			output.WriteLine("No test files found, name them like pane.test.ts");
			return 1;
		}
		int passed = 0;
		int failed = 0;
		foreach (string file in files)
		{
			string relative = Path.GetRelativePath(root, file);
			output.WriteLine(relative);
			try
			{
				using PluginHost.LoadedPlugin plugin = PluginHost.LoadedPlugin.Load(manifest, sandbox: true);
				foreach (JsonNode? result in plugin.RunTests(relative))
				{
					bool ok = result?["ok"]?.GetValue<bool>() == true;
					string name = result?["name"]?.ToString() ?? "";
					output.WriteLine((ok ? "  pass  " : "  FAIL  ") + name + (ok ? "" : ": " + result?["error"]));
					if (ok)
						passed++;
					else
						failed++;
				}
			}
			catch (Exception ex)
			{
				failed++;
				output.WriteLine("  FAIL  the test file did not run: " + PluginHost.Describe(ex));
			}
		}
		output.WriteLine(passed + " passed, " + failed + " failed");
		return failed == 0 ? 0 : 1;
	}
}
