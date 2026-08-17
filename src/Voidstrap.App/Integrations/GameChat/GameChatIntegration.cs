using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Voidstrap.Integrations.GameChat
{
    public static class GameChatLog
    {
        private const int Capacity = 2000;
        private static readonly object Sync = new();
        private static readonly Queue<Models.Entities.ActivityData.UserMessage> Entries = new();

        public static event EventHandler<Models.Entities.ActivityData.UserMessage>? Added;

        public static IReadOnlyList<Models.Entities.ActivityData.UserMessage> Snapshot()
        {
            lock (Sync)
                return Entries.ToArray();
        }

        public static void Add(string sender, string channel, string message)
        {
            var entry = new Models.Entities.ActivityData.UserMessage
            {
                Sender = string.IsNullOrWhiteSpace(sender) ? "System" : sender,
                Channel = string.IsNullOrWhiteSpace(channel) ? "Game" : channel,
                Message = message,
                Time = DateTime.Now
            };
            lock (Sync)
            {
                while (Entries.Count >= Capacity)
                    Entries.Dequeue();
                Entries.Enqueue(entry);
            }
            EventHandler<Models.Entities.ActivityData.UserMessage>? handlers = Added;
            if (handlers == null)
                return;
            foreach (EventHandler<Models.Entities.ActivityData.UserMessage> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(null, entry);
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException("GameChatLog::Added", ex);
                }
            }
        }
    }

    public class GameChatIntegration : IDisposable
    {
        private const string Tag = "GameChatIntegration";

        private readonly ActivityWatcher _activityWatcher;
        private readonly uint _robloxPid;
        private GameChatOverlay? _overlay;
        private GameChatKeyboardHook? _hook;
        private readonly CancellationTokenSource _lifetimeCts = new CancellationTokenSource();
        private readonly object _overlayGate = new object();
        private Thread? _hookThread;
        private Dispatcher? _hookDispatcher;
        private Dispatcher? _overlayDispatcher;
        private Task? _monitorTask;
        private int _accountRefreshPending;
        private int _hangHandled;
        private volatile bool _disposed;

        private const int SW_HIDE = 0;

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr window, int command);

        public GameChatIntegration(ActivityWatcher activityWatcher, int robloxPid)
        {
            _activityWatcher = activityWatcher;
            _robloxPid = (uint)robloxPid;
            _activityWatcher.OnGameJoin += OnGameJoin;
            _activityWatcher.OnGameLeave += OnGameLeave;
            Utility.WebsiteAuth.Changed += OnAccountChanged;

            StartOverlay();
            _monitorTask = Task.Run(() => MonitorOverlayAsync(_lifetimeCts.Token));
        }

        private void StartOverlay()
        {
            if (_disposed)
                return;
            Application? application = Application.Current;
            if (application == null)
            {
                App.Logger.WriteLine(Tag, "No application dispatcher is available, game chat will not start");
                return;
            }
            Dispatcher dispatcher = application.Dispatcher;
            if (dispatcher.CheckAccess())
                CreateOverlay();
            else
                dispatcher.BeginInvoke(new Action(CreateOverlay));
        }

        private void CreateOverlay()
        {
            if (_disposed)
                return;
            GameChatOverlay overlay;
            try
            {
                overlay = new GameChatOverlay(_activityWatcher);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(Tag + "::CreateOverlay", ex);
                return;
            }
            lock (_overlayGate)
            {
                if (_disposed)
                {
                    try { overlay.Close(); } catch { }
                    return;
                }
                _overlay = overlay;
                _overlayDispatcher = overlay.Dispatcher;
            }
            overlay.Closed += OnOverlayClosed;
            if (_activityWatcher.InGame)
                overlay.EnterGame(_activityWatcher.Data?.JobId ?? "");
            StartHookThread(overlay);
            App.Logger.WriteLine(Tag, "Game chat overlay created");
        }

        private void StartHookThread(GameChatOverlay overlay)
        {
            var thread = new Thread(() => HookThreadMain(overlay))
            {
                IsBackground = true,
                Name = "Voidstrap Game Chat Input"
            };
            thread.SetApartmentState(ApartmentState.STA);
            lock (_overlayGate)
                _hookThread = thread;
            thread.Start();
        }

        private void HookThreadMain(GameChatOverlay overlay)
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            GameChatKeyboardHook? hook = null;
            try
            {
                dispatcher.UnhandledException += OnHookDispatcherException;
                lock (_overlayGate)
                {
                    if (_disposed || !ReferenceEquals(_overlay, overlay))
                        return;
                    _hookDispatcher = dispatcher;
                }
                hook = new GameChatKeyboardHook(overlay, _robloxPid);
                hook.SetEnabled(_activityWatcher.InGame);
                lock (_overlayGate)
                {
                    if (_disposed)
                        return;
                    _hook = hook;
                }
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(Tag + "::HookThread", ex);
            }
            finally
            {
                dispatcher.UnhandledException -= OnHookDispatcherException;
                try
                {
                    hook?.Dispose();
                }
                catch
                {
                }
                lock (_overlayGate)
                {
                    if (ReferenceEquals(_hook, hook))
                        _hook = null;
                    if (ReferenceEquals(_hookDispatcher, dispatcher))
                        _hookDispatcher = null;
                    if (ReferenceEquals(_hookThread, Thread.CurrentThread))
                        _hookThread = null;
                }
            }
        }

        private static void OnHookDispatcherException(object? sender, DispatcherUnhandledExceptionEventArgs e)
        {
            e.Handled = true;
            App.Logger.WriteException(Tag + "::HookDispatcher", e.Exception);
        }

        private async Task MonitorOverlayAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(2000, token).ConfigureAwait(false);
                    GameChatOverlay? overlay;
                    lock (_overlayGate)
                        overlay = _overlay;
                    if (overlay == null)
                        continue;
                    IntPtr handle = overlay.WindowHandle;
                    long heartbeat = overlay.LastHeartbeatMs;
                    if (handle == IntPtr.Zero || !IsWindowVisible(handle) || heartbeat == 0 || Environment.TickCount64 - heartbeat < 6000)
                        continue;
                    if (Interlocked.Exchange(ref _hangHandled, 1) != 0)
                        continue;
                    HideOverlayWindows(handle);
                    App.Logger.WriteLine(Tag, "Game chat was hidden because the overlay stopped responding");
                    if (App.Settings.Prop.GameChatEnabled)
                    {
                        App.Settings.Prop.GameChatEnabled = false;
                        App.Settings.SaveDeferred();
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("GameChatIntegration::Monitor", ex);
            }
        }

        private static void HideOverlayWindows(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
                return;
            ShowWindowAsync(handle, SW_HIDE);
        }

        private void OnGameJoin(object? sender, EventArgs e)
        {
            SetHookEnabled(true);
            GameChatOverlay? overlay;
            lock (_overlayGate)
                overlay = _overlay;
            overlay?.EnterGame(_activityWatcher.Data?.JobId ?? "");
        }

        private void OnGameLeave(object? sender, EventArgs e)
        {
            if (!_activityWatcher.IsTeleporting)
                SetHookEnabled(false);
            else
                ResetHookChatMode();
            GameChatOverlay? overlay;
            lock (_overlayGate)
                overlay = _overlay;
            overlay?.LeaveGame();
        }

        private void ResetHookChatMode()
        {
            GameChatKeyboardHook? hook;
            Dispatcher? dispatcher;
            lock (_overlayGate)
            {
                hook = _hook;
                dispatcher = _hookDispatcher;
            }
            if (_disposed || hook == null || dispatcher == null)
                return;
            if (dispatcher.CheckAccess())
            {
                hook.ResetChatMode();
                return;
            }
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
                return;
            try
            {
                dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!_disposed && ReferenceEquals(_hook, hook))
                        hook.ResetChatMode();
                }));
            }
            catch (InvalidOperationException)
            {
            }
        }

		private void SetHookEnabled(bool enabled)
		{
			GameChatKeyboardHook? hook;
			Dispatcher? dispatcher;
			lock (_overlayGate)
			{
				hook = _hook;
				dispatcher = _hookDispatcher;
			}
			if (_disposed || hook == null || dispatcher == null)
				return;
			if (dispatcher.CheckAccess())
			{
				hook.SetEnabled(enabled);
				return;
			}
			if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
				return;
			try
			{
				dispatcher.BeginInvoke(new Action(() =>
				{
					if (!_disposed && ReferenceEquals(_hook, hook))
						hook.SetEnabled(enabled);
				}));
			}
			catch (InvalidOperationException)
			{
			}
		}

		private void OnOverlayClosed(object? sender, EventArgs e)
		{
			if (sender is not GameChatOverlay overlay)
				return;
			overlay.Closed -= OnOverlayClosed;
			Dispatcher? hookDispatcher;
			lock (_overlayGate)
			{
				if (!ReferenceEquals(_overlay, overlay))
					return;
				hookDispatcher = _hookDispatcher;
				_overlay = null;
				_overlayDispatcher = null;
			}
			ShutdownHookDispatcher(hookDispatcher);
		}

		private static void ShutdownHookDispatcher(Dispatcher? dispatcher)
		{
			if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
				return;
			try
			{
				dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
			}
			catch (InvalidOperationException)
			{
			}
		}

        private void OnAccountChanged()
        {
            GameChatRoblox.InvalidateAccountState();
            Dispatcher? dispatcher;
            lock (_overlayGate)
                dispatcher = _overlayDispatcher;
            if (_disposed || dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
                return;
            if (Interlocked.Exchange(ref _accountRefreshPending, 1) != 0)
                return;
            try
            {
                dispatcher.BeginInvoke(new Action(RestartForAccount));
            }
            catch (InvalidOperationException)
            {
                Interlocked.Exchange(ref _accountRefreshPending, 0);
            }
        }

        private async void RestartForAccount()
        {
            try
            {
                while (!_disposed && Interlocked.Exchange(ref _accountRefreshPending, 0) != 0)
                {
					GameChatOverlay? overlay;
					lock (_overlayGate)
						overlay = _overlay;
                    if (overlay != null)
                        await overlay.RefreshAccountAsync();
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("GameChatIntegration::AccountChanged", ex);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Interlocked.Exchange(ref _accountRefreshPending, 0);

            _activityWatcher.OnGameJoin -= OnGameJoin;
            _activityWatcher.OnGameLeave -= OnGameLeave;
            Utility.WebsiteAuth.Changed -= OnAccountChanged;

            GameChatOverlay? overlay;
            Dispatcher? dispatcher;
            Dispatcher? hookDispatcher;
            lock (_overlayGate)
            {
				overlay = _overlay;
				dispatcher = _overlayDispatcher;
				hookDispatcher = _hookDispatcher;
            }

            if (overlay != null)
                HideOverlayWindows(overlay.WindowHandle);
            _lifetimeCts.Cancel();

			if (overlay != null && dispatcher != null && !dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
			{
				try
				{
					dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(overlay.Close));
				}
				catch (InvalidOperationException)
				{
				}
			}

			ShutdownHookDispatcher(hookDispatcher);

            _lifetimeCts.Dispose();
			_monitorTask = null;
            GC.SuppressFinalize(this);
        }
    }
}
