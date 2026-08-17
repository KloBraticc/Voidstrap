using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Voidstrap.Integrations.Nvidia;
using Voidstrap.Models;

namespace Voidstrap.Integrations
{
    public static class NvidiaProfileManager
    {
        private const long MaxNipBytes = 16L * 1024L * 1024L;

        private static readonly Encoding Utf16Bom = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);

        public static void SaveToNip(string path, IEnumerable<NvidiaEditorEntry> entries)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            Dictionary<string, XElement> byId = new Dictionary<string, XElement>(StringComparer.Ordinal);
            List<string> order = new List<string>();

            foreach (NvidiaEditorEntry entry in entries ?? Array.Empty<NvidiaEditorEntry>())
            {
                if (entry == null || !TryNormalizeSettingId(entry.SettingId, out string fixedId))
                    continue;

                if (!byId.ContainsKey(fixedId))
                    order.Add(fixedId);

                byId[fixedId] = new XElement("ProfileSetting",
                    new XElement("SettingNameInfo", string.IsNullOrWhiteSpace(entry.Name) ? "Setting " + fixedId : entry.Name),
                    new XElement("SettingID", fixedId),
                    new XElement("ValueType", NormalizeValueType(entry.ValueType)),
                    new XElement("SettingValue", entry.Value ?? "0"));
            }

            XElement settings = new XElement("Settings");
            foreach (string id in order)
                settings.Add(byId[id]);

            XDocument doc = new XDocument(
                new XDeclaration("1.0", "utf-16", null),
                new XElement("ArrayOfProfile",
                    new XElement("Profile",
                        new XElement("ProfileName", "Voidstrap"),
                        new XElement("Executeables",
                            new XElement("string", "robloxplayerbeta.exe"),
                            new XElement("string", "robloxstudiobeta.exe")),
                        settings)));

            WriteUtf16Xml(path, doc);
        }

        public static List<NvidiaEditorEntry> LoadFromNip(string path)
        {
            List<NvidiaEditorEntry> results = new List<NvidiaEditorEntry>();

            if (!File.Exists(path))
                return results;

            try
            {
                if (new FileInfo(path).Length > MaxNipBytes)
                    return results;
            }
            catch
            {
                return results;
            }

            XDocument doc;
            try
            {
                doc = XDocument.Load(path);
            }
            catch
            {
                return results;
            }

            foreach (XElement node in doc.Descendants("ProfileSetting"))
            {
                string? name = node.Element("SettingNameInfo")?.Value;
                string? id = node.Element("SettingID")?.Value;
                string? value = node.Element("SettingValue")?.Value;
                string? type = node.Element("ValueType")?.Value;

                if (!TryNormalizeSettingId(id, out string fixedId))
                    continue;

                results.Add(new NvidiaEditorEntry
                {
                    Name = string.IsNullOrWhiteSpace(name) ? "Setting " + fixedId : name,
                    SettingId = fixedId,
                    Value = value ?? "0",
                    ValueType = NormalizeValueType(type),
                });
            }

            return results;
        }

        private static bool TryNormalizeSettingId(string? raw, out string result)
        {
            if (!TryParseSettingId(raw, out uint id))
            {
                result = null!;
                return false;
            }
            result = id.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        private static string NormalizeValueType(string? type)
        {
            return type?.ToLowerInvariant() switch
            {
                "string" => "String",
                "binary" => "Binary",
                "boolean" => "Boolean",
                "hex" => "Hex",
                _ => "Dword",
            };
        }

        private static void WriteUtf16Xml(string path, XDocument doc)
        {
            using XmlWriter writer = XmlWriter.Create(path, new XmlWriterSettings
            {
                Encoding = Utf16Bom,
                Indent = true,
                OmitXmlDeclaration = false,
            });
            doc.Save(writer);
        }

        public static NvidiaApplyResult ApplyToDriver(IEnumerable<NvidiaEditorEntry> entries, string? profileName = null)
        {
            List<KeyValuePair<uint, uint>> pairs = new List<KeyValuePair<uint, uint>>();
            List<string> skipped = new List<string>();

            foreach (NvidiaEditorEntry entry in entries ?? Array.Empty<NvidiaEditorEntry>())
            {
                if (entry == null)
                    continue;

                if (!TryParseSettingId(entry.SettingId, out uint id))
                {
                    skipped.Add((entry.Name ?? "setting") + ": id \"" + entry.SettingId + "\" is not a number");
                    continue;
                }

                string kind = (entry.ValueType ?? "Dword").Trim().ToLowerInvariant();
                if (kind == "string" || kind == "binary")
                {
                    skipped.Add((entry.Name ?? "setting") + ": " + entry.ValueType + " values are not supported");
                    continue;
                }

                if (!TryParseSettingValue(entry.Value, entry.ValueType, out uint value))
                {
                    skipped.Add((entry.Name ?? "setting") + ": value \"" + entry.Value + "\" is not valid for " + entry.ValueType);
                    continue;
                }

                pairs.Add(new KeyValuePair<uint, uint>(id, value));
            }

            if (pairs.Count == 0)
            {
                NvidiaApplyResult empty = new NvidiaApplyResult
                {
                    Ok = skipped.Count == 0,
                    Message = skipped.Count == 0
                        ? "There were no settings to apply"
                        : "None of the " + skipped.Count + " setting(s) could be applied",
                };
                empty.Failures.AddRange(skipped);
                return empty;
            }

            NvidiaApplyResult result = NvidiaProfileInspector.Apply(pairs, profileName);
            result.Failures.AddRange(skipped);
            return result;
        }

        public static int RefreshValuesFromDriver(IEnumerable<NvidiaEditorEntry> entries, string? profileName = null)
        {
            List<NvidiaEditorEntry> managed = new List<NvidiaEditorEntry>();
            List<uint> ids = new List<uint>();

            foreach (NvidiaEditorEntry entry in entries ?? Array.Empty<NvidiaEditorEntry>())
            {
                if (entry == null || !TryParseSettingId(entry.SettingId, out uint id))
                    continue;
                managed.Add(entry);
                ids.Add(id);
            }

            if (ids.Count == 0)
                return 0;

            Dictionary<uint, uint> live = NvidiaProfileInspector.ReadValues(ids, profileName);
            if (live.Count == 0)
                return 0;

            int refreshed = 0;
            for (int i = 0; i < managed.Count; i++)
            {
                if (!live.TryGetValue(ids[i], out uint value))
                    continue;
                string text = value.ToString(CultureInfo.InvariantCulture);
                if (managed[i].Value == text)
                    continue;
                managed[i].Value = text;
                refreshed++;
            }
            return refreshed;
        }

        public static async Task<NvidiaApplyResult> ApplyElevatedAsync(IEnumerable<NvidiaEditorEntry> entries)
        {
            string payload = Path.Combine(Path.GetTempPath(), "Voidstrap", Path.GetRandomFileName() + ".nip");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(payload)!);
                SaveToNip(payload, entries.ToList());
            }
            catch (Exception ex)
            {
                return new NvidiaApplyResult
                {
                    Ok = false,
                    Message = "Could not stage the settings for the elevated apply: " + ex.Message,
                };
            }

            try
            {
                ProcessStartInfo start = new ProcessStartInfo
                {
                    FileName = Environment.ProcessPath ?? Paths.Application,
                    UseShellExecute = true,
                    Verb = "runas",
                };
                start.ArgumentList.Add("-nvapply");
                start.ArgumentList.Add(payload);

                using Process? child = Process.Start(start);
                if (child == null)
                {
                    return new NvidiaApplyResult
                    {
                        Ok = false,
                        Message = "The elevated helper did not start.",
                    };
                }

                await child.WaitForExitAsync().ConfigureAwait(continueOnCapturedContext: false);
                return child.ExitCode == 0
                    ? new NvidiaApplyResult { Ok = true, Message = "Applied to the driver as administrator." }
                    : new NvidiaApplyResult { Ok = false, Message = "The elevated apply reported a problem. Check the log for details." };
            }
            catch (Win32Exception)
            {
                return new NvidiaApplyResult
                {
                    Ok = false,
                    Message = "Administrator access was declined, so nothing was changed.",
                };
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("NvidiaProfileManager::ApplyElevatedAsync", "Elevated apply failed: " + ex.Message);
                return new NvidiaApplyResult
                {
                    Ok = false,
                    Message = "Could not run the elevated apply.",
                };
            }
            finally
            {
                try
                {
                    if (File.Exists(payload))
                        File.Delete(payload);
                }
                catch
                {
                }
            }
        }

        public static bool ApplyStagedFile(string path)
        {
            try
            {
                List<NvidiaEditorEntry> entries = LoadFromNip(path);
                if (entries.Count == 0)
                {
                    App.Logger.WriteLine("NvidiaProfileManager::ApplyStagedFile", "Nothing to apply from " + path);
                    return false;
                }
                NvidiaApplyResult result = ApplyToDriver(entries);
                App.Logger.WriteLine("NvidiaProfileManager::ApplyStagedFile", result.Message);
                foreach (string failure in result.Failures)
                    App.Logger.WriteLine("NvidiaProfileManager::ApplyStagedFile", "  " + failure);
                return result.Ok;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("NvidiaProfileManager::ApplyStagedFile", "Failed: " + ex.Message);
                return false;
            }
        }

        private static bool TryParseSettingId(string? raw, out uint id)
        {
            id = 0u;
            string text = (raw ?? string.Empty).Trim();
            if (text.Length == 0)
                return false;
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return uint.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id);
            return uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
        }

        private static bool TryParseSettingValue(string? raw, string? valueType, out uint value)
        {
            value = 0u;
            string text = (raw ?? string.Empty).Trim();
            if (text.Length == 0)
                return false;

            string kind = (valueType ?? "Dword").Trim().ToLowerInvariant();

            if (kind == "boolean")
            {
                if (bool.TryParse(text, out bool flag))
                {
                    value = flag ? 1u : 0u;
                    return true;
                }
                if (text == "1" || text == "0")
                {
                    value = text == "1" ? 1u : 0u;
                    return true;
                }
                return false;
            }

            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return uint.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);

            if (kind == "hex")
                return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);

            if (uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                return true;

            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int signed))
            {
                value = unchecked((uint)signed);
                return true;
            }

            return false;
        }
    }
}
