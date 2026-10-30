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
        "cookie|account|token|password|credential|secret|auth|login|process|shell|execute|registry|download|install|webview|webbrowser|clipboard|plugin|consent|rollback|mcp|assembly|reflection|gettype|loadcomponent|dispatcher|objectdataprovider|xmldataprovider|printdialog",
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
