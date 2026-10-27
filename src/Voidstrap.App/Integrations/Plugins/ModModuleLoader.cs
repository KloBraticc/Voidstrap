using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Jint;
using Jint.Native;
using Jint.Runtime.Modules;

namespace Voidstrap.Integrations.Plugins;

internal sealed class ModModuleLoader : DefaultModuleLoader
{
	private const string TestingSpecifier = "voidstrap/testing";

	private const string TypesSpecifier = "voidstrap";

	private static readonly string[] Extensions = [".ts", ".tsx", ".js", ".mjs", ".jsx"];

	public ModModuleLoader(string basePath) : base(basePath, restrictToBasePath: true)
	{
	}

	public override ResolvedSpecifier Resolve(string? referencingModuleLocation, ModuleRequest moduleRequest)
	{
		if (moduleRequest.Specifier is TestingSpecifier or TypesSpecifier)
			return new ResolvedSpecifier(moduleRequest, "voidstrap-builtin:" + moduleRequest.Specifier, null, SpecifierType.Bare);
		string specifier = moduleRequest.Specifier;
		bool relative = specifier.StartsWith("./", StringComparison.Ordinal) || specifier.StartsWith("../", StringComparison.Ordinal);
		if (relative && !Extensions.Any(extension => specifier.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
		{
			foreach (string extension in Extensions)
			{
				foreach (string candidate in new[] { specifier + extension, specifier.TrimEnd('/') + "/index" + extension })
				{
					try
					{
						ResolvedSpecifier attempt = base.Resolve(referencingModuleLocation, moduleRequest with { Specifier = candidate });
						if (attempt.Uri is { IsFile: true } found && File.Exists(found.LocalPath))
							return attempt;
					}
					catch (ModuleResolutionException)
					{
					}
				}
			}
		}
		return base.Resolve(referencingModuleLocation, moduleRequest);
	}

	protected override string LoadModuleContents(Engine engine, ResolvedSpecifier resolved)
	{
		if (resolved.Key == "voidstrap-builtin:" + TestingSpecifier)
			return "export const test = globalThis.test; export const expect = globalThis.expect; export const mod = globalThis.mod;";
		if (resolved.Key == "voidstrap-builtin:" + TypesSpecifier)
			return "export {};";
		string source = base.LoadModuleContents(engine, resolved);
		string path = resolved.Uri?.LocalPath ?? resolved.Key;
		return ModTranspiler.NeedsTranspile(path) ? ModTranspiler.Transpile(source, path) : source;
	}
}

internal static class ModTranspiler
{
	private const int MaxCacheEntries = 256;

	private static readonly object Gate = new();

	private static readonly Dictionary<string, string> Cache = new(StringComparer.Ordinal);

	private static Engine? _engine;

	public static bool NeedsTranspile(string path)
	{
		string extension = Path.GetExtension(path);
		return extension.Equals(".ts", StringComparison.OrdinalIgnoreCase)
			|| extension.Equals(".tsx", StringComparison.OrdinalIgnoreCase)
			|| extension.Equals(".jsx", StringComparison.OrdinalIgnoreCase);
	}

	public static string Transpile(string source, string path)
	{
		string kind = Path.GetExtension(path).ToLowerInvariant();
		string key = kind + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
		lock (Gate)
		{
			if (Cache.TryGetValue(key, out string? cached))
				return cached;
			_engine ??= CreateEngine();
			_engine.Constraints.Reset();
			string output = _engine.Invoke("__voidstrapTranspile", source, "module" + kind).AsString();
			if (Cache.Count >= MaxCacheEntries)
				Cache.Clear();
			Cache[key] = output;
			return output;
		}
	}

	private static Engine CreateEngine()
	{
		Engine engine = new(options =>
		{
			options.Strict();
			options.TimeoutInterval(TimeSpan.FromSeconds(15));
			options.LimitMemory(256L * 1024 * 1024);
		});
		engine.Execute(Resource.GetString("ModTranspiler.js"));
		return engine;
	}
}
