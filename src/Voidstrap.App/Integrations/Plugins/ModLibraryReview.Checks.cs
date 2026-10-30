using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Integrations.Plugins;

internal static partial class ModLibraryReview
{
    public static async Task<PreflightResult> RunAsync(string mod, CancellationToken token)
    {
        List<string> errors = [];
        List<string> reviews = [];
        string folder = Path.Combine(Paths.Plugins, mod);
        if (!Directory.Exists(folder))
            return new PreflightResult(1, 0, "The mod folder does not exist: " + mod);
        if (!Regex.IsMatch(mod, "^[a-z0-9][a-z0-9-]{0,63}$"))
            errors.Add("the folder name must be lowercase letters, digits and hyphens");
        CheckNameRules(mod, errors);
        CheckFiles(folder, errors, reviews);
        CheckManifest(mod, folder, errors, reviews);
        await CheckVersionAsync(mod, errors, token).ConfigureAwait(false);
        return new PreflightResult(errors.Count, reviews.Count, BuildReport(mod, errors, reviews));
    }

    private static string BuildReport(string mod, List<string> errors, List<string> reviews)
    {
        StringBuilder report = new();
        report.AppendLine("Library preflight for " + mod + ", the same checks the review bot runs:");
        if (errors.Count > 0)
        {
            report.AppendLine();
            report.AppendLine("RED X items, fix every one before publishing:");
            foreach (string error in errors)
                report.AppendLine("  X " + error);
        }
        if (reviews.Count > 0)
        {
            report.AppendLine();
            report.AppendLine("These pass the automated gate but the reviewer will look at them, be ready to explain or remove them:");
            foreach (string review in reviews)
                report.AppendLine("  ? " + review);
        }
        if (errors.Count == 0 && reviews.Count == 0)
            report.AppendLine("  everything passed, publish is allowed");
        report.AppendLine();
        report.AppendLine(
            errors.Count > 0
                ? "Publish stays closed until the X items are fixed. Fix the mod files, then run preflight again."
                : "Publish may run. The AI reviewer still reads every file, so keep the description honest and remove anything the question marks above mention if it is not essential."
        );
        return report.ToString().TrimEnd();
    }

    private static void CheckNameRules(string mod, List<string> errors)
    {
        string spaced = mod.Replace('-', ' ');
        foreach (ReviewRule rule in BlockedRules)
        {
            if (rule.Level != "review" && rule.Binary && rule.Regex.IsMatch(spaced))
                errors.Add("the mod name violates a library rule: " + rule.Reason);
        }
    }

    private static void CheckFiles(string folder, List<string> errors, List<string> reviews)
    {
        List<(string Rel, string Full, long Size, bool Symlink)> files = [];
        Collect(folder, folder, files);
        if (files.Count > MaxFiles)
            errors.Add("has " + files.Count + " files, the limit is " + MaxFiles);
        if (files.Sum(file => file.Size) > MaxModBytes)
            errors.Add("is larger than " + MaxModBytes + " bytes in total");
        foreach ((string rel, string full, long size, bool symlink) in files)
        {
            string[] parts = rel.Split('/');
            if (symlink)
            {
                errors.Add(rel + " is a symbolic link");
                continue;
            }
            if (parts.Length > MaxPathDepth)
                errors.Add(rel + " is nested too deeply, the limit is " + MaxPathDepth + " levels");
            if (parts.Any(part => part == ".." || !Regex.IsMatch(part, "^[A-Za-z0-9._-]+$")))
                errors.Add(rel + " has an unsafe file name");
            if (parts.Where((part, index) => part.StartsWith('.') && !(index == 0 && part == PluginManifest.ManifestFolder)).Any())
                errors.Add(rel + " is a hidden file");
            CheckNameAgainstRules(rel, errors);
            if (
                rel.StartsWith(PluginManifest.ManifestFolder + "/", StringComparison.Ordinal)
                && rel != PluginManifest.ManifestFolder + "/plugin.json"
            )
                errors.Add(rel + " must not be published, only plugin.json belongs in " + PluginManifest.ManifestFolder);
            if (rel.StartsWith("node_modules/", StringComparison.Ordinal))
                errors.Add("node_modules must not be published");
            string extension = Path.GetExtension(rel).ToLowerInvariant();
            if (!AllowedExtensions.Contains(extension))
            {
                errors.Add(rel + " has a file type that is not allowed, allowed: " + string.Join(", ", AllowedExtensions));
                continue;
            }
            if (extension == ".ico")
            {
                CheckIcon(rel, full, size, errors);
                continue;
            }
            if (size > MaxFileBytes)
                errors.Add(rel + " is larger than " + MaxFileBytes + " bytes");
            CheckText(rel, full, CodeExtensions.Contains(extension), errors, reviews);
        }
    }

    private static void Collect(string directory, string baseFolder, List<(string Rel, string Full, long Size, bool Symlink)> files)
    {
        try
        {
            foreach (string path in Directory.EnumerateFiles(directory))
            {
                bool symlink = (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
                files.Add((Path.GetRelativePath(baseFolder, path).Replace('\\', '/'), path, new FileInfo(path).Length, symlink));
            }
            foreach (string child in Directory.EnumerateDirectories(directory))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                {
                    files.Add((Path.GetRelativePath(baseFolder, child).Replace('\\', '/'), child, 0, true));
                    continue;
                }
                Collect(child, baseFolder, files);
            }
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "The mod folder could not be walked: " + ex.Message);
        }
    }

    private static void CheckNameAgainstRules(string rel, List<string> errors)
    {
        string spaced = rel.Replace('/', ' ').Replace('_', ' ').Replace('.', ' ').Replace('-', ' ');
        foreach (ReviewRule rule in BlockedRules)
        {
            if (rule.Level != "review" && rule.Binary && rule.Regex.IsMatch(spaced))
                errors.Add(rel + " file name violates a library rule: " + rule.Reason);
        }
    }

    private static void CheckIcon(string rel, string full, long size, List<string> errors)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(full);
        }
        catch (Exception ex)
        {
            errors.Add(rel + " could not be read: " + ex.Message);
            return;
        }
        if (size > MaxIconBytes)
            errors.Add(rel + " is larger than " + MaxIconBytes + " bytes");
        if (bytes.Length < 22 || bytes[0] != 0 || bytes[1] != 0 || bytes[2] != 1 || bytes[3] != 0)
        {
            errors.Add(rel + " is not a valid .ico icon");
            return;
        }
        int count = BitConverter.ToUInt16(bytes, 4);
        if (count < 1 || count > 16 || bytes.Length < 6 + count * 16)
        {
            errors.Add(rel + " has an invalid icon directory");
            return;
        }
        for (int index = 0; index < count; index++)
        {
            int entry = 6 + index * 16;
            int frameSize = (int)BitConverter.ToUInt32(bytes, entry + 8);
            int offset = (int)BitConverter.ToUInt32(bytes, entry + 12);
            if (frameSize < 40 || offset < 6 + count * 16 || offset + frameSize > bytes.Length)
            {
                errors.Add(rel + " has an out of bounds icon frame");
                return;
            }
            bool isPng =
                bytes[offset] == 0x89
                && bytes[offset + 1] == 0x50
                && bytes[offset + 2] == 0x4E
                && bytes[offset + 3] == 0x47
                && bytes[offset + 4] == 0x0D
                && bytes[offset + 5] == 0x0A
                && bytes[offset + 6] == 0x1A
                && bytes[offset + 7] == 0x0A;
            bool isBmp = BitConverter.ToUInt32(bytes, offset) == 40;
            if (!isPng && !isBmp)
            {
                errors.Add(rel + " frame " + (index + 1) + " is neither a PNG nor a BMP icon frame");
                return;
            }
        }
        string ascii = Encoding.Latin1.GetString(bytes);
        foreach (ReviewRule rule in BlockedRules)
        {
            if (rule.Binary && rule.Level != "review" && rule.Regex.IsMatch(ascii))
            {
                errors.Add(rel + " metadata violates a library rule: " + rule.Reason);
                return;
            }
        }
    }

    private static void CheckText(string rel, string full, bool isCode, List<string> errors, List<string> reviews)
    {
        string text;
        try
        {
            text = File.ReadAllText(full);
        }
        catch (Exception ex)
        {
            errors.Add(rel + " could not be read: " + ex.Message);
            return;
        }
        if (text.Contains('\0'))
            errors.Add(rel + " contains binary data, text files must hold plain readable text only, re-save the file as text or remove it");
        if (InvisibleCharacterPattern.IsMatch(text))
            errors.Add(rel + " contains invisible or direction changing characters, remove them, they look like hidden instructions");
        string[] lines = text.Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index].TrimEnd('\r');
            string where = rel + ":" + (index + 1);
            if (isCode && line.Length > MaxLineLength)
                errors.Add(
                    where
                        + " is longer than "
                        + MaxLineLength
                        + " characters, lines this long usually mean minified or packed code, write normal readable code with short lines"
                );
            if (new Regex("[A-Za-z0-9+/=]{" + MaxEncodedRun + ",}", RegexOptions.None, TimeSpan.FromSeconds(1)).IsMatch(line))
                errors.Add(
                    where
                        + " holds a long encoded blob, a very long run of letters and digits looks like hidden data, keep data short and readable"
                );
            if (new Regex("(0x)?[0-9a-fA-F]{" + MaxEncodedRun + ",}", RegexOptions.None, TimeSpan.FromSeconds(1)).IsMatch(line))
                errors.Add(
                    where + " holds a long hex blob, a very long run of hex digits looks like hidden data, keep data short and readable"
                );
            foreach (ReviewRule rule in BlockedRules)
            {
                if ((rule.CodeOnly && !isCode) || !rule.Regex.IsMatch(line))
                    continue;
                if (rule.Level == "review")
                    reviews.Add(where + " " + rule.Reason);
                else
                    errors.Add(where + " " + rule.Reason);
            }
            if (ReviewerInjectionPattern.IsMatch(line))
                errors.Add(
                    where
                        + " text that tries to instruct the reviewer, the AI review flags this and blocks the merge, remove every sentence that talks to a reviewer"
                );
        }
    }

    private static void CheckManifest(string mod, string folder, List<string> errors, List<string> reviews)
    {
        string manifestPath = Path.Combine(folder, PluginManifest.ManifestFolder, "plugin.json");
        if (!File.Exists(manifestPath))
        {
            errors.Add(PluginManifest.ManifestFolder + "/plugin.json is missing");
            return;
        }
        System.Text.Json.Nodes.JsonObject? manifest;
        try
        {
            manifest =
                System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifestPath)) as System.Text.Json.Nodes.JsonObject
                ?? throw new InvalidDataException("not an object");
        }
        catch (Exception ex)
        {
            errors.Add(PluginManifest.ManifestFolder + "/plugin.json is not valid JSON: " + ex.Message);
            return;
        }
        if (!string.Equals(manifest["name"]?.ToString(), mod, StringComparison.Ordinal))
            errors.Add("plugin.json name must match the folder name");
        string version = manifest["version"]?.ToString() ?? "";
        if (!SemverRegex.IsMatch(version))
            errors.Add("plugin.json version must look like 1.2.3");
        string description = manifest["description"]?.ToString() ?? "";
        if (description.Trim().Length == 0 || description.Length > MaxDescription)
            errors.Add("plugin.json description must be 1 to " + MaxDescription + " characters");
        if (manifest["network"] is not System.Text.Json.Nodes.JsonArray hosts)
        {
            errors.Add("plugin.json network must be a list of host names");
        }
        else
        {
            foreach (System.Text.Json.Nodes.JsonNode? hostNode in hosts)
            {
                string host = hostNode?.ToString() ?? "";
                if (!HostRegex.IsMatch(host))
                    errors.Add("network host " + host + " is not a plain host name");
                else if (PluginNative.IsCollectingHost(host))
                    errors.Add(host + " is blocked for mods because it is used to collect data");
            }
        }
        if (manifest["tags"] is System.Text.Json.Nodes.JsonArray tags)
        {
            if (tags.Count > 5)
                errors.Add("plugin.json tags must be a list of up to 5 tags");
            foreach (System.Text.Json.Nodes.JsonNode? tagNode in tags)
            {
                string tag = tagNode?.ToString() ?? "";
                if (!KnownTags.Contains(tag))
                    errors.Add("tag " + tag + " is not in the library tags list, allowed: " + string.Join(", ", KnownTags.Order()));
            }
        }
        if (manifest["permissions"] is System.Text.Json.Nodes.JsonArray permissions)
        {
            string[] known = ["roblox-account", "native", "files", "web", "clipboard"];
            if (permissions.Any(permission => !known.Contains(permission?.ToString(), StringComparer.Ordinal)))
                errors.Add("plugin.json permissions may only contain roblox-account, native, files, web or clipboard");
            else if (
                permissions.Any(permission => permission?.ToString() is "native")
                && (permissions.Any(permission => permission?.ToString() is "web") || manifest["network"] is System.Text.Json.Nodes.JsonArray { Count: > 0 })
            )
                errors.Add("native cannot be combined with web or network hosts");
            else if (
                permissions.Any(permission => permission?.ToString() is "clipboard")
                && (permissions.Any(permission => permission?.ToString() is "web") || manifest["network"] is System.Text.Json.Nodes.JsonArray { Count: > 0 })
            )
                errors.Add("clipboard cannot be combined with web or network hosts");
            else if (
                permissions.Any(permission => permission?.ToString() is "roblox-account")
                && manifest["network"] is System.Text.Json.Nodes.JsonArray accountHosts
                && accountHosts.Any(host => !AllowedHosts.Contains(host?.ToString() ?? ""))
            )
                errors.Add("roblox-account can only be combined with trusted network hosts");
            else if (
                permissions.Any(permission => permission?.ToString() is "roblox-account")
                && permissions.Any(permission => permission?.ToString() is "native" or "files" or "web")
            )
                errors.Add("native, files and web cannot be combined with roblox-account");
            else if (permissions.Any(permission => permission?.ToString() is "native" or "files" or "web" or "clipboard"))
                reviews.Add(
                    "asks for native, files, web or clipboard access, the maintainer must approve every version, it shows a Powerful mod badge and users get a timed warning, describe exactly why it needs this"
                );
            else if (permissions.Count > 0)
                reviews.Add(
                    "asks to use the Roblox account through $.roblox.request, the maintainer must review it, make sure the description says exactly what is read"
                );
        }
        if (manifest["library"] is System.Text.Json.Nodes.JsonObject)
            reviews.Add(
                "plugin.json carries a library block, the publish window signs you in and writes owner.json from that account, the ids must match your own account"
            );
        string hooksPath = Path.Combine(folder, "hooks", "hooks.json");
        if (!File.Exists(hooksPath))
        {
            errors.Add("hooks/hooks.json is missing");
            return;
        }
        try
        {
            System.Text.Json.Nodes.JsonObject? hooks =
                System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(hooksPath)) as System.Text.Json.Nodes.JsonObject
                ?? throw new InvalidDataException("not an object");
            if (
                hooks["modules"] is not System.Text.Json.Nodes.JsonArray modules
                || modules.Count != 1
                || modules[0] is not System.Text.Json.Nodes.JsonValue moduleValue
                || !moduleValue.TryGetValue(out string? module)
            )
            {
                errors.Add("hooks.json must name exactly one module");
                return;
            }
            string hooksRoot = Path.GetFullPath(Path.Combine(folder, "hooks"));
            string entry = Path.GetFullPath(Path.Combine(hooksRoot, module));
            if (!entry.StartsWith(hooksRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(entry))
                errors.Add("the hooks module must exist inside hooks/");
        }
        catch (Exception ex)
        {
            errors.Add("hooks/hooks.json is missing or not valid JSON: " + ex.Message);
        }
    }

    private static async Task CheckVersionAsync(string mod, List<string> errors, CancellationToken token)
    {
        try
        {
            using System.Net.Http.HttpClient client = Voidstrap.Utility.VpnHttpClient.Create(TimeSpan.FromSeconds(20));
            string payload = await client.GetStringAsync(LibraryIndexUrl, token).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(payload);
            string? indexVersion = null;
            foreach (JsonElement entry in document.RootElement.GetProperty("mods").EnumerateArray())
            {
                if (string.Equals(entry.GetProperty("name").GetString(), mod, StringComparison.Ordinal))
                {
                    indexVersion = entry.GetProperty("version").GetString();
                    break;
                }
            }
            if (indexVersion == null)
                return;
            string manifestPath = Path.Combine(Paths.Plugins, mod, PluginManifest.ManifestFolder, "plugin.json");
            string localVersion = "0.0.0";
            if (
                File.Exists(manifestPath)
                && System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifestPath)) is System.Text.Json.Nodes.JsonObject manifest
            )
                localVersion = manifest["version"]?.ToString() ?? "0.0.0";
            if (CompareVersions(localVersion, indexVersion) <= 0)
                errors.Add(
                    "this mod already exists in the library at version "
                        + indexVersion
                        + ", raise plugin.json version above it, the gate rejects equal or lower versions with a red X"
                );
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            App.Logger?.WriteLine(LogIdent, "The library index could not be read for the version check: " + ex.Message);
        }
    }

    private static int CompareVersions(string left, string right)
    {
        int[] a = left.Split('.').Select(part => int.TryParse(part, out int value) ? value : 0).ToArray();
        int[] b = right.Split('.').Select(part => int.TryParse(part, out int value) ? value : 0).ToArray();
        for (int index = 0; index < 3; index++)
        {
            if (a[index] != b[index])
                return a[index].CompareTo(b[index]);
        }
        return 0;
    }
}
