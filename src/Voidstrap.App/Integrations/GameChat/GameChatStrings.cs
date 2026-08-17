namespace Voidstrap.Integrations.GameChat
{
    public static class GameChatStrings
    {
        public const string AboutText = "About Voidstrap Chat:\nA cross server chat overlay built into Voidstrap.\nChat with everyone in your Roblox server, whether they use Voidstrap or not you can invite them.\nPowered by the Voidstrap website API.\nSource: https://github.com/KloBraticc/Voidstrap";
        public const string AlreadyUpToDate = "Already up to date ({0})";
        public const string BannedFromChat = "You are banned from the Voidstrap chat.";
        public const string BugSending = "Sending your bug report to the Voidstrap team...";
        public const string BugSent = "Thanks! Your bug report was sent to the Voidstrap developers.";
        public const string BugFailed = "Could not send your bug report. Please try again later.";
        public const string BugCooldown = "Please wait a minute before sending another bug report.";
        public const string BugTooShort = "Please describe the bug in a bit more detail.";
        public const string ChatInputBoxText = "Press / key | Ctrl+Shift+C to hide";
        public const string CheckingForUpdates = "Checking for updates...";
        public const string ConnectedSuccessfully = "Connected successfully!";
        public const string ConnectingToServer = "Connecting to server {0}...";
        public const string ConnectionError = "Connection error: {0}";
        public const string ConnectionFailed = "Connection failed: {0}";
        public const string CouldNotOpenLink = "Could not open link: {0}";
        public const string CurrentChannelID = "Current Channel ID";
        public const string EchoResponse = "{0} (Only you can see this message.)";
        public const string EmoteError = "Emote error: {0}";
        public const string EmoteNoGame = "You must be in a game to perform an emote.";
        public const string EmoteQueued = "Emote '{0}' sent. It will play if this game has Voidstrap emote integration.";
        public const string FailedToQueueEmote = "Failed to queue emote.";
        public const string UnknownError = "Unknown error.";
        public const string VerifyChecking = "Verifying with your Voidstrap account...";
        public const string VerifySuccess = "Verified as {0}! You can now use /emote.";
        public const string VerifyNotSignedIn = "Sign in to your Voidstrap account first, then chat '/verify' again.";
        public const string VerifyFailed = "Verification failed: {0}";
        public const string VerifyChallengeFailed = "Could not reach the verification server. Please try again.";
        public const string Unverified = "Your account has been unverified.";
        public const string FailedToSendMessage = "Failed to send message: {0}";
        public const string FilterPreferenceCurrent = "Current message filter: {0}";
        public const string FilterPreferenceSet = "Message filter set to: {0}";
        public const string MessageRejectedApiError = "Your message could not be processed due to a server error. Please try again.";
        public const string MessageRejectedModeration = "Your message was not sent as it violates community guidelines.";
        public const string MessageRejectedQueueFull = "Your message was rejected because the server queue is full. Please try again shortly.";
        public const string MessageRejectedUnknown = "Your message was not sent due to unknown reasons.";
        public const string MessageHiddenDueToFilterSettings = "[Message hidden due to your filter settings.]";
        public const string MustBeVerifiedEmote = "You must be verified to use /emote.";
        public const string MutedSpeaker = "Speaker '{0}' has been muted.";
        public const string ResetToDefault = "Chat window reset to its default position and size.";
        public const string RequestTimedOut = "Request timed out.";
        public const string SpeakerNotMuted = "Speaker '{0}' was not muted.";
        public const string UsageBug = "Usage: /bug <describe the problem>";
        public const string StartupText = "Welcome to Voidstrap Chat.\nChat '/update' to check for updates.\nChat '/?' or '/help' for a list of chat commands.";
        public const string System = "System";
        public const string UnknownCommand = "Unknown command '{0}'. Use '/?' or '/help' for a list of commands.";
        public const string UnmutedSpeaker = "Speaker '{0}' has been unmuted.";
        public const string UpdateCheckFailed = "Update check failed: {0}";
        public const string UpdateAvailable = "A new Voidstrap update is available ({0}). Opening the download page...";
        public const string UsageEmote = "Usage: /emote <name>";
        public const string UsageFilter = "Usage: /filter <strict|default|relaxed>";
        public const string UsageMute = "Usage: /mute <speaker>";
        public const string UsageUnmute = "Usage: /unmute <speaker>";
        public const string UsageWhisper = "Usage: /w \"<speaker 12345>\" message or /w <speaker> message";
        public const string WhisperFrom = "From {0}";
        public const string WhisperTo = "To {0}";
        public const string NotConnected = "Not connected. Use '/rc' or '/reconnect' to connect to server.";
        public const string ReceiveError = "Receive error: {0}";
        public const string SendError = "Send error: {0}";
        public const string SendTimedOut = "Send timed out.";
        public const string AttemptingLogin = "Attempting to log in...";
        public const string LoginSuccess = "Welcome back! Login successful.";
        public const string AlreadyLoggedIn = "You are already logged in.";
        public const string LoginBrowserOpened = "Opening the Voidstrap sign in page in your browser. Finish signing in there.";
        public const string LoginTimedOut = "Sign in timed out or was cancelled.";
        public const string LoginBrowserFailed = "Could not open the sign in page in your browser. Please try again.";
        public const string LoggedOut = "You have been logged out.";
        public const string LoginFailed = "Login failed due to a server error.";
        public const string UserNotFoundInChannel = "User {0} not found in this channel.";
        public const string DebugConsoleTitle = "Voidstrap Chat Debugger";
        public const string DebugConsoleInitialized = "DEBUG CONSOLE INITIALIZED AT {0}";
        public const string DebugConsoleUseClose = "Use '/console' or '/debug' again to close";
        public const string HelpHeader = "Voidstrap Chat commands";
        public const string ViewProfileTooltip = "Click or right click to view {0}'s profile";
        public const string FriendRequestSent = "Friend request sent.";
        public const string FriendRequestFailed = "Could not send friend request.";
        public const string CopiedUserId = "Copied user id {0}.";
        public const string CtxCopyMessage = "Copy Message";
        public const string CtxCopyUserId = "Copy User ID";
        public const string CtxCopyUsername = "Copy Username";
        public const string CtxViewProfile = "View Profile";
        public const string CtxMuteUser = "Mute User";
        public const string CtxUnmuteUser = "Unmute User";
        public const string CopiedMessage = "Copied message.";

        public static readonly (string Token, string Description)[] CommandTokens =
        [
            ("/help", "show the list of commands"),
            ("/about", "about Voidstrap Chat"),
            ("/reconnect", "reconnect to the chat server"),
            ("/clear", "clear the chat box"),
            ("/id", "show the current channel id"),
            ("/w", "whisper privately to a speaker"),
            ("/mute", "hide messages from a speaker"),
            ("/unmute", "show a muted speaker again"),
            ("/filter", "set the local message filter"),
            ("/echo", "echo a message back to only you"),
            ("/verify", "link your Roblox account"),
            ("/unverify", "unlink your Roblox account"),
            ("/emote", "perform an emote in supported games"),
            ("/update", "check for Voidstrap updates"),
            ("/login", "sign in with your Voidstrap account"),
            ("/logout", "sign out of your Voidstrap account"),
            ("/console", "open or close the debug console"),
            ("/bug", "send a bug report to the developers"),
        ];

        public static readonly (string Command, string Description)[] HelpEntries =
        [
            ("/help", "show this list of commands"),
            ("/about", "about Voidstrap Chat"),
            ("/reconnect", "reconnect to the chat server"),
            ("/clear", "clear the chat box"),
            ("/id", "show the current channel id"),
            ("/w <speaker> <message>", "whisper privately to a speaker"),
            ("/mute <speaker>", "hide messages from a speaker"),
            ("/unmute <speaker>", "show a muted speaker again"),
            ("/filter <strict|default|relaxed>", "set the local message filter"),
            ("/echo <text>", "echo a message back to only you"),
            ("/verify", "link your Roblox account using your Roblox login on this PC"),
            ("/unverify", "unlink your Roblox account"),
            ("/emote <name>", "perform an emote in supported games"),
            ("/update", "check for Voidstrap updates"),
            ("/login", "sign in with your Voidstrap account"),
            ("/logout", "sign out of your Voidstrap account"),
            ("/console", "open or close the debug console"),
            ("/bug <description>", "send a bug report to the developers"),
        ];
    }
}
