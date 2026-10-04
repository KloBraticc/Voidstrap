using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace Voidstrap.Integrations.Nvidia;

public sealed class NvidiaSetting
{
    public uint Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public uint Value { get; init; }

    public NvSettingType Type { get; init; }

    public bool IsPredefined { get; init; }

    public string HexId => "0x" + Id.ToString("X8", CultureInfo.InvariantCulture);
}

public sealed class NvidiaApplyResult
{
    public bool Ok { get; init; }

    public string Message { get; init; } = string.Empty;

    public int Applied { get; init; }

    public List<string> Failures { get; } = new List<string>();
}

public static class NvidiaProfileInspector
{
    private sealed class Session(IntPtr handle) : IDisposable
    {
        private bool _disposed;

        public IntPtr Handle { get; } = handle;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            NvApi.DestroySession(Handle);
            GC.SuppressFinalize(this);
        }
    }

    public const string DefaultProfileName = "Voidstrap";

    public const string NeedsElevationMessage = "Windows only lets an administrator change driver profile settings. Restart Voidstrap as administrator and apply again";

    private static readonly string[] RobloxExecutables = new string[2] { "RobloxPlayerBeta.exe", "RobloxStudioBeta.exe" };

    public static bool IsAvailable => NvApi.IsAvailable;

    public static string UnavailableReason => NvApi.Failure;

    private static Session? Open(out string error)
    {
        error = string.Empty;
        if (!NvApi.IsAvailable)
        {
            error = NvApi.Failure.Length > 0 ? NvApi.Failure : "NVAPI is unavailable";
            return null;
        }
        NvStatus status = NvApi.CreateSession(out IntPtr session);
        if (status != NvStatus.Ok || session == IntPtr.Zero)
        {
            error = "Could not open a driver settings session (" + status + ")";
            return null;
        }
        status = NvApi.LoadSettings(session);
        if (status != NvStatus.Ok)
        {
            NvApi.DestroySession(session);
            error = "Could not read the driver settings (" + status + ")";
            return null;
        }
        return new Session(session);
    }

    public static List<NvidiaSetting> ReadProfile(string? profileName = null)
    {
        using Session? session = Open(out string error);
        if (session == null)
        {
            App.Logger?.WriteLine("NvidiaProfileInspector", "ReadProfile skipped: " + error);
            return new List<NvidiaSetting>();
        }
        IntPtr profile = ResolveTargetProfile(session.Handle, profileName, create: false, out string _, out NvStatus status);
        if (profile == IntPtr.Zero)
        {
            App.Logger?.WriteLine("NvidiaProfileInspector", "Profile not found for read (" + status + ")");
            return new List<NvidiaSetting>();
        }
        return Enumerate(session.Handle, profile);
    }

    public static Dictionary<uint, uint> ReadValues(IEnumerable<uint> settingIds, string? profileName = null)
    {
        Dictionary<uint, uint> values = new Dictionary<uint, uint>();
        HashSet<uint> wanted = new HashSet<uint>(settingIds ?? Array.Empty<uint>());
        if (wanted.Count == 0)
            return values;
        foreach (NvidiaSetting setting in ReadProfile(profileName))
        {
            if (setting.Type == NvSettingType.Dword && wanted.Contains(setting.Id))
                values[setting.Id] = setting.Value;
        }
        return values;
    }

    private static List<NvidiaSetting> Enumerate(IntPtr session, IntPtr profile, bool strict = false)
    {
        List<NvidiaSetting> results = new List<NvidiaSetting>();
        const int chunk = 128;
        IntPtr buffer = Marshal.AllocHGlobal(NvApi.SettingSize * chunk);
        try
        {
            uint index = 0u;
            while (true)
            {
                uint count = chunk;
                for (int i = 0; i < chunk; i++)
                    NvApi.WriteSettingVersion(buffer, i);
                NvStatus status = NvApi.EnumSettings(session, profile, index, ref count, buffer);
                if (status == NvStatus.EndEnumeration)
                    break;
                if (status != NvStatus.Ok)
                {
                    if (strict)
                        throw new InvalidOperationException("Could not enumerate NVIDIA settings (" + status + ")");
                    break;
                }
                if (count == 0u)
                    break;
                if (count > chunk)
                    throw new InvalidOperationException("The NVIDIA driver returned an invalid setting count");
                for (int i = 0; i < count; i++)
                {
                    NvSettingType type = NvApi.ReadSettingType(buffer, i);
                    results.Add(new NvidiaSetting
                    {
                        Id = NvApi.ReadSettingId(buffer, i),
                        Name = NvApi.ReadSettingName(buffer, i),
                        Value = type == NvSettingType.Dword ? NvApi.ReadSettingValue(buffer, i) : 0u,
                        Type = type,
                        IsPredefined = NvApi.ReadSettingIsPredefined(buffer, i),
                    });
                }
                if (count < chunk)
                    break;
                index += count;
            }
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine("NvidiaProfileInspector", "Enumeration failed: " + ex.Message);
            if (strict)
                throw;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return results;
    }

    public sealed class AppliedSetting
    {
        public string Profile { get; set; } = string.Empty;
        public uint Id { get; set; }
        public uint? Original { get; set; }
        public uint Value { get; set; }
    }

    private static string AppliedSettingsPath => Path.Combine(Paths.NipProfiles, "AppliedSettings.json");

    internal static bool HasSettingsToRestore => LoadAppliedSettings().Count > 0
        || (!File.Exists(AppliedSettingsPath) && File.Exists(Path.Combine(Paths.NipProfiles, "Voidstrap.nip")));

    private static List<AppliedSetting> LoadAppliedSettings()
    {
        if (!File.Exists(AppliedSettingsPath))
            return [];
        return JsonSerializer.Deserialize<List<AppliedSetting>>(File.ReadAllText(AppliedSettingsPath))
            ?? throw new InvalidDataException("The NVIDIA settings backup is invalid");
    }

    private static void SaveAppliedSettings(List<AppliedSetting> settings)
    {
        Directory.CreateDirectory(Paths.NipProfiles);
        string temporary = AppliedSettingsPath + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings));
            File.Move(temporary, AppliedSettingsPath, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static List<AppliedSetting> LoadSettingsBackup(IntPtr session)
    {
        List<AppliedSetting> settings = LoadAppliedSettings();
        if (!File.Exists(AppliedSettingsPath) && File.Exists(Path.Combine(Paths.NipProfiles, "Voidstrap.nip")))
        {
            IntPtr legacyProfile = ResolveTargetProfile(session, null, false, out string name, out NvStatus status);
            if (legacyProfile == IntPtr.Zero && status != NvStatus.ProfileNotFound)
                throw new InvalidOperationException("Could not resolve the NVIDIA profile (" + status + ")");
            if (legacyProfile != IntPtr.Zero)
            {
                Dictionary<uint, NvidiaSetting> live = Enumerate(session, legacyProfile, true).ToDictionary(setting => setting.Id);
                foreach (Models.NvidiaEditorEntry entry in Integrations.NvidiaProfileManager.LoadFromNip(Path.Combine(Paths.NipProfiles, "Voidstrap.nip")))
                {
                    if (!Integrations.NvidiaProfileManager.TryParseSettingId(entry.SettingId, out uint id)
                        || !Integrations.NvidiaProfileManager.TryParseSettingValue(entry.Value, entry.ValueType, out uint value)
                        || !live.TryGetValue(id, out NvidiaSetting? current)
                        || current.Type != NvSettingType.Dword || current.IsPredefined || current.Value != value)
                        continue;
                    settings.Add(new AppliedSetting { Profile = name, Id = id, Value = value });
                }
            }
        }
        return settings;
    }

    public static NvidiaApplyResult RestoreAppliedSettings()
    {
        using Utility.InterProcessLock operation = new("NvidiaSettings");
        if (!operation.IsAcquired)
            return Failure("Another NVIDIA settings operation is running");
        try
        {
            return RestoreAppliedSettingsCore();
        }
        catch (Exception ex)
        {
            App.Logger?.WriteException("NvidiaProfileInspector", ex);
            return Failure(ex.Message);
        }
    }

    private static NvidiaApplyResult RestoreAppliedSettingsCore()
    {
        using Session? session = Open(out string error);
        if (session == null)
            return Failure(error);
        List<AppliedSetting> settings = LoadSettingsBackup(session.Handle);
        int restored = 0;
        foreach (IGrouping<string, AppliedSetting> group in settings.GroupBy(setting => setting.Profile))
        {
            NvStatus status = NvApi.FindProfileByName(session.Handle, group.Key, out IntPtr profile);
            if (status == NvStatus.ProfileNotFound)
                continue;
            if (status != NvStatus.Ok || profile == IntPtr.Zero)
                return Failure("Could not read the NVIDIA profile (" + status + ")");
            Dictionary<uint, NvidiaSetting> live = Enumerate(session.Handle, profile, true).ToDictionary(setting => setting.Id);
            List<AppliedSetting> changed = [];
            foreach (AppliedSetting setting in group)
            {
                if (!live.TryGetValue(setting.Id, out NvidiaSetting? current) || current.IsPredefined
                    || current.Type != NvSettingType.Dword || current.Value != setting.Value)
                    continue;
                status = setting.Original is uint original
                    ? NvApi.SetDwordSetting(session.Handle, profile, setting.Id, original)
                    : NvApi.DeleteSetting(session.Handle, profile, setting.Id);
                if (status != NvStatus.Ok && status != NvStatus.SettingNotFound)
                    return Failure(status == NvStatus.InvalidUserPrivilege ? NeedsElevationMessage : "Could not restore NVIDIA setting " + setting.Id + ": " + status);
                restored++;
                changed.Add(setting);
            }
            if (changed.Count > 0)
            {
                Dictionary<uint, NvidiaSetting> checkedSettings = Enumerate(session.Handle, profile, true).ToDictionary(setting => setting.Id);
                foreach (AppliedSetting setting in changed)
                {
                    bool found = checkedSettings.TryGetValue(setting.Id, out NvidiaSetting? current);
                    bool restoredValue = setting.Original is uint original
                        ? found && current!.Type == NvSettingType.Dword && current.Value == original
                        : !found || current!.IsPredefined;
                    if (!restoredValue)
                        return Failure("The NVIDIA driver did not restore setting " + setting.Id);
                }
            }
        }
        if (restored > 0)
        {
            NvStatus status = NvApi.SaveSettings(session.Handle);
            if (status != NvStatus.Ok)
                return Failure(status == NvStatus.InvalidUserPrivilege ? NeedsElevationMessage : "Could not save the NVIDIA cleanup: " + status);
        }
        SaveAppliedSettings([]);
        return new NvidiaApplyResult { Ok = true, Applied = restored, Message = "Restored " + restored + " NVIDIA settings applied by Voidstrap" };
    }

    public static NvidiaApplyResult Apply(IEnumerable<KeyValuePair<uint, uint>> settings, string? profileName = null)
    {
        using Utility.InterProcessLock operation = new("NvidiaSettings");
        if (!operation.IsAcquired)
            return Failure("Another NVIDIA settings operation is running");
        try
        {
            return ApplyCore(settings, profileName);
        }
        catch (Exception ex)
        {
            App.Logger?.WriteException("NvidiaProfileInspector", ex);
            return Failure(ex.Message);
        }
    }

    private static NvidiaApplyResult ApplyCore(IEnumerable<KeyValuePair<uint, uint>> settings, string? profileName)
    {
        if (settings == null)
            return Failure("No settings were supplied");

        using Session? session = Open(out string error);
        if (session == null)
            return Failure(error);

        IntPtr profile = ResolveTargetProfile(session.Handle, profileName, create: true, out string targetName, out NvStatus status);
        if (profile == IntPtr.Zero)
            return Failure("Could not resolve a driver profile for Roblox (" + status + ")");

        NvStatus namedStatus = NvApi.FindProfileByName(session.Handle, targetName, out IntPtr namedProfile);
        if (namedStatus != NvStatus.Ok || namedProfile != profile)
            return Failure("Could not identify the NVIDIA profile for the settings backup");

        List<AppliedSetting> backup = LoadSettingsBackup(session.Handle);
        string originalBackup = JsonSerializer.Serialize(backup);
        List<NvidiaSetting> previous = Enumerate(session.Handle, profile, true);
        List<string> rejected = new List<string>();
        Dictionary<uint, uint> accepted = new();
        Dictionary<uint, uint> requested = new Dictionary<uint, uint>();
        int applied = 0;
        foreach (KeyValuePair<uint, uint> setting in settings)
        {
            if (previous.Exists(item => item.Id == setting.Key && item.Type != NvSettingType.Dword))
            {
                rejected.Add("0x" + setting.Key.ToString("X8", CultureInfo.InvariantCulture) + ": this setting is not a DWORD value");
                continue;
            }
            NvStatus set = NvApi.SetDwordSetting(session.Handle, profile, setting.Key, setting.Value);
            if (set == NvStatus.Ok)
            {
                applied++;
                requested[setting.Key] = setting.Value;
                accepted[setting.Key] = setting.Value;
                continue;
            }
            if (set == NvStatus.InvalidUserPrivilege)
                return Failure(NeedsElevationMessage);
            rejected.Add("0x" + setting.Key.ToString("X8", CultureInfo.InvariantCulture) + " -> " + set);
        }

        foreach (NvidiaSetting live in Enumerate(session.Handle, profile, true))
        {
            if (!requested.TryGetValue(live.Id, out uint wanted))
                continue;
            requested.Remove(live.Id);
            if (live.Type != NvSettingType.Dword || live.Value == wanted)
                continue;
            applied--;
            accepted.Remove(live.Id);
            rejected.Add("0x" + live.Id.ToString("X8", CultureInfo.InvariantCulture) + " -> the driver stored " + live.Value + " instead of " + wanted);
        }
        foreach (uint missing in requested.Keys)
        {
            applied--;
            accepted.Remove(missing);
            rejected.Add("0x" + missing.ToString("X8", CultureInfo.InvariantCulture) + " -> the driver did not keep this setting");
        }

        foreach (KeyValuePair<uint, uint> setting in accepted)
        {
            AppliedSetting? tracked = backup.Find(item => item.Profile == targetName && item.Id == setting.Key);
            NvidiaSetting? original = previous.Find(item => item.Id == setting.Key);
            if (tracked == null)
            {
                tracked = new AppliedSetting { Profile = targetName, Id = setting.Key, Original = original is { IsPredefined: false, Type: NvSettingType.Dword } ? original.Value : null };
                backup.Add(tracked);
            }
            else if (original == null || original.IsPredefined)
                tracked.Original = null;
            else if (original.Type == NvSettingType.Dword && original.Value != tracked.Value)
                tracked.Original = original.Value;
            tracked.Value = setting.Value;
        }
        SaveAppliedSettings(backup);
        NvStatus save = NvApi.SaveSettings(session.Handle);
        if (save != NvStatus.Ok)
            SaveAppliedSettings(JsonSerializer.Deserialize<List<AppliedSetting>>(originalBackup)!);
        if (save == NvStatus.InvalidUserPrivilege)
            return Failure(NeedsElevationMessage);
        if (save != NvStatus.Ok)
        {
            NvidiaApplyResult saveFailed = new NvidiaApplyResult
            {
                Ok = false,
                Message = "The driver rejected the save (" + save + ")",
                Applied = applied,
            };
            saveFailed.Failures.AddRange(rejected);
            return saveFailed;
        }

        NvidiaApplyResult result = new NvidiaApplyResult
        {
            Ok = applied > 0,
            Applied = applied,
            Message = rejected.Count == 0
                ? "Applied " + applied + " setting(s) to the \"" + targetName + "\" driver profile"
                : applied > 0
                    ? "Applied " + applied + " setting(s) to \"" + targetName + "\", " + rejected.Count + " did not stick"
                    : "The driver did not keep any of the " + rejected.Count + " setting(s)",
        };
        result.Failures.AddRange(rejected);
        App.Logger?.WriteLine("NvidiaProfileInspector", result.Message);
        return result;
    }

    public static NvidiaApplyResult ResetAll(string? profileName = null)
    {
        using Session? session = Open(out string error);
        if (session == null)
            return new NvidiaApplyResult { Ok = false, Message = error };

        IntPtr profile = ResolveTargetProfile(session.Handle, profileName, create: false, out string _, out NvStatus _);
        if (profile == IntPtr.Zero)
            return new NvidiaApplyResult { Ok = true, Message = "Roblox had no NVIDIA driver settings to reset" };

        List<string> rejected = new List<string>();
        int cleared = 0;
        foreach (NvidiaSetting setting in Enumerate(session.Handle, profile))
        {
            if (setting.IsPredefined)
                continue;
            uint id = setting.Id;
            NvStatus status = NvApi.DeleteSetting(session.Handle, profile, id);
            if (status == NvStatus.Ok)
            {
                cleared++;
                continue;
            }
            if (status == NvStatus.InvalidUserPrivilege)
                return new NvidiaApplyResult { Ok = false, Message = NeedsElevationMessage };
            if (status == NvStatus.SettingNotFound)
                continue;
            rejected.Add("0x" + id.ToString("X8", CultureInfo.InvariantCulture) + " -> " + status);
        }

        NvStatus save = NvApi.SaveSettings(session.Handle);
        if (save == NvStatus.InvalidUserPrivilege)
            return new NvidiaApplyResult { Ok = false, Message = NeedsElevationMessage };

        NvidiaApplyResult result = new NvidiaApplyResult
        {
            Ok = save == NvStatus.Ok,
            Applied = cleared,
            Message = save == NvStatus.Ok
                ? "Reset " + cleared + " NVIDIA setting(s) for Roblox to the driver default"
                : "The driver rejected the save (" + save + ")",
        };
        result.Failures.AddRange(rejected);
        App.Logger?.WriteLine("NvidiaProfileInspector", result.Message);
        return result;
    }

    private static NvidiaApplyResult Failure(string message)
    {
        return new NvidiaApplyResult { Ok = false, Message = message };
    }

    private static IntPtr ResolveProfile(IntPtr session, string? profileName, bool create, out NvStatus status)
    {
        string name = string.IsNullOrWhiteSpace(profileName) ? DefaultProfileName : profileName.Trim();
        status = NvApi.FindProfileByName(session, name, out IntPtr profile);
        if (status == NvStatus.Ok && profile != IntPtr.Zero)
            return profile;
        if (!create)
            return IntPtr.Zero;
        status = NvApi.CreateProfile(session, name, out profile);
        return status == NvStatus.Ok ? profile : IntPtr.Zero;
    }

    private static IntPtr ResolveTargetProfile(IntPtr session, string? profileName, bool create, out string targetName, out NvStatus status)
    {
        string preferred = string.IsNullOrWhiteSpace(profileName) ? DefaultProfileName : profileName.Trim();
        foreach (string executable in RobloxExecutables)
        {
            try
            {
                if (NvApi.FindApplication(session, executable, out IntPtr owner) == NvStatus.Ok && owner != IntPtr.Zero)
                {
                    targetName = NvApi.GetProfileName(session, owner, preferred);
                    status = NvStatus.Ok;
                    return owner;
                }
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine("NvidiaProfileInspector", "Owner lookup for " + executable + " failed: " + ex.Message);
            }
        }

        IntPtr profile = ResolveProfile(session, preferred, create, out status);
        targetName = preferred;
        if (profile == IntPtr.Zero)
            return IntPtr.Zero;
        if (!create)
            return profile;

        foreach (string executable in RobloxExecutables)
        {
            try
            {
                NvStatus attach = NvApi.CreateApplication(session, profile, executable);
                if (attach != NvStatus.Ok && attach != NvStatus.ExecutableAlreadyInProfile)
                    App.Logger?.WriteLine("NvidiaProfileInspector", "Could not attach " + executable + " (" + attach + ")");
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine("NvidiaProfileInspector", "Attaching " + executable + " failed: " + ex.Message);
            }
        }
        return profile;
    }
}
