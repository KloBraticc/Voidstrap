using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;

namespace Voidstrap.Integrations.Plugins;

internal static partial class PluginNative
{
    private static readonly string[] AllowedPrefixes =
    [
        "System.Windows.",
        "Wpf.Ui.",
        "Voidstrap.UI.",
        "Voidstrap.Models.",
        "Voidstrap.Enums.",
        "Voidstrap.Resources.",
    ];

    private static readonly string[] DeniedPrefixes =
    [
        "System.Windows.Markup",
        "System.Windows.Input",
        "System.Windows.Interop",
        "System.Windows.Navigation",
        "System.Windows.Threading",
        "System.Windows.Data",
        "System.Windows.Forms",
        "System.Windows.Resources",
        "System.Windows.Xps",
        "System.Windows.Baml2006",
        "Voidstrap.UI.Elements.ContextMenu.Web",
    ];

    private static readonly HashSet<Type> AllowedSystemTypes =
    [
        typeof(object),
        typeof(string),
        typeof(bool),
        typeof(int),
        typeof(long),
        typeof(double),
        typeof(float),
        typeof(decimal),
        typeof(byte),
        typeof(char),
        typeof(void),
        typeof(Math),
        typeof(TimeSpan),
        typeof(DateTime),
        typeof(Enum),
        typeof(ValueType),
        typeof(Array),
        typeof(App),
        typeof(EventArgs),
        typeof(MulticastDelegate),
        typeof(Delegate),
    ];

    [GeneratedRegex(
        "cookie|account|token|password|credential|secret|auth|login|process|shell|execute|registry|download|install|webview|webbrowser|clipboard|plugin|consent|rollback|mcp|assembly|reflection|gettype|loadcomponent|dispatcher|objectdataprovider|xmldataprovider|printdialog|voidstrapmods|messagebox|textbox|richtext|autosuggest|textinput|textcomposition",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex SensitiveName();

    [GeneratedRegex("file|path|url|uri", RegexOptions.IgnoreCase)]
    private static partial Regex VoidstrapLocationName();

    private static readonly HashSet<string> DeniedMembers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Source",
        "UriSource",
        "StreamSource",
        "BaseUri",
        "NavigateUri",
        "GetResourceStream",
        "GetContentStream",
        "GetRemoteStream",
        "Shutdown",
        "Parent",
        "TemplatedParent",
        "Owner",
        "RaiseEvent",
        "KeyDown",
        "KeyUp",
        "PreviewKeyDown",
        "PreviewKeyUp",
    };

    private static readonly Assembly[] SearchAssemblies =
    [
        typeof(App).Assembly,
        typeof(FrameworkElement).Assembly,
        typeof(UIElement).Assembly,
        typeof(DependencyObject).Assembly,
        typeof(Wpf.Ui.Controls.Button).Assembly,
    ];

    public static bool IsPowerful(PluginManifest manifest) =>
        manifest.HasPermission(PluginManifest.NativePermission)
        || manifest.HasPermission(PluginManifest.FilesPermission)
        || manifest.HasPermission(PluginManifest.WebPermission);

    public static bool IsTypeAllowed(Type? type)
    {
        if (type == null)
            return false;
        if (type.IsArray || type.IsByRef)
            return IsTypeAllowed(type.GetElementType());
        if (type.IsGenericType)
            return type.GetGenericArguments().All(IsTypeAllowed) && IsGenericContainerAllowed(type);
        if (AllowedSystemTypes.Contains(type))
            return true;
        if (
            typeof(Window).IsAssignableFrom(type)
            || typeof(Application).IsAssignableFrom(type)
            || typeof(System.Windows.Controls.Frame).IsAssignableFrom(type)
            || type == typeof(System.Windows.Media.VisualTreeHelper)
            || type == typeof(LogicalTreeHelper)
        )
            return false;
        string name = type.FullName ?? "";
        if (SensitiveName().IsMatch(name))
            return false;
        if (name.StartsWith("Voidstrap.", StringComparison.Ordinal) && VoidstrapLocationName().IsMatch(type.Name))
            return false;
        if (DeniedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            return false;
        return AllowedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static bool IsGenericContainerAllowed(Type type)
    {
        string name = type.GetGenericTypeDefinition().FullName ?? "";
        return name.StartsWith("System.Collections.Generic.", StringComparison.Ordinal)
            || name.StartsWith("System.Nullable", StringComparison.Ordinal)
            || name.StartsWith("System.Windows.", StringComparison.Ordinal);
    }

    public static bool IsMemberAllowed(MemberInfo member)
    {
        if (member.DeclaringType == null || !IsDeclaringAllowed(member.DeclaringType))
            return false;
        if (DeniedMembers.Contains(member.Name) || SensitiveName().IsMatch(member.Name))
            return false;
        if ((member.DeclaringType.FullName ?? "").StartsWith("Voidstrap.", StringComparison.Ordinal) && VoidstrapLocationName().IsMatch(member.Name))
            return false;
        Type? returned = member switch
        {
            PropertyInfo property => property.PropertyType,
            FieldInfo field => field.FieldType,
            MethodInfo method => method.ReturnType,
            _ => null,
        };
        return returned == null || IsTypeAllowed(returned) || returned.IsInterface && IsSafeInterface(returned);
    }

    private static bool IsDeclaringAllowed(Type type) => IsTypeAllowed(type) || type.IsInterface && IsSafeInterface(type);

    private static bool IsSafeInterface(Type type) => (type.Namespace ?? "").StartsWith("System.Collections", StringComparison.Ordinal);

    public static Type? ResolveType(string name)
    {
        if (name.Length is 0 or > 200 || name.Contains(',') || name.Contains('`'))
            return null;
        Type? type = SearchAssemblies.Select(assembly => assembly.GetType(name, false)).FirstOrDefault(found => found != null);
        return IsTypeAllowed(type) ? type : null;
    }

    [GeneratedRegex(
        @"password|passwd|roblosecurity|cookie|two.?factor|2fa|credit.?card|seed.?phrase|sign.?in|log.?in|\beval\b|new\s+Function|\bFunction\s*\(|atob|fromCharCode|\\x[0-9a-f]{2}|\\u[0-9a-f]{4}",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex RefusedSource();

    [GeneratedRegex("password|passwd|roblosecurity|cookie|two.?factor|2fa|credit.?card|seed.?phrase|sign.?in|log.?in", RegexOptions.IgnoreCase)]
    private static partial Regex PhishingText();

    public static bool LooksLikePhishing(string text) => PhishingText().IsMatch(text);

    public static string? RefusalReason(PluginManifest manifest)
    {
        bool native = manifest.HasPermission(PluginManifest.NativePermission);
        bool outbound = manifest.NetworkHosts.Any() || manifest.HasPermission(PluginManifest.WebPermission);
        if (native && outbound)
            return "native cannot be combined with web or network hosts, so what a mod reads cannot be sent out";
        if (manifest.HasPermission(PluginManifest.RobloxAccountPermission))
            return "native, files and web cannot be combined with roblox-account";
        string marker = Path.DirectorySeparatorChar + PluginManifest.ManifestFolder + Path.DirectorySeparatorChar + "types";
        foreach (string file in Directory.EnumerateFiles(manifest.Directory, "*", SearchOption.AllDirectories))
        {
            if (file.Contains(marker, StringComparison.OrdinalIgnoreCase))
                continue;
            if (Path.GetExtension(file).ToLowerInvariant() is ".exe" or ".dll" or ".bat" or ".cmd" or ".ps1" or ".sh" or ".msi" or ".scr" or ".com" or ".vbs" or ".so" or ".dylib")
                return Path.GetFileName(file) + " is a program, which powerful mods can never include";
            if (Path.GetExtension(file).ToLowerInvariant() is not (".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".json"))
                continue;
            Match match = RefusedSource().Match(File.ReadAllText(file));
            if (match.Success)
                return Path.GetFileName(file) + " contains \"" + match.Value + "\", which powerful mods cannot use";
        }
        return null;
    }

    public static string ContentHash(PluginManifest manifest)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        string marker = Path.DirectorySeparatorChar + PluginManifest.ManifestFolder + Path.DirectorySeparatorChar + "types";
        foreach (string file in Directory.EnumerateFiles(manifest.Directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (file.Contains(marker, StringComparison.OrdinalIgnoreCase))
                continue;
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(manifest.Directory, file)));
            hash.AppendData(File.ReadAllBytes(file));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static string GrantName(PluginManifest manifest) => "powerful:" + ContentHash(manifest);

    public static bool IsApproved(PluginManifest manifest)
    {
        try
        {
            return PluginConsent.HasGrant(manifest.Name, GrantName(manifest));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
