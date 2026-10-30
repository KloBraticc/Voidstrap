using System;
using System.Collections.Generic;
using System.IO;

namespace Voidstrap.Utility;

internal static class VinegarPaths
{
    private static string Home
    {
        get
        {
            string? home = Environment.GetEnvironmentVariable("HOME");
            return string.IsNullOrWhiteSpace(home) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : home;
        }
    }

    private static string DataHome
    {
        get
        {
            string? configured = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            return !string.IsNullOrWhiteSpace(configured) && Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(Home, ".local", "share");
        }
    }

    public static string FlatpakDataDirectory => Path.Combine(Home, ".var", "app", "org.vinegarhq.Vinegar", "data", "vinegar");

    public static string NativeDataDirectory => Path.Combine(DataHome, "vinegar");

    public static IEnumerable<string> DataDirectories
    {
        get
        {
            yield return FlatpakDataDirectory;
            yield return NativeDataDirectory;
        }
    }

    public static string RobloxDirectory
    {
        get
        {
            foreach (string data in DataDirectories)
            {
                if (Directory.Exists(data))
                    return Path.Combine(data, "appdata", "Roblox");
            }
            return Path.Combine(FlatpakDataDirectory, "appdata", "Roblox");
        }
    }

    public static IEnumerable<string> LogDirectories
    {
        get
        {
            foreach (string data in DataDirectories)
                yield return Path.Combine(data, "appdata", "Roblox", "logs");
        }
    }

    public static IEnumerable<string> VersionDirectories
    {
        get
        {
            foreach (string data in DataDirectories)
                yield return Path.Combine(data, "versions");
        }
    }
}
