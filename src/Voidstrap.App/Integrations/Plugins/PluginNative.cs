using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
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
        "cookie|account|token|password|credential|secret|auth|login|process|shell|execute|registry|download|install|webview|webbrowser|clipboard|plugin|consent|rollback|mcp|assembly|reflection|gettype|loadcomponent|dispatcher|objectdataprovider|xmldataprovider|printdialog|presentationsource|hwndsource|automation|localvalue|converter|bitmapdecoder|bitmapframe|bitmapimage|mediaplayer|mediaelement|textrange|voidstrapmods|messagebox|textbox|richtext|autosuggest|textinput|textcomposition",
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

    private static readonly string[] CollectingHosts =
    [
        "discord.com",
        "discordapp.com",
        "webhook.site",
        "requestbin.com",
        "pipedream.net",
        "ngrok.io",
        "ngrok.app",
        "pastebin.com",
        "hastebin.com",
        "telegram.org",
        "ipify.org",
        "grabify.link",
        "iplogger.org",
    ];

    public static bool IsCollectingHost(string host) =>
        CollectingHosts.Any(blocked => host.Equals(blocked, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + blocked, StringComparison.OrdinalIgnoreCase));

    public static void CheckRequest(Uri uri, string method, string? body, JsonObject? headers)
    {
        if (IsCollectingHost(uri.IdnHost))
            throw new InvalidOperationException(uri.IdnHost + " is blocked for mods because it is used to collect data");
        if (ModLibraryReview.AllowedHosts.Contains(uri.IdnHost))
            return;
        if (method != "GET" || body != null || headers is { Count: > 0 } || uri.Query.Length > 0 || uri.AbsolutePath.Length > 256)
            throw new InvalidOperationException(uri.IdnHost + " is read only for mods: plain GET requests with no query, body or custom headers");
    }

    public static bool IsPowerful(PluginManifest manifest) =>
        manifest.HasPermission(PluginManifest.NativePermission)
        || manifest.HasPermission(PluginManifest.FilesPermission)
        || manifest.HasPermission(PluginManifest.WebPermission)
        || manifest.HasPermission(PluginManifest.ClipboardPermission);

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
        @"password|passwd|roblosecurity|cookie|two.?factor|\b2fa\b|credit.?card|seed.?phrase|sign.?in|log[ _-]?in|\beval\b|new\s+Function|\bFunction\s*\(|\batob\b|fromCharCode|\\x[0-9a-f]{2}|\\u[0-9a-f]{4}",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex RefusedSource();

    [GeneratedRegex(@"password|passwd|roblosecurity|cookie|two.?factor|\b2fa\b|credit.?card|seed.?phrase|sign.?in|log[ _-]?in", RegexOptions.IgnoreCase)]
    private static partial Regex PhishingText();

    public static bool LooksLikePhishing(string text) => PhishingText().IsMatch(text);

    public static string? RefusalReason(PluginManifest manifest)
    {
        bool native = manifest.HasPermission(PluginManifest.NativePermission);
        bool outbound = manifest.NetworkHosts.Any() || manifest.HasPermission(PluginManifest.WebPermission);
        if (native && outbound)
            return "native cannot be combined with web or network hosts, so what a mod reads cannot be sent out";
        if (manifest.HasPermission(PluginManifest.ClipboardPermission) && outbound)
            return "clipboard cannot be combined with web or network hosts, so what a mod reads cannot be sent out";
        if (manifest.HasPermission(PluginManifest.RobloxAccountPermission))
            return "native, files and web cannot be combined with roblox-account";
        foreach (string file in Directory.EnumerateFiles(manifest.Directory, "*", SearchOption.AllDirectories))
        {
            if (IsGeneratedType(file))
                continue;
            if (Path.GetExtension(file).ToLowerInvariant() is ".exe" or ".dll" or ".bat" or ".cmd" or ".ps1" or ".sh" or ".msi" or ".scr" or ".com" or ".vbs" or ".so" or ".dylib")
                return Path.GetFileName(file) + " is a program, which powerful mods can never include";
            if (Path.GetFileName(file).Equals("owner.json", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(file).ToLowerInvariant() is not (".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".json"))
                continue;
            Match match = RefusedSource().Match(File.ReadAllText(file));
            if (match.Success)
                return Path.GetFileName(file) + " contains \"" + match.Value + "\", which powerful mods cannot use";
        }
        return null;
    }

    private static bool IsGeneratedType(string file)
    {
        string separator = Path.DirectorySeparatorChar.ToString();
        string marker = separator + PluginManifest.ManifestFolder + separator + "types" + separator;
        if (ModTypes.IsGeneratedRootConfig(file))
            return true;
        return file.Contains(marker, StringComparison.OrdinalIgnoreCase)
            && (
                file.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(file).Equals("tsconfig.json", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(file).Equals("plugin.schema.json", StringComparison.OrdinalIgnoreCase)
            );
    }

    public static string ContentHash(PluginManifest manifest)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string file in Directory.EnumerateFiles(manifest.Directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (IsGeneratedType(file))
                continue;
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(manifest.Directory, file)));
            hash.AppendData(HashableContent(file));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static byte[] HashableContent(string file)
    {
        byte[] content = File.ReadAllBytes(file);
        if (!Path.GetFileName(file).Equals("plugin.json", StringComparison.OrdinalIgnoreCase))
            return content;
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(content) is not System.Text.Json.Nodes.JsonObject manifest)
                return content;
            manifest.Remove("library");
            return Encoding.UTF8.GetBytes(manifest.ToJsonString());
        }
        catch (System.Text.Json.JsonException)
        {
            return content;
        }
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
