using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vortice.D3DCompiler;

namespace Voidstrap.Integrations.RiShade
{
    internal static class RiShadeShaderCache
    {
        private const string LOG_IDENT = "RiShade";
        private const ShaderFlags Flags = ShaderFlags.OptimizationLevel3;
        private const string FormatTag = "o3v1";

        public static readonly string[] PixelEntries =
        [
            "PSMain",
            "PSDownsamplePrefilter",
            "PSDownsample",
            "PSUpsampleTent",
            "PSBlurH",
            "PSBlurV",
            "PSBloomCombine",
            "PSDepthUp",
            "PSGi",
            "PSSsr",
            "PSComposite",
            "PSPassthrough",
        ];

        private static readonly ConcurrentDictionary<string, Lazy<byte[]>> Compiled = new(StringComparer.Ordinal);
        private static int _warming;

        private static string Folder => Path.Combine(Paths.Cache, "RiShadeShaders");

        public static byte[] Get(string source, string entry, string profile, string sourceName)
        {
            string key = KeyFor(source, entry, profile);
            Lazy<byte[]> lazy = Compiled.GetOrAdd(key, static (k, state) => new Lazy<byte[]>(() => LoadOrCompile(k, state.source, state.entry, state.profile, state.sourceName), LazyThreadSafetyMode.ExecutionAndPublication), (source, entry, profile, sourceName));
            try
            {
                return lazy.Value;
            }
            catch
            {
                Compiled.TryRemove(new(key, lazy));
                throw;
            }
        }

        public static void WarmInBackground()
        {
            if (Interlocked.Exchange(ref _warming, 1) != 0)
                return;
            Task.Run(Warm);
        }

        private static void Warm()
        {
            var clock = Stopwatch.StartNew();
            try
            {
                Get(RiShadeShaders.Source, "VSMain", "vs_5_0", "RiShade");
                Parallel.ForEach(PixelEntries, entry => Get(RiShadeShaders.Source, entry, "ps_5_0", "RiShade"));
                App.Logger.WriteLine(LOG_IDENT, $"Shaders ready in {clock.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "Shader warm up failed: " + ex.Message);
                Interlocked.Exchange(ref _warming, 0);
            }
        }

        private static string KeyFor(string source, string entry, string profile)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(FormatTag + "\n" + profile + "\n" + entry + "\n" + source));
            return Convert.ToHexString(hash, 0, 16);
        }

        private static byte[] LoadOrCompile(string key, string source, string entry, string profile, string sourceName)
        {
            string path = Path.Combine(Folder, key + ".cso");
            try
            {
                if (File.Exists(path))
                {
                    byte[] cached = File.ReadAllBytes(path);
                    if (cached.Length > 4 && cached[0] == (byte)'D' && cached[1] == (byte)'X' && cached[2] == (byte)'B' && cached[3] == (byte)'C')
                        return cached;
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Cached shader {entry} could not be read: {ex.Message}");
            }

            byte[] bytecode = Compile(source, entry, profile, sourceName);
            try
            {
                Directory.CreateDirectory(Folder);
                string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(temp, bytecode);
                File.Move(temp, path, true);
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Shader {entry} could not be cached: {ex.Message}");
            }
            return bytecode;
        }

        private static byte[] Compile(string source, string entry, string profile, string sourceName)
        {
            Compiler.Compile(source, null!, null!, entry, sourceName, profile, Flags, out var blob, out var errors);
            using (errors)
            {
                if (blob == null)
                {
                    string message = errors != null ? errors.AsString() : "unknown";
                    throw new InvalidOperationException($"{sourceName} shader {entry} failed to compile: {message}");
                }
            }
            using (blob)
            {
                return blob.AsBytes();
            }
        }
    }
}
