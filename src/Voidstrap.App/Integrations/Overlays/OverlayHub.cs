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
		private static volatile bool _hostProcess;

		private static volatile bool _compositorLive;
		private static volatile bool _linuxHomepageNativeShaderActive;
		private static int _crosshairRefreshPending;

		public static bool InGame => _inGame;

		public static void MarkHostProcess()
		{
			_hostProcess = true;
		}

		private static bool _loggedLinuxHomepageUnavailable;
		private static LinuxHomepageBackgroundOverlay? _linuxHomepage;
		private static int _linuxHomepageGeneration;
		private static Mutex? _linuxHomepageMutex;
		private static bool _linuxHomepageMutexHeld;
		private static System.Threading.Timer? _linuxHomepageRetry;
		private const string LinuxGameplayLeaseName = "VoidstrapLinuxGameplayActive";
		private static readonly object _linuxGameplayLeaseGate = new object();
		private static readonly object _linuxGameplayProbeGate = new object();
		private static readonly NamedWaitHandleOptions _linuxGameplayLeaseOptions = new NamedWaitHandleOptions
		{
			CurrentUserOnly = true,
			CurrentSessionOnly = false
		};
		private static Thread? _linuxGameplayLeaseThread;
		private static AutoResetEvent? _linuxGameplayLeaseSignal;
		private static ManualResetEventSlim? _linuxGameplayLeaseAcknowledged;
		private static volatile bool _linuxGameplayLeaseRequested;
		private static volatile bool _linuxGameplayLeaseStopping;
		private static volatile bool _linuxGameplayLeaseOperational;
		private static int _linuxGameplayLeaseCommandGeneration;
		private static int _linuxGameplayLeaseAcknowledgedGeneration = -1;
		private static Mutex? _linuxGameplayProbeMutex;
		private static int _linuxGameplayLeaseFailureLogged;
		private static int _linuxGameplayProbeFailureLogged;

		internal static bool LinuxHomepageRunning => _linuxHomepage is { IsDisposed: false };
		internal static bool LinuxGameplayLeaseOperational => !Voidstrap.Utility.Platform.IsLinux
			|| _linuxGameplayLeaseOperational
			&& Volatile.Read(ref _linuxGameplayLeaseAcknowledgedGeneration) == Volatile.Read(ref _linuxGameplayLeaseCommandGeneration);

		public static bool CompositorCrosshairActive => _compositorLive && OverlayCrosshair.CanComposite();

		internal static void SetCompositorLive(bool live)
		{
			_compositorLive = live;
			RefreshCrosshair();
		}

		public static void RefreshCrosshair()
		{
			if (Interlocked.Exchange(ref _crosshairRefreshPending, 1) != 0)
				return;
			try
			{
				System.Windows.Application? app = System.Windows.Application.Current;
				if (app == null || app.Dispatcher.HasShutdownStarted || app.Dispatcher.HasShutdownFinished)
				{
					Interlocked.Exchange(ref _crosshairRefreshPending, 0);
					return;
				}
				app.Dispatcher.BeginInvoke(new Action(() =>
				{
					Interlocked.Exchange(ref _crosshairRefreshPending, 0);
					try
					{
						Voidstrap.UI.Elements.Crosshair.CrosshairWindow.Reconcile();
					}
					catch (Exception ex)
					{
						App.Logger.WriteException("OverlayHub::RefreshCrosshair", ex);
					}
				}));
			}
			catch (Exception ex)
			{
				Interlocked.Exchange(ref _crosshairRefreshPending, 0);
				App.Logger.WriteException("OverlayHub::RefreshCrosshair", ex);
			}
		}

		public static bool HomepageBackgroundActive => !_inGame
			&& !_gameTransition
			&& OverlaySettings.HomepageBackgroundEnabled
			&& (!Voidstrap.Utility.Platform.IsLinux || !_linuxHomepageNativeShaderActive && IsLinuxHomepageLifecycleActive());

		internal static void SetLinuxHomepageNativeShaderActive(bool active)
		{
			if (!Voidstrap.Utility.Platform.IsLinux || _linuxHomepageNativeShaderActive == active)
				return;
			_linuxHomepageNativeShaderActive = active;
			App.Logger.WriteLine("Overlays", active
				? "The Sober-native homepage shader is active, disabling delayed window capture"
				: "The Sober-native homepage shader is inactive, enabling the compatible window renderer");
			Refresh();
		}

        public static bool Refresh()
        {
			RefreshCrosshair();
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
			if (Voidstrap.Utility.Platform.IsLinux)
				SetLinuxGameplayLease(true);
			Refresh();
		}

		public static void OnGameLeave()
		{
			_inGame = false;
			_gameTransition = false;
			if (Voidstrap.Utility.Platform.IsLinux)
				SetLinuxGameplayLease(false);
			Refresh();
		}

		internal static void SynchronizeLinuxGameState(bool inGame)
		{
			if (!Voidstrap.Utility.Platform.IsLinux || _shutdown)
				return;
			_inGame = inGame;
			_gameTransition = false;
			SetLinuxGameplayLease(inGame);
			Refresh();
		}

		internal static void ReleaseLinuxGameplayLease()
		{
			if (!Voidstrap.Utility.Platform.IsLinux)
				return;
			_inGame = false;
			_gameTransition = false;
			SetLinuxGameplayLease(false);
		}

		public static void OnGameTransitionStarted()
		{
			_inGame = true;
			_gameTransition = true;
			if (Voidstrap.Utility.Platform.IsLinux)
				SetLinuxGameplayLease(true);
			Stop();
		}

		public static void OnGameTransitionCompleted()
		{
			_inGame = true;
			_gameTransition = false;
			if (Voidstrap.Utility.Platform.IsLinux)
				SetLinuxGameplayLease(true);
			Refresh();
		}

		public static void Shutdown()
		{
			_shutdown = true;
			_inGame = false;
			RefreshCrosshair();
			if (Voidstrap.Utility.Platform.IsLinux)
				ShutdownLinuxGameplayLease();
			Stop();
			RobloxFpsCap.Shutdown();
		}

		private static bool IsLinuxHomepageLifecycleActive()
		{
			if (_shutdown)
				return false;
			lock (_linuxGameplayProbeGate)
			{
				if (_shutdown)
					return false;
				try
				{
					_linuxGameplayProbeMutex ??= new Mutex(false, LinuxGameplayLeaseName, _linuxGameplayLeaseOptions);
					bool acquired;
					try
					{
						acquired = _linuxGameplayProbeMutex.WaitOne(0);
					}
					catch (AbandonedMutexException)
					{
						acquired = true;
					}
					if (!acquired)
						return false;
					_linuxGameplayProbeMutex.ReleaseMutex();
					Interlocked.Exchange(ref _linuxGameplayProbeFailureLogged, 0);
					return true;
				}
				catch (Exception ex)
				{
					if (Interlocked.Exchange(ref _linuxGameplayProbeFailureLogged, 1) == 0)
						App.Logger.WriteLine("OverlayHub", "The Linux gameplay state could not be checked: " + ex.Message);
					return false;
				}
			}
		}

		private static bool SetLinuxGameplayLease(bool active)
		{
			if (!Voidstrap.Utility.Platform.IsLinux || (_shutdown && active))
				return false;
			lock (_linuxGameplayLeaseGate)
			{
				if (!active && _linuxGameplayLeaseThread == null)
					return true;
				if (!EnsureLinuxGameplayLeaseThread())
					return false;
				int currentGeneration = Volatile.Read(ref _linuxGameplayLeaseCommandGeneration);
				if (_linuxGameplayLeaseRequested == active
					&& Volatile.Read(ref _linuxGameplayLeaseAcknowledgedGeneration) == currentGeneration)
					return _linuxGameplayLeaseOperational;

				_linuxGameplayLeaseRequested = active;
				int generation = Interlocked.Increment(ref _linuxGameplayLeaseCommandGeneration);
				ManualResetEventSlim acknowledged = _linuxGameplayLeaseAcknowledged!;
				acknowledged.Reset();
				_linuxGameplayLeaseSignal!.Set();
				if (!WaitForLinuxGameplayLeaseAcknowledgement(acknowledged, generation, 5000))
				{
					if (Interlocked.Exchange(ref _linuxGameplayLeaseFailureLogged, 1) == 0)
						App.Logger.WriteLine("OverlayHub", "The Linux gameplay state lease did not acknowledge its new state");
					return false;
				}
				if (!_linuxGameplayLeaseOperational)
					return false;
				Interlocked.Exchange(ref _linuxGameplayLeaseFailureLogged, 0);
				return true;
			}
		}

		private static bool EnsureLinuxGameplayLeaseThread()
		{
			if (_linuxGameplayLeaseThread is { IsAlive: true })
				return true;
			if (_linuxGameplayLeaseThread != null)
			{
				_linuxGameplayLeaseSignal?.Dispose();
				_linuxGameplayLeaseAcknowledged?.Dispose();
			}

			AutoResetEvent signal = new AutoResetEvent(false);
			ManualResetEventSlim acknowledged = new ManualResetEventSlim(false);
			_linuxGameplayLeaseSignal = signal;
			_linuxGameplayLeaseAcknowledged = acknowledged;
			_linuxGameplayLeaseRequested = false;
			_linuxGameplayLeaseStopping = false;
			_linuxGameplayLeaseOperational = false;
			Volatile.Write(ref _linuxGameplayLeaseAcknowledgedGeneration, -1);
			Thread thread = new Thread(() => RunLinuxGameplayLease(signal, acknowledged))
			{
				IsBackground = true,
				Name = "Linux Gameplay Lease"
			};
			_linuxGameplayLeaseThread = thread;
			try
			{
				thread.Start();
				return true;
			}
			catch (Exception ex)
			{
				_linuxGameplayLeaseThread = null;
				_linuxGameplayLeaseSignal = null;
				_linuxGameplayLeaseAcknowledged = null;
				signal.Dispose();
				acknowledged.Dispose();
				if (Interlocked.Exchange(ref _linuxGameplayLeaseFailureLogged, 1) == 0)
					App.Logger.WriteLine("OverlayHub", "The Linux gameplay state lease could not start: " + ex.Message);
				return false;
			}
		}

		private static void RunLinuxGameplayLease(AutoResetEvent signal, ManualResetEventSlim acknowledged)
		{
			Mutex? lease = null;
			bool held = false;
			try
			{
				lease = new Mutex(false, LinuxGameplayLeaseName, _linuxGameplayLeaseOptions);
				_linuxGameplayLeaseOperational = true;
				while (!_linuxGameplayLeaseStopping)
				{
					int generation = Volatile.Read(ref _linuxGameplayLeaseCommandGeneration);
					bool requested = _linuxGameplayLeaseRequested;
					if (requested && !held)
					{
						try
						{
							held = lease.WaitOne(0);
						}
						catch (AbandonedMutexException)
						{
							held = true;
						}
					}
					else if (!requested && held)
					{
						lease.ReleaseMutex();
						held = false;
					}

					if (_linuxGameplayLeaseStopping)
						break;
					if (generation == Volatile.Read(ref _linuxGameplayLeaseCommandGeneration)
						&& requested == _linuxGameplayLeaseRequested)
					{
						Volatile.Write(ref _linuxGameplayLeaseAcknowledgedGeneration, generation);
						acknowledged.Set();
					}
					signal.WaitOne(requested && !held ? 100 : Timeout.Infinite);
				}
			}
			catch (Exception ex)
			{
				_linuxGameplayLeaseOperational = false;
				if (Interlocked.Exchange(ref _linuxGameplayLeaseFailureLogged, 1) == 0)
					App.Logger.WriteLine("OverlayHub", "The Linux gameplay state lease stopped: " + ex.Message);
			}
			finally
			{
				if (held && lease != null)
				{
					try
					{
						lease.ReleaseMutex();
					}
					catch (ApplicationException)
					{
					}
				}
				lease?.Dispose();
				_linuxGameplayLeaseOperational = false;
				Volatile.Write(
					ref _linuxGameplayLeaseAcknowledgedGeneration,
					Volatile.Read(ref _linuxGameplayLeaseCommandGeneration));
				acknowledged.Set();
			}
		}

		private static bool WaitForLinuxGameplayLeaseAcknowledgement(
			ManualResetEventSlim acknowledged,
			int generation,
			int timeoutMilliseconds)
		{
			long deadline = Environment.TickCount64 + timeoutMilliseconds;
			while (true)
			{
				if (Volatile.Read(ref _linuxGameplayLeaseAcknowledgedGeneration) == generation)
					return true;
				int remaining = (int)Math.Min(int.MaxValue, deadline - Environment.TickCount64);
				if (remaining <= 0)
					return false;
				acknowledged.Wait(Math.Min(remaining, 100));
				if (Volatile.Read(ref _linuxGameplayLeaseAcknowledgedGeneration) == generation)
					return true;
				acknowledged.Reset();
			}
		}

		private static void ShutdownLinuxGameplayLease()
		{
			Thread? thread;
			lock (_linuxGameplayLeaseGate)
			{
				thread = _linuxGameplayLeaseThread;
				if (thread is { IsAlive: true })
				{
					_linuxGameplayLeaseRequested = false;
					_linuxGameplayLeaseStopping = true;
					int generation = Interlocked.Increment(ref _linuxGameplayLeaseCommandGeneration);
					ManualResetEventSlim acknowledged = _linuxGameplayLeaseAcknowledged!;
					acknowledged.Reset();
					_linuxGameplayLeaseSignal!.Set();
					WaitForLinuxGameplayLeaseAcknowledgement(acknowledged, generation, 5000);
					if (Thread.CurrentThread != thread)
						thread.Join(TimeSpan.FromSeconds(5));
				}
				if (thread == null || !thread.IsAlive)
				{
					_linuxGameplayLeaseThread = null;
					_linuxGameplayLeaseSignal?.Dispose();
					_linuxGameplayLeaseSignal = null;
					_linuxGameplayLeaseAcknowledged?.Dispose();
					_linuxGameplayLeaseAcknowledged = null;
				}
			}
			lock (_linuxGameplayProbeGate)
			{
				_linuxGameplayProbeMutex?.Dispose();
				_linuxGameplayProbeMutex = null;
			}
		}

		private static bool Start()
        {
			if (_shutdown || _gameTransition || !_hostProcess)
				return false;
			if (Voidstrap.Utility.Platform.IsLinux)
				return StartLinuxHomepage();
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

		private static bool StartLinuxHomepage()
		{
			if (App.LaunchSettings.WindowAuditFlag.Active)
				return false;
			if (!HomepageBackgroundActive)
			{
				StopLinuxHomepage();
				return false;
			}
			if (!LinuxHomepageBackgroundOverlay.IsSupported)
			{
				if (!_loggedLinuxHomepageUnavailable)
				{
					_loggedLinuxHomepageUnavailable = true;
					App.Logger.WriteLine("Overlays", "The Sober homepage renderer needs an available composited X11 or XWayland display");
				}
				ScheduleLinuxHomepageRetry();
				return false;
			}
			_loggedLinuxHomepageUnavailable = false;

			System.Windows.Application? application = System.Windows.Application.Current;
			if (application == null || application.Dispatcher.HasShutdownStarted || application.Dispatcher.HasShutdownFinished)
				return false;

			int generation = Interlocked.Increment(ref _linuxHomepageGeneration);
			if (application.Dispatcher.CheckAccess())
			{
				StartLinuxHomepageOnDispatcher(generation);
				return LinuxHomepageRunning;
			}

			try
			{
				application.Dispatcher.BeginInvoke(
					System.Windows.Threading.DispatcherPriority.Normal,
					new Action(() => StartLinuxHomepageOnDispatcher(generation)));
				return true;
			}
			catch (InvalidOperationException)
			{
				return false;
			}
		}

		private static void StartLinuxHomepageOnDispatcher(int generation)
		{
			if (generation != Volatile.Read(ref _linuxHomepageGeneration)
				|| _shutdown
				|| !HomepageBackgroundActive)
				return;
			if (LinuxHomepageRunning)
			{
				StopLinuxHomepageRetry();
				return;
			}
			if (_linuxHomepage is { IsDisposed: true } stopping)
			{
				if (!stopping.IsSafeToRelease)
				{
					ScheduleLinuxHomepageRetry();
					return;
				}
				_linuxHomepage = null;
			}
			if (!TryAcquireLinuxHomepageMutex())
			{
				ScheduleLinuxHomepageRetry();
				return;
			}
			StopLinuxHomepageRetry();

			try
			{
				_linuxHomepage?.Dispose();
				_linuxHomepage = new LinuxHomepageBackgroundOverlay();
			}
			catch (Exception ex)
			{
				_linuxHomepage = null;
				ReleaseLinuxHomepageMutex();
				App.Logger.WriteException("OverlayHub::StartLinuxHomepage", ex);
			}
		}

		private static bool TryAcquireLinuxHomepageMutex()
		{
			if (_linuxHomepageMutexHeld)
				return true;
			try
			{
				_linuxHomepageMutex ??= new Mutex(false, "VoidstrapLinuxHomepageOverlayActive");
				try
				{
					_linuxHomepageMutexHeld = _linuxHomepageMutex.WaitOne(0);
				}
				catch (AbandonedMutexException)
				{
					_linuxHomepageMutexHeld = true;
				}
				return _linuxHomepageMutexHeld;
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("OverlayHub", "The Linux homepage ownership lock could not be opened: " + ex.Message);
				return false;
			}
		}

		private static void ReleaseLinuxHomepageMutex()
		{
			if (_linuxHomepageMutexHeld && _linuxHomepageMutex != null)
			{
				try
				{
					_linuxHomepageMutex.ReleaseMutex();
				}
				catch (ApplicationException)
				{
				}
			}
			_linuxHomepageMutexHeld = false;
			_linuxHomepageMutex?.Dispose();
			_linuxHomepageMutex = null;
		}

		private static void ScheduleLinuxHomepageRetry()
		{
			lock (_lock)
			{
				_linuxHomepageRetry ??= new System.Threading.Timer(OnLinuxHomepageRetry, null, 2000, 2000);
			}
		}

		private static void StopLinuxHomepageRetry()
		{
			System.Threading.Timer? timer;
			lock (_lock)
			{
				timer = _linuxHomepageRetry;
				_linuxHomepageRetry = null;
			}
			timer?.Dispose();
		}

		private static void OnLinuxHomepageRetry(object? state)
		{
			if (_shutdown || !HomepageBackgroundActive)
			{
				StopLinuxHomepageRetry();
				return;
			}
			StartLinuxHomepage();
		}

		private static void StopLinuxHomepage()
		{
			StopLinuxHomepageRetry();
			int generation = Interlocked.Increment(ref _linuxHomepageGeneration);
			System.Windows.Application? application = System.Windows.Application.Current;
			if (application == null || application.Dispatcher.HasShutdownStarted || application.Dispatcher.HasShutdownFinished)
				return;
			if (application.Dispatcher.CheckAccess())
			{
				StopLinuxHomepageOnDispatcher(generation);
				return;
			}

			try
			{
				application.Dispatcher.BeginInvoke(
					System.Windows.Threading.DispatcherPriority.Send,
					new Action(() => StopLinuxHomepageOnDispatcher(generation)));
			}
			catch (InvalidOperationException)
			{
			}
		}

		private static void StopLinuxHomepageOnDispatcher(int generation)
		{
			if (generation != Volatile.Read(ref _linuxHomepageGeneration))
				return;
			LinuxHomepageBackgroundOverlay? overlay = _linuxHomepage;
			overlay?.Dispose();
			if (overlay == null || overlay.IsSafeToRelease)
			{
				_linuxHomepage = null;
				ReleaseLinuxHomepageMutex();
			}
			else
			{
				_linuxHomepage = overlay;
			}
		}

		internal static void OnLinuxHomepageOverlayStopped(LinuxHomepageBackgroundOverlay overlay)
		{
			if (!Voidstrap.Utility.Platform.IsLinux)
				return;
			System.Windows.Application? application = System.Windows.Application.Current;
			if (application == null || application.Dispatcher.HasShutdownStarted || application.Dispatcher.HasShutdownFinished)
				return;
			try
			{
				application.Dispatcher.BeginInvoke(
					System.Windows.Threading.DispatcherPriority.Background,
					new Action(() => CompleteLinuxHomepageStop(overlay)));
			}
			catch (InvalidOperationException)
			{
			}
		}

		private static void CompleteLinuxHomepageStop(LinuxHomepageBackgroundOverlay overlay)
		{
			if (!ReferenceEquals(_linuxHomepage, overlay))
				return;
			_linuxHomepage = null;
			ReleaseLinuxHomepageMutex();
			if (!_shutdown && HomepageBackgroundActive)
				StartLinuxHomepage();
		}

        private static void Stop()
        {
			if (Voidstrap.Utility.Platform.IsLinux)
			{
				StopLinuxHomepage();
				return;
			}
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
