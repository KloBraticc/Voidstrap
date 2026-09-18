// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;
using System.Diagnostics;

namespace Wpf.Ui.Controls;

internal static class NavigateUriLauncher
{
    private static readonly (string Command, string? Verb)[] DesktopOpeners =
    {
        ("xdg-open", null),
        ("gio", "open"),
        ("kde-open6", null),
        ("kde-open5", null),
        ("exo-open", null)
    };

    internal static bool Open(string? navigateUri)
    {
        if (string.IsNullOrWhiteSpace(navigateUri))
        {
            return false;
        }

        string target = Uri.TryCreate(navigateUri, UriKind.Absolute, out Uri? uri)
            ? uri.AbsoluteUri
            : navigateUri;

        if (!OperatingSystem.IsLinux())
        {
            try
            {
                using Process? process = Process.Start(new ProcessStartInfo(target)
                {
                    UseShellExecute = true
                });
                return process is not null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        foreach ((string command, string? verb) in DesktopOpeners)
        {
            try
            {
                ProcessStartInfo startInfo = new(command)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                if (verb is not null)
                {
                    startInfo.ArgumentList.Add(verb);
                }

                startInfo.ArgumentList.Add(target);
                using Process? process = Process.Start(startInfo);
                if (process is not null)
                {
                    return true;
                }
            }
            catch (Exception)
            {
            }
        }

        return false;
    }
}
