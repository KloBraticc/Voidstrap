using System;
using System.Threading;

namespace Voidstrap.Integrations.Overlays
{
    public static class OverlayHub
    {
        private static Thread? _thread;
        private static CancellationTokenSource? _cts;
        private static readonly object _lock = new object();
		private static volatile bool _shutdown;
		private static volatile bool _gameTransition;
		private static volatile bool _inGame;

		private static volatile bool _compositorLive;

		public static bool InGame => _inGame;

		public static bool CompositorCrosshairActive => _compositorLive && OverlayCrosshair.IsEnabled();

		internal static void SetCompositorLive(bool live)
		{
			if (_compositorLive == live)
				return;
			_compositorLive = live;
			if (live)
				CloseStandaloneCrosshair();
			else
				RestoreStandaloneCrosshair();
		}

		private static void RestoreStandaloneCrosshair()
		{
			try
			{
				System.Windows.Application? app = System.Windows.Application.Current;
				if (app == null || !OverlayCrosshair.IsEnabled())
					return;
				app.Dispatcher.BeginInvoke(new Action(() =>
				{
					try
					{
						if (!OverlayCrosshair.IsEnabled() || _compositorLive)
							return;
						if (app.Resources["CrosshairWindow"] is Voidstrap.UI.Elements.Crosshair.CrosshairWindow)
							return;
						app.Resources["CrosshairWindow"] = new Voidstrap.UI.Elements.Crosshair.CrosshairWindow(new Voidstrap.UI.ViewModels.Settings.ModsViewModel());
						App.Logger.WriteLine("Overlays", "Compositor stopped drawing the crosshair, the standalone crosshair is back");
					}
					catch (Exception ex)
					{
						App.Logger.WriteException("OverlayHub::RestoreStandaloneCrosshair", ex);
					}
				}));
			}
			catch (Exception ex)
			{
				App.Logger.WriteException("OverlayHub::RestoreStandaloneCrosshair", ex);
			}
		}

		private static void CloseStandaloneCrosshair()
		{
			try
			{
				System.Windows.Application? app = System.Windows.Application.Current;
				if (app == null)
					return;
				app.Dispatcher.BeginInvoke(new Action(() =>
				{
					try
					{
						if (app.Resources["CrosshairWindow"] is System.Windows.Window window)
						{
							app.Resources.Remove("CrosshairWindow");
							window.Close();
						}
					}
					catch (Exception ex)
					{
						App.Logger.WriteException("OverlayHub::CloseStandaloneCrosshair", ex);
					}
				}));
			}
			catch (Exception ex)
			{
				App.Logger.WriteException("OverlayHub::CloseStandaloneCrosshair", ex);
			}
		}

		public static bool HomepageBackgroundActive => !_inGame && !_gameTransition && OverlaySettings.HomepageBackgroundEnabled;

        public static bool Refresh()
        {
            if (OverlaySettings.AnyEnabled && !_gameTransition)
				return Start();
            Stop();
			return true;
        }

        public static void Restart()
        {
            Stop();
            Refresh();
        }

        public static void OnGameJoin()
        {
			_inGame = true;
			_gameTransition = false;
			Refresh();
        }

        public static void OnGameLeave()
        {
			_inGame = false;
			_gameTransition = false;
			Refresh();
		}

		public static void OnGameTransitionStarted()
		{
			_inGame = true;
			_gameTransition = true;
			Stop();
		}

		public static void OnGameTransitionCompleted()
		{
			_inGame = true;
			_gameTransition = false;
			Refresh();
        }

        public static void Shutdown()
        {
			_shutdown = true;
            Stop();
            RobloxFpsCap.Shutdown();
        }

        private static bool Start()
        {
			if (_shutdown || _gameTransition)
				return false;
            lock (_lock)
            {
                if (_thread != null)
                    return true;
                var cts = new CancellationTokenSource();
				Thread thread = new Thread(() => Supervise(cts))
                {
                    IsBackground = true,
                    Name = "Overlays",
					Priority = ThreadPriority.BelowNormal,
                };
				_cts = cts;
				_thread = thread;
				try
				{
					thread.Start();
					return true;
				}
				catch (Exception ex)
				{
					_thread = null;
					_cts = null;
					cts.Dispose();
					App.Logger.WriteException("OverlayHub::Start", ex);
					return false;
				}
            }
        }

        private static void Stop()
        {
            CancellationTokenSource? cts;
            lock (_lock)
            {
                cts = _cts;
            }
			if (cts == null)
                return;
            try
            {
                cts.Cancel();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("OverlayHub::Stop", ex);
            }
        }

        private static void Supervise(CancellationTokenSource owner)
        {
            CancellationToken token = owner.Token;
            Mutex? mutex = null;
            bool held = false;
            try
            {
                mutex = new Mutex(false, "VoidstrapOverlayCompositorActive");
                bool loggedWaitOwner = false;
                bool loggedWaitRoblox = false;
                int fastFailures = 0;
                while (!token.IsCancellationRequested)
                {
                    if (!OverlaySettings.AnyEnabled)
                        break;
                    if (!held)
                    {
                        try
                        {
                            held = mutex.WaitOne(0);
                        }
                        catch (AbandonedMutexException)
                        {
                            held = true;
                        }
                        if (!held)
                        {
                            if (!loggedWaitOwner)
                            {
                                loggedWaitOwner = true;
                                App.Logger.WriteLine("Overlays", "Another Voidstrap process is running the compositor, standing by");
                            }
                            if (token.WaitHandle.WaitOne(2000))
                                break;
                            continue;
                        }
                        App.Logger.WriteLine("Overlays", "This process now owns the compositor");
                    }

                    if (!RobloxLightingOverlay.RobloxWindow.TryGet(out _))
                    {
                        if (!loggedWaitRoblox)
                        {
                            loggedWaitRoblox = true;
                            App.Logger.WriteLine("Overlays", "Waiting for the Roblox window");
                        }
                        if (token.WaitHandle.WaitOne(1000))
                            break;
                        continue;
                    }
                    loggedWaitRoblox = false;

                    IntPtr sessionHwnd = RobloxLightingOverlay.RobloxWindow.GetHandle();
                    long sessionStartedMs = Environment.TickCount64;
                    using (var runCts = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        var runToken = runCts.Token;
                        var watcher = new Thread(() => WatchRoblox(runCts))
                        {
                            IsBackground = true,
                            Name = "OverlaysWatch",
							Priority = ThreadPriority.BelowNormal,
                        };
                        watcher.Start();
                        try
                        {
                            var compositor = new OverlayCompositor();
                            compositor.Run(runToken);
                        }
                        finally
                        {
                            runCts.Cancel();
                            watcher.Join(1500);
                        }
                    }

                    if (!OverlaySettings.AnyEnabled)
                        break;
                    IntPtr currentHwnd = RobloxLightingOverlay.RobloxWindow.GetHandle();
                    if (Environment.TickCount64 - sessionStartedMs < 3000 && sessionHwnd != IntPtr.Zero && currentHwnd == sessionHwnd)
                        fastFailures++;
                    else
                        fastFailures = 0;
                    if (fastFailures == 3)
                        App.Logger.WriteLine("Overlays", "Compositor keeps ending quickly, retrying every 10 seconds");
                    if (!token.IsCancellationRequested)
                        App.Logger.WriteLine("Overlays", "Compositor session ended, waiting for Roblox again");
                    if (token.WaitHandle.WaitOne(fastFailures >= 3 ? 10000 : 1000))
                        break;
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("OverlayHub::Supervise", ex);
            }
            finally
            {
                if (held && mutex != null)
                {
                    try
                    {
                        mutex.ReleaseMutex();
                    }
                    catch
                    {
                    }
                }
                mutex?.Dispose();
                CompleteThread(owner);
            }
        }

        private static void CompleteThread(CancellationTokenSource owner)
        {
            bool dispose = false;
			bool restart = false;
            lock (_lock)
            {
                if (ReferenceEquals(_thread, Thread.CurrentThread) && ReferenceEquals(_cts, owner))
                {
                    _thread = null;
                    _cts = null;
                    dispose = true;
					restart = !_shutdown && !_gameTransition && OverlaySettings.AnyEnabled;
                }
            }
            if (dispose)
                owner.Dispose();
			if (restart)
				ThreadPool.QueueUserWorkItem(_ => Start());
			else
				RobloxFpsCap.Shutdown();
        }

        private static void WatchRoblox(CancellationTokenSource runCts)
        {
            try
            {
                while (!runCts.IsCancellationRequested)
                {
                    if (!RobloxLightingOverlay.RobloxWindow.TryGet(out _))
                    {
                        runCts.Cancel();
                        break;
                    }
                    if (runCts.Token.WaitHandle.WaitOne(750))
                        break;
                }
            }
            catch
            {
            }
        }
    }
}
