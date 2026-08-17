using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace Voidstrap.Integrations.Nvidia;

public enum NvStatus
{
    Ok = 0,
    Error = -1,
    LibraryNotFound = -2,
    NoImplementation = -3,
    ApiNotInitialized = -4,
    InvalidArgument = -5,
    NvidiaDeviceNotFound = -6,
    EndEnumeration = -7,
    InvalidHandle = -8,
    IncompatibleStructVersion = -9,
    InvalidPointer = -14,
    InvalidUserPrivilege = -137,
    SettingNotFound = -160,
    SettingSizeTooLarge = -161,
    ProfileNotFound = -163,
    ProfileNameEmpty = -164,
    ExecutableNotFound = -166,
    ExecutableAlreadyInProfile = -167,
}

public enum NvSettingType
{
    Dword = 0,
    Binary = 1,
    AnsiString = 2,
    UnicodeString = 3,
}

internal static class NvApi
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr QueryInterfaceDelegate(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvStatus NoArgs();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvStatus OutHandle(out IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvStatus WithHandle(IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvStatus FindProfile(IntPtr session, IntPtr name, out IntPtr profile);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvStatus CreateProfileDelegate(IntPtr session, IntPtr profile, out IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvStatus SessionProfileBuffer(IntPtr session, IntPtr profile, IntPtr buffer);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvStatus DeleteSettingDelegate(IntPtr session, IntPtr profile, uint settingId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvStatus FindAppDelegate(IntPtr session, IntPtr name, out IntPtr profile, IntPtr app);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvStatus EnumSettingsDelegate(IntPtr session, IntPtr profile, uint startIndex, ref uint count, IntPtr buffer);

    private const int UnicodeStringMax = 2048;

    private const int BinaryDataMax = 4096;

    private const int SettingOffsetVersion = 0;
    private const int SettingOffsetName = 4;
    private const int SettingOffsetId = 4100;
    private const int SettingOffsetType = 4104;
    private const int SettingOffsetLocation = 4108;
    private const int SettingOffsetIsCurrentPredefined = 4112;
    private const int SettingOffsetIsPredefinedValid = 4116;
    private const int SettingOffsetPredefinedUnion = 4120;
    private const int UnionSize = 4100;
    private const int SettingOffsetCurrentUnion = 8220;
    internal const int SettingSize = 12320;

    private const int ProfileOffsetVersion = 0;
    private const int ProfileOffsetName = 4;
    private const int ProfileOffsetGpuSupport = 4100;
    private const int ProfileOffsetIsPredefined = 4104;
    private const int ProfileOffsetNumOfApps = 4108;
    private const int ProfileOffsetNumOfSettings = 4112;
    internal const int ProfileSize = 4116;

    private const int AppOffsetVersion = 0;
    private const int AppOffsetIsPredefined = 4;
    private const int AppOffsetAppName = 8;
    private const int AppOffsetUserFriendlyName = 4104;
    private const int AppOffsetLauncher = 8200;
    internal const int AppSize = 12296;

    internal static readonly int SettingVersion = MakeVersion(SettingSize, 1);

    internal static readonly int ProfileVersion = MakeVersion(ProfileSize, 1);

    internal static readonly int AppVersion = MakeVersion(AppSize, 1);

    private const uint IdInitialize = 0x0150E828u;
    private const uint IdCreateSession = 0x0694D52Eu;
    private const uint IdDestroySession = 0xDAD9CFF8u;
    private const uint IdLoadSettings = 0x375DBD6Bu;
    private const uint IdSaveSettings = 0xFCBC7E14u;
    private const uint IdFindProfileByName = 0x7E4A9A0Bu;
    private const uint IdCreateProfile = 0xCC176068u;
    private const uint IdSetSetting = 0x577DD202u;
    private const uint IdDeleteProfileSetting = 0xE4A26362u;
    private const uint IdFindApplicationByName = 0xEEE566B2u;
    private const uint IdCreateApplication = 0x4347A9DEu;
    private const uint IdEnumSettings = 0xAE3039DAu;
    private const uint IdGetProfileInfo = 0x61CD6FD6u;

    private static readonly object Gate = new object();

    private static readonly ConcurrentDictionary<uint, Delegate?> _resolved = new ConcurrentDictionary<uint, Delegate?>();

    private static IntPtr _library = IntPtr.Zero;

    private static QueryInterfaceDelegate? _queryInterface;

    private static bool _initialized;

    private static bool _probed;

    private static string _failure = string.Empty;

    internal static string Failure => _failure;

    internal static bool IsAvailable
    {
        get
        {
            lock (Gate)
            {
                Probe();
                return _initialized;
            }
        }
    }

    private static int MakeVersion(int structSize, int version) => structSize | (version << 16);

    private static void Probe()
    {
        if (_probed)
            return;
        _probed = true;

        if (!OperatingSystem.IsWindows())
        {
            _failure = "NVAPI is only available on Windows";
            return;
        }

        string name = Environment.Is64BitProcess ? "nvapi64.dll" : "nvapi.dll";
        if (!NativeLibrary.TryLoad(name, out _library))
        {
            _failure = name + " could not be loaded, the NVIDIA driver may not be installed";
            return;
        }

        if (!NativeLibrary.TryGetExport(_library, "nvapi_QueryInterface", out IntPtr address))
        {
            _failure = "nvapi_QueryInterface was not found in " + name;
            Unload();
            return;
        }

        _queryInterface = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(address);

        NoArgs? initialize = ResolveRaw<NoArgs>(IdInitialize);
        if (initialize == null)
        {
            _failure = "NvAPI_Initialize was not exposed by the driver";
            Unload();
            return;
        }

        NvStatus status = initialize();
        if (status != NvStatus.Ok)
        {
            _failure = "NvAPI_Initialize failed with " + status;
            Unload();
            return;
        }

        _initialized = true;
    }

    private static void Unload()
    {
        _queryInterface = null;
        if (_library == IntPtr.Zero)
            return;
        try
        {
            NativeLibrary.Free(_library);
        }
        catch
        {
        }
        _library = IntPtr.Zero;
    }

    private static T? Resolve<T>(uint id) where T : Delegate
    {
        if (_resolved.TryGetValue(id, out Delegate? cached))
            return cached as T;
        lock (Gate)
        {
            Probe();
            if (!_initialized)
                return null;
            T? resolved = ResolveRaw<T>(id);
            _resolved[id] = resolved;
            return resolved;
        }
    }

    private static T? ResolveRaw<T>(uint id) where T : Delegate
    {
        QueryInterfaceDelegate? query = _queryInterface;
        if (query == null)
            return null;
        IntPtr pointer = query(id);
        return pointer == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(pointer);
    }

    internal static NvStatus CreateSession(out IntPtr session)
    {
        session = IntPtr.Zero;
        OutHandle? create = Resolve<OutHandle>(IdCreateSession);
        return create == null ? NvStatus.NoImplementation : create(out session);
    }

    internal static void DestroySession(IntPtr session)
    {
        if (session == IntPtr.Zero)
            return;
        Resolve<WithHandle>(IdDestroySession)?.Invoke(session);
    }

    internal static NvStatus LoadSettings(IntPtr session)
    {
        WithHandle? load = Resolve<WithHandle>(IdLoadSettings);
        return load == null ? NvStatus.NoImplementation : load(session);
    }

    internal static NvStatus SaveSettings(IntPtr session)
    {
        WithHandle? save = Resolve<WithHandle>(IdSaveSettings);
        return save == null ? NvStatus.NoImplementation : save(session);
    }

    internal static NvStatus FindProfileByName(IntPtr session, string name, out IntPtr profile)
    {
        profile = IntPtr.Zero;
        FindProfile? find = Resolve<FindProfile>(IdFindProfileByName);
        if (find == null)
            return NvStatus.NoImplementation;
        IntPtr buffer = AllocUnicodeString(name);
        try
        {
            return find(session, buffer, out profile);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static NvStatus CreateProfile(IntPtr session, string name, out IntPtr profile)
    {
        profile = IntPtr.Zero;
        CreateProfileDelegate? create = Resolve<CreateProfileDelegate>(IdCreateProfile);
        if (create == null)
            return NvStatus.NoImplementation;
        IntPtr buffer = Marshal.AllocHGlobal(ProfileSize);
        try
        {
            Zero(buffer, ProfileSize);
            Marshal.WriteInt32(buffer, ProfileOffsetVersion, ProfileVersion);
            WriteUnicodeString(buffer, ProfileOffsetName, name);
            Marshal.WriteInt32(buffer, ProfileOffsetGpuSupport, 0);
            return create(session, buffer, out profile);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static NvStatus SetDwordSetting(IntPtr session, IntPtr profile, uint settingId, uint value)
    {
        SessionProfileBuffer? set = Resolve<SessionProfileBuffer>(IdSetSetting);
        if (set == null)
            return NvStatus.NoImplementation;
        IntPtr buffer = Marshal.AllocHGlobal(SettingSize);
        try
        {
            Zero(buffer, SettingSize);
            Marshal.WriteInt32(buffer, SettingOffsetVersion, SettingVersion);
            Marshal.WriteInt32(buffer, SettingOffsetId, unchecked((int)settingId));
            Marshal.WriteInt32(buffer, SettingOffsetType, 0);
            Marshal.WriteInt32(buffer, SettingOffsetCurrentUnion, unchecked((int)value));
            return set(session, profile, buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static NvStatus DeleteSetting(IntPtr session, IntPtr profile, uint settingId)
    {
        DeleteSettingDelegate? del = Resolve<DeleteSettingDelegate>(IdDeleteProfileSetting);
        return del == null ? NvStatus.NoImplementation : del(session, profile, settingId);
    }

    internal static NvStatus CreateApplication(IntPtr session, IntPtr profile, string executable)
    {
        SessionProfileBuffer? create = Resolve<SessionProfileBuffer>(IdCreateApplication);
        if (create == null)
            return NvStatus.NoImplementation;
        IntPtr buffer = Marshal.AllocHGlobal(AppSize);
        try
        {
            Zero(buffer, AppSize);
            Marshal.WriteInt32(buffer, AppOffsetVersion, AppVersion);
            Marshal.WriteInt32(buffer, AppOffsetIsPredefined, 0);
            WriteUnicodeString(buffer, AppOffsetAppName, executable);
            WriteUnicodeString(buffer, AppOffsetUserFriendlyName, executable);
            WriteUnicodeString(buffer, AppOffsetLauncher, string.Empty);
            return create(session, profile, buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static NvStatus FindApplication(IntPtr session, string executable, out IntPtr profile)
    {
        profile = IntPtr.Zero;
        FindAppDelegate? find = Resolve<FindAppDelegate>(IdFindApplicationByName);
        if (find == null)
            return NvStatus.NoImplementation;
        IntPtr name = AllocUnicodeString(executable);
        IntPtr app = Marshal.AllocHGlobal(AppSize);
        try
        {
            Zero(app, AppSize);
            Marshal.WriteInt32(app, AppOffsetVersion, AppVersion);
            return find(session, name, out profile, app);
        }
        finally
        {
            Marshal.FreeHGlobal(app);
            Marshal.FreeHGlobal(name);
        }
    }

    internal static string GetProfileName(IntPtr session, IntPtr profile, string fallback)
    {
        SessionProfileBuffer? info = Resolve<SessionProfileBuffer>(IdGetProfileInfo);
        if (info == null || profile == IntPtr.Zero)
            return fallback;
        IntPtr buffer = Marshal.AllocHGlobal(ProfileSize);
        try
        {
            Zero(buffer, ProfileSize);
            Marshal.WriteInt32(buffer, ProfileOffsetVersion, ProfileVersion);
            if (info(session, profile, buffer) != NvStatus.Ok)
                return fallback;
            string name = ReadUnicodeString(buffer, ProfileOffsetName);
            return name.Length > 0 ? name : fallback;
        }
        catch
        {
            return fallback;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static NvStatus EnumSettings(IntPtr session, IntPtr profile, uint startIndex, ref uint count, IntPtr buffer)
    {
        EnumSettingsDelegate? enumerate = Resolve<EnumSettingsDelegate>(IdEnumSettings);
        return enumerate == null ? NvStatus.NoImplementation : enumerate(session, profile, startIndex, ref count, buffer);
    }

    internal static uint ReadSettingId(IntPtr buffer, int index) =>
        unchecked((uint)Marshal.ReadInt32(buffer, (index * SettingSize) + SettingOffsetId));

    internal static uint ReadSettingValue(IntPtr buffer, int index) =>
        unchecked((uint)Marshal.ReadInt32(buffer, (index * SettingSize) + SettingOffsetCurrentUnion));

    internal static NvSettingType ReadSettingType(IntPtr buffer, int index) =>
        (NvSettingType)Marshal.ReadInt32(buffer, (index * SettingSize) + SettingOffsetType);

    internal static bool ReadSettingIsPredefined(IntPtr buffer, int index) =>
        Marshal.ReadInt32(buffer, (index * SettingSize) + SettingOffsetIsCurrentPredefined) != 0;

    internal static string ReadSettingName(IntPtr buffer, int index) =>
        ReadUnicodeString(buffer, (index * SettingSize) + SettingOffsetName);

    internal static void WriteSettingVersion(IntPtr buffer, int index) =>
        Marshal.WriteInt32(buffer, index * SettingSize, SettingVersion);

    private static IntPtr AllocUnicodeString(string value)
    {
        IntPtr buffer = Marshal.AllocHGlobal(UnicodeStringMax * 2);
        Zero(buffer, UnicodeStringMax * 2);
        WriteUnicodeString(buffer, 0, value);
        return buffer;
    }

    private static void WriteUnicodeString(IntPtr buffer, int offset, string value)
    {
        string text = value ?? string.Empty;
        int max = UnicodeStringMax - 1;
        if (text.Length > max)
            text = text.Substring(0, max);
        for (int i = 0; i < text.Length; i++)
            Marshal.WriteInt16(buffer, offset + (i * 2), (short)text[i]);
        Marshal.WriteInt16(buffer, offset + (text.Length * 2), 0);
    }

    private static string ReadUnicodeString(IntPtr buffer, int offset)
    {
        StringBuilder builder = new StringBuilder(64);
        for (int i = 0; i < UnicodeStringMax; i++)
        {
            short ch = Marshal.ReadInt16(buffer, offset + (i * 2));
            if (ch == 0)
                break;
            builder.Append((char)ch);
        }
        return builder.ToString();
    }

    private static void Zero(IntPtr buffer, int length)
    {
        int whole = length - (length % 4);
        for (int i = 0; i < whole; i += 4)
            Marshal.WriteInt32(buffer, i, 0);
        for (int i = whole; i < length; i++)
            Marshal.WriteByte(buffer, i, 0);
    }
}
