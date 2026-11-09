using System.Security.Cryptography;
using System.Web;
using DiscordRPC;
using DiscordRPC.Message;
using System.Windows;
using Microsoft.Win32;
using Voidstrap.UI;

namespace Voidstrap.Integrations;

internal static class DiscordInvites
{
    internal const string JoinArgument = "-discordjoin";

    private const int MaxSecretLength = 128;

    private static readonly TimeSpan JoinWaitTimeout = TimeSpan.FromSeconds(15);

    internal static bool Enabled =>
        App.Settings.Prop.UseDiscordRichPresence && App.Settings.Prop.AllowDiscordInvites;

    internal static string? BuildSecret(ActivityData? data)
    {
        if (data == null || data.PlaceId <= 0)
            return null;
        bool shareable =
            (data.ServerType == ServerType.Public && Guid.TryParse(data.JobId, out _))
            || (data.ServerType == ServerType.Private && Guid.TryParse(data.AccessCode, out _));
        if (!shareable)
            return null;
        string secret = data.GetNativeJoinUri(launchData: false);
        return secret.Length <= MaxSecretLength && TryParseSecret(secret, out _) ? secret : null;
    }

    internal static void Apply(DiscordRPC.RichPresence presence, ActivityData? data, UniverseDetails? universe, int playing = 0, int serverMax = 0)
    {
        string? secret = Enabled ? BuildSecret(data) : null;
        if (secret == null || data == null)
        {
            presence.Party = null;
            presence.Secrets = null;
            return;
        }
        int max = Math.Max(serverMax > 0 ? serverMax : universe?.Data?.MaxPlayers ?? 0, 2);
        presence.Party = new Party
        {
            ID = PartyId(data),
            Size = Math.Clamp(playing, 1, max),
            Max = max,
            Privacy = Party.PrivacySetting.Private,
        };
        presence.Secrets = new Secrets { JoinSecret = secret };
        presence.Buttons = null;
    }

    private static string PartyId(ActivityData data)
    {
        string source = data.ServerType == ServerType.Private ? data.AccessCode : data.JobId;
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(data.PlaceId + ":" + source));
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    internal static bool TryParseSecret(string? secret, out string deeplink)
    {
        deeplink = string.Empty;
        if (
            string.IsNullOrWhiteSpace(secret)
            || secret.Length > MaxSecretLength
            || !Uri.TryCreate(secret, UriKind.Absolute, out Uri? uri)
            || !uri.Scheme.Equals("roblox", StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Equals("experiences", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.Equals("/start", StringComparison.OrdinalIgnoreCase)
        )
            return false;
        var query = HttpUtility.ParseQueryString(uri.Query);
        if (query.Count != 2 || !long.TryParse(query["placeId"], NumberStyles.None, CultureInfo.InvariantCulture, out long placeId) || placeId <= 0)
            return false;
        string? instance = query["gameInstanceId"];
        string? access = query["accessCode"];
        if (instance != null && Guid.TryParse(instance, out Guid job))
            deeplink = $"roblox://experiences/start?placeId={placeId}&gameInstanceId={job:D}";
        else if (access != null && Guid.TryParse(access, out Guid code))
            deeplink = $"roblox://experiences/start?placeId={placeId}&accessCode={code:D}";
        else
            return false;
        return true;
    }

    internal static void Register(DiscordRpcClient client)
    {
        const string LOG_IDENT = "DiscordInvites::Register";

        if (!Enabled || string.IsNullOrEmpty(Paths.Application))
            return;
        try
        {
            client.RegisterUriScheme(null, Paths.Application);
            WriteLaunchCommand();
            client.Subscribe(EventType.JoinRequest);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "Could not register Discord invites: " + ex.Message);
        }
    }

    private static void WriteLaunchCommand()
    {
        string scheme = "discord-" + App.DiscordApplicationId;
        string command = "\"" + Paths.Application + "\" " + JoinArgument;
        if (OperatingSystem.IsWindows())
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + scheme);
            key.SetValue("", "URL:Run Voidstrap protocol");
            key.SetValue("URL Protocol", "");
            using RegistryKey icon = key.CreateSubKey("DefaultIcon");
            icon.SetValue("", Paths.Application);
            using RegistryKey open = key.CreateSubKey(@"shell\open\command");
            open.SetValue("", command);
        }
        else if (OperatingSystem.IsLinux())
        {
            string folder = Path.Combine(
                Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg
                    ? xdg
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"),
                "applications"
            );
            Directory.CreateDirectory(folder);
            string file = scheme + ".desktop";
            File.WriteAllText(
                Path.Combine(folder, file),
                "[Desktop Entry]\nName=Voidstrap\nExec=" + command + " %u\nType=Application\nNoDisplay=true\nCategories=Discord;Games;\nMimeType=x-scheme-handler/" + scheme + ";\n"
            );
            ProcessStartInfo info = new("xdg-mime") { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("default");
            info.ArgumentList.Add(file);
            info.ArgumentList.Add("x-scheme-handler/" + scheme);
            using Process? process = Process.Start(info);
        }
    }

    internal static void HandleJoinRequest(DiscordRpcClient client, JoinRequestMessage request)
    {
        _ = Task.Run(() =>
        {
            string name = request.User?.DisplayName is { Length: > 0 } display ? display : request.User?.Username ?? "?";
            MessageBoxResult answer = Frontend.ShowMessageBox(
                string.Format(Strings.Discord_Invites_JoinRequest, name),
                MessageBoxImage.Question,
                MessageBoxButton.YesNo,
                MessageBoxResult.No
            );
            try
            {
                client.Respond(request, answer == MessageBoxResult.Yes);
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("DiscordInvites::HandleJoinRequest", "Could not answer join request: " + ex.Message);
            }
        });
    }

    internal static async Task<string?> WaitForJoinAsync()
    {
        const string LOG_IDENT = "DiscordInvites::WaitForJoinAsync";

        if (!DiscordIpc.TryFindPipe(out int pipe))
        {
            App.Logger.WriteLine(LOG_IDENT, "Discord is not running");
            return null;
        }
        TaskCompletionSource<string?> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        DiscordRpcClient client = DiscordIpc.CreateClient(App.DiscordApplicationId, pipe);
        void OnJoin(object sender, JoinMessage message) =>
            result.TrySetResult(TryParseSecret(message.Secret, out string link) ? link : null);
        client.OnJoin += OnJoin;
        try
        {
            client.RegisterUriScheme(null, Paths.Application);
            client.Subscribe(EventType.Join);
            client.Initialize();
            Task finished = await Task.WhenAny(result.Task, Task.Delay(JoinWaitTimeout)).ConfigureAwait(false);
            string? link = finished == result.Task ? await result.Task.ConfigureAwait(false) : null;
            App.Logger.WriteLine(LOG_IDENT, link == null ? "No valid join secret arrived" : "Received join secret");
            return link;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "Join failed: " + ex.Message);
            return null;
        }
        finally
        {
            client.OnJoin -= OnJoin;
            DiscordIpc.Close(client);
        }
    }
}
