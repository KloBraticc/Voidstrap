using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using Voidstrap.UI.Elements.Settings.Pages;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings;

public partial class MainWindow
{
    private const double JitSliceMilliseconds = 6.0;

    private static readonly string[] JitWarmupNamespaces =
    {
        "Voidstrap.UI.Elements.Settings",
        "Voidstrap.UI.ViewModels.Settings",
        "Voidstrap.UI.Elements.Controls",
    };

    private static bool _jitWarmupDone;

    private readonly Queue<Type> _pageWarmupQueue = new Queue<Type>();

    private bool _pageWarmupStarted;

    private System.Threading.Tasks.Task<List<MethodBase>>? _jitWarmupList;

    private int _jitWarmupIndex;

    private int _jitWarmupCount;

    private long _pageWarmupTicks;

    private FrameworkElement? _pendingPrelayout;

    private const int WarmupQuietMilliseconds = 700;

    private const int MaxWarmupDeferrals = 12;

    private long _lastWarmupInputTicks;

    private int _warmupDeferrals;

    private bool _warmupInputHooked;

    private DispatcherTimer? _warmupDelayTimer;

    private void StartPageWarmup()
    {
        if (_pageWarmupStarted || _isClosed || Voidstrap.Utility.Platform.IsLinux)
        {
            return;
        }
        _pageWarmupStarted = true;
        _pageWarmupTicks = Stopwatch.GetTimestamp();
        foreach (NavigationItem item in GetNavigationItemsInServiceOrder())
        {
            if (item.PageType != null && item.Cache && item.IsEnabled && item.Visibility == Visibility.Visible && !_pageWarmupQueue.Contains(item.PageType))
            {
                _pageWarmupQueue.Enqueue(item.PageType);
            }
        }
        _pageWarmupQueue.Enqueue(typeof(LibraryPage));
        if (!_jitWarmupDone)
        {
            _jitWarmupList = System.Threading.Tasks.Task.Run(() => EnumerateJitWarmupMethods().ToList());
        }
        _warmupInputHooked = true;
        PreviewMouseMove += OnWarmupMouseInput;
        PreviewMouseDown += OnWarmupMouseInput;
        PreviewKeyDown += OnWarmupKeyInput;
        QueueWarmupStep();
    }

    private void OnWarmupMouseInput(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _lastWarmupInputTicks = Environment.TickCount64;
    }

    private void OnWarmupKeyInput(object sender, System.Windows.Input.KeyEventArgs e)
    {
        _lastWarmupInputTicks = Environment.TickCount64;
    }

    private bool DeferWarmupForInput()
    {
        if (_warmupDeferrals >= MaxWarmupDeferrals || Environment.TickCount64 - _lastWarmupInputTicks >= WarmupQuietMilliseconds)
        {
            return false;
        }
        _warmupDeferrals++;
        if (_warmupDelayTimer == null)
        {
            _warmupDelayTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(WarmupQuietMilliseconds)
            };
            _warmupDelayTimer.Tick += WarmupDelayTimer_Tick;
        }
        _warmupDelayTimer.Stop();
        _warmupDelayTimer.Start();
        return true;
    }

    private void WarmupDelayTimer_Tick(object? sender, EventArgs e)
    {
        _warmupDelayTimer?.Stop();
        QueueWarmupStep();
    }

    private void StopPageWarmup()
    {
        _pageWarmupQueue.Clear();
        _pendingPrelayout = null;
        if (_warmupDelayTimer != null)
        {
            _warmupDelayTimer.Stop();
            _warmupDelayTimer.Tick -= WarmupDelayTimer_Tick;
            _warmupDelayTimer = null;
        }
        if (_warmupInputHooked)
        {
            _warmupInputHooked = false;
            PreviewMouseMove -= OnWarmupMouseInput;
            PreviewMouseDown -= OnWarmupMouseInput;
            PreviewKeyDown -= OnWarmupKeyInput;
        }
        _jitWarmupList = null;
    }

    private void QueueWarmupStep()
    {
        if (!_isClosed)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(RunWarmupStep));
        }
    }

    private void RunWarmupStep()
    {
        if (_isClosed)
        {
            return;
        }
        if ((_pendingPrelayout != null || _pageWarmupQueue.Count > 0) && DeferWarmupForInput())
        {
            return;
        }
        FrameworkElement? pending = _pendingPrelayout;
        if (pending != null)
        {
            _pendingPrelayout = null;
            long started = Stopwatch.GetTimestamp();
            try
            {
                if (!pending.IsLoaded)
                {
                    PrelayoutPage(pending);
                }
                if (pending is System.Windows.Controls.Page searchPage && _indexedSearchPages.TryAdd(searchPage, searchPage))
                {
                    IndexDynamicPageSearchEntries(searchPage);
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("MainWindow::PageWarmup", pending.GetType().Name + " could not be laid out: " + ex.Message);
            }
            App.Logger.WriteLine("MainWindow::PageWarmup", pending.GetType().Name + " laid out in " + (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds + " ms");
            QueueWarmupStep();
            return;
        }
        while (_pageWarmupQueue.Count > 0)
        {
            Type pageType = _pageWarmupQueue.Dequeue();
            long started = Stopwatch.GetTimestamp();
            FrameworkElement? page = null;
            try
            {
                page = pageType == typeof(LibraryPage) ? WarmLibraryPage() : RootNavigation.PrecachePage(pageType);
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("MainWindow::PageWarmup", pageType.Name + " could not be prepared: " + ex.Message);
            }
            if (page != null)
            {
                App.Logger.WriteLine("MainWindow::PageWarmup", pageType.Name + " created in " + (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds + " ms");
                _pendingPrelayout = page;
                QueueWarmupStep();
                return;
            }
        }
        if (_jitWarmupDone)
        {
            StopPageWarmup();
            return;
        }
        System.Threading.Tasks.Task<List<MethodBase>>? listTask = _jitWarmupList;
        if (listTask == null)
        {
            StopPageWarmup();
            return;
        }
        if (!listTask.IsCompleted)
        {
            listTask.ContinueWith(OnJitWarmupListReady, System.Threading.CancellationToken.None, System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously, System.Threading.Tasks.TaskScheduler.Default);
            return;
        }
        if (!listTask.IsCompletedSuccessfully)
        {
            App.Logger.WriteLine("MainWindow::PageWarmup", "Code warm up skipped: " + listTask.Exception?.GetBaseException().Message);
            _jitWarmupDone = true;
            StopPageWarmup();
            return;
        }
        List<MethodBase> methods = listTask.Result;
        long sliceStarted = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(sliceStarted).TotalMilliseconds < JitSliceMilliseconds)
        {
            if (_jitWarmupIndex >= methods.Count)
            {
                _jitWarmupDone = true;
                App.Logger.WriteLine("MainWindow::PageWarmup", "Pages and " + _jitWarmupCount + " methods ready after " + (int)Stopwatch.GetElapsedTime(_pageWarmupTicks).TotalMilliseconds + " ms");
                StopPageWarmup();
                return;
            }
            try
            {
                RuntimeHelpers.PrepareMethod(methods[_jitWarmupIndex].MethodHandle);
                _jitWarmupCount++;
            }
            catch
            {
            }
            _jitWarmupIndex++;
        }
        QueueWarmupStep();
    }

    private void OnJitWarmupListReady(System.Threading.Tasks.Task<List<MethodBase>> task)
    {
        if (!_isClosed)
        {
            QueueWarmupStep();
        }
    }

    private LibraryPage? WarmLibraryPage()
    {
        if (_libraryPage != null)
        {
            return null;
        }
        _libraryPage = new LibraryPage();
        return _libraryPage;
    }

    private void PrelayoutPage(FrameworkElement page)
    {
        double width = RootFrame.ActualWidth;
        double height = RootFrame.ActualHeight;
        if (width <= 0.0 || height <= 0.0)
        {
            return;
        }
        page.Measure(new Size(width, height));
        page.Arrange(new Rect(0.0, 0.0, width, height));
    }

    private static IEnumerable<MethodBase> EnumerateJitWarmupMethods()
    {
        const BindingFlags flags = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        Type[] types;
        try
        {
            types = typeof(MainWindow).Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.OfType<Type>().ToArray();
        }
        foreach (Type type in types)
        {
            string? ns = type.Namespace;
            if (ns == null || type.ContainsGenericParameters || type.IsInterface || !JitWarmupNamespaces.Any(prefix => ns.StartsWith(prefix, StringComparison.Ordinal)))
            {
                continue;
            }
            MethodBase[] methods;
            try
            {
                methods = type.GetMethods(flags).Cast<MethodBase>().Concat(type.GetConstructors(flags).Where(ctor => !ctor.IsStatic)).ToArray();
            }
            catch
            {
                continue;
            }
            foreach (MethodBase method in methods)
            {
                if (method.IsAbstract || method.ContainsGenericParameters || (method.Attributes & MethodAttributes.PinvokeImpl) != 0)
                {
                    continue;
                }
                MethodImplAttributes impl = method.MethodImplementationFlags;
                if ((impl & MethodImplAttributes.CodeTypeMask) != MethodImplAttributes.IL || (impl & MethodImplAttributes.InternalCall) != 0)
                {
                    continue;
                }
                yield return method;
            }
        }
    }
}
