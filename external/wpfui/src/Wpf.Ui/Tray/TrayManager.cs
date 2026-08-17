// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, 
// You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski
// and WPF UI Contributors. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Interop;

namespace Wpf.Ui.Tray
{
    /// <summary>
    /// Manages system tray icons for WPF applications.
    /// </summary>
    internal static class TrayManager
    {
        private static readonly Dictionary<INotifyIcon, HwndSource> ParentSources = new();
        /// <summary>
        /// Registers a tray icon using the application's main window as parent.
        /// </summary>
        public static bool Register(INotifyIcon notifyIcon)
        {
            return Register(notifyIcon, GetParentSource());
        }

        /// <summary>
        /// Registers a tray icon using a specified <see cref="Window"/> as parent.
        /// </summary>
        public static bool Register(INotifyIcon notifyIcon, Window parentWindow)
        {
            if (parentWindow is null)
                return false;

            return Register(notifyIcon, PresentationSource.FromVisual(parentWindow) as HwndSource);
        }

        /// <summary>
        /// Registers a tray icon using a specified <see cref="HwndSource"/>.
        /// </summary>
        public static bool Register(INotifyIcon notifyIcon, HwndSource parentSource)
        {
            if (notifyIcon is null)
                throw new ArgumentNullException(nameof(notifyIcon));

            if (parentSource is null)
            {
                if (notifyIcon.IsRegistered)
                    Unregister(notifyIcon);
                return false;
            }

            if (parentSource.Handle == IntPtr.Zero)
                return false;

            // Ensure clean re-registration
            if (notifyIcon.IsRegistered)
                Unregister(notifyIcon);

            notifyIcon.Id = TrayData.NotifyIcons.Count + 1;
            notifyIcon.HookWindow = new TrayHandler(
                $"wpfui_th_{parentSource.Handle}_{notifyIcon.Id}",
                parentSource.Handle)
            {
                ElementId = notifyIcon.Id
            };

            // Prepare NOTIFYICONDATA
            notifyIcon.ShellIconData = new Interop.Shell32.NOTIFYICONDATA
            {
                uID = notifyIcon.Id,
                uFlags = Interop.Shell32.NIF.MESSAGE,
                uCallbackMessage = (int)Interop.User32.WM.TRAYMOUSEMESSAGE,
                hWnd = notifyIcon.HookWindow.Handle,
                dwState = 0x2
            };

            // Set tooltip text
            if (!string.IsNullOrWhiteSpace(notifyIcon.TooltipText))
            {
                notifyIcon.ShellIconData.szTip = notifyIcon.TooltipText;
                notifyIcon.ShellIconData.uFlags |= Interop.Shell32.NIF.TIP;
            }

            // Set icon handle
            var hIcon = notifyIcon.Icon != null
                ? Hicon.FromSource(notifyIcon.Icon)
                : Hicon.FromApp();

            if (hIcon != IntPtr.Zero)
            {
                notifyIcon.ShellIconData.hIcon = hIcon;
                notifyIcon.ShellIconData.uFlags |= Interop.Shell32.NIF.ICON;
            }

            // Add window hook
            notifyIcon.HookWindow.AddHook(notifyIcon.WndProc);

            if (!Interop.Shell32.Shell_NotifyIcon(Interop.Shell32.NIM.ADD, notifyIcon.ShellIconData))
            {
                notifyIcon.HookWindow.RemoveHook(notifyIcon.WndProc);
                notifyIcon.HookWindow.Dispose();
                notifyIcon.HookWindow = null;
                Hicon.Destroy(notifyIcon.ShellIconData.hIcon);
                notifyIcon.ShellIconData.hIcon = IntPtr.Zero;
                return false;
            }

            TrayData.NotifyIcons.Add(notifyIcon);
            ParentSources[notifyIcon] = parentSource;
            parentSource.Disposed += OnParentSourceDisposed;
            notifyIcon.IsRegistered = true;

            return true;
        }

        /// <summary>
        /// Unregisters and removes a tray icon from the system tray.
        /// </summary>
        public static bool Unregister(INotifyIcon notifyIcon)
        {
            if (notifyIcon is null)
                throw new ArgumentNullException(nameof(notifyIcon));

            if (!notifyIcon.IsRegistered || notifyIcon.ShellIconData is null)
                return false;

            Interop.Shell32.Shell_NotifyIcon(Interop.Shell32.NIM.DELETE, notifyIcon.ShellIconData);

            notifyIcon.IsRegistered = false;
            if (ParentSources.Remove(notifyIcon, out HwndSource? parentSource))
                parentSource.Disposed -= OnParentSourceDisposed;
            notifyIcon.HookWindow?.RemoveHook(notifyIcon.WndProc);
            notifyIcon.HookWindow?.Dispose();
            notifyIcon.HookWindow = null;
            Hicon.Destroy(notifyIcon.ShellIconData.hIcon);
            notifyIcon.ShellIconData.hIcon = IntPtr.Zero;

            TrayData.NotifyIcons.Remove(notifyIcon);

            return true;
        }

        private static void OnParentSourceDisposed(object? sender, EventArgs e)
        {
            if (sender is not HwndSource source)
                return;
            foreach (var pair in new List<KeyValuePair<INotifyIcon, HwndSource>>(ParentSources))
            {
                if (ReferenceEquals(pair.Value, source))
                    Unregister(pair.Key);
            }
        }

        /// <summary>
        /// Retrieves the main application's <see cref="HwndSource"/>.
        /// </summary>
        private static HwndSource? GetParentSource()
        {
            var mainWindow = Application.Current?.MainWindow;
            return mainWindow is null
                ? null
                : PresentationSource.FromVisual(mainWindow) as HwndSource;
        }
    }
}
