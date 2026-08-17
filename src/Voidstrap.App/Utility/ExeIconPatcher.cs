using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Voidstrap.Utility
{
    public static class ExeIconPatcher
    {
        private const string LOG_IDENT = "ExeIconPatcher";
        public const string BackupSuffix = ".vsiconbak";

        private static readonly IntPtr RT_ICON = (IntPtr)3;
        private static readonly IntPtr RT_GROUP_ICON = (IntPtr)14;
        private const uint LOAD_LIBRARY_AS_DATAFILE = 0x00000002;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr BeginUpdateResource(string pFileName, bool bDeleteExistingResources);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateResource(IntPtr hUpdate, IntPtr lpType, IntPtr lpName, ushort wLanguage, byte[]? lpData, uint cb);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool EndUpdateResource(IntPtr hUpdate, bool fDiscard);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr hModule);

        private delegate bool EnumResNameProc(IntPtr hModule, IntPtr lpType, IntPtr lpName, IntPtr lParam);
        private delegate bool EnumResLangProc(IntPtr hModule, IntPtr lpType, IntPtr lpName, ushort wLang, IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool EnumResourceNames(IntPtr hModule, IntPtr lpszType, EnumResNameProc lpEnumFunc, IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool EnumResourceLanguages(IntPtr hModule, IntPtr lpType, IntPtr lpName, EnumResLangProc lpEnumFunc, IntPtr lParam);

        public static bool IsValidExecutable(string exePath)
        {
            if (!File.Exists(exePath))
                return false;

            IntPtr h = LoadLibraryEx(exePath, IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE);
            if (h == IntPtr.Zero)
                return false;

            FreeLibrary(h);
            return true;
        }

        public static bool Restore(string exePath)
        {
            try
            {
                string backup = exePath + BackupSuffix;
                if (!File.Exists(backup))
                    return false;

                ClearReadOnly(exePath);
                File.Copy(backup, exePath, true);
                App.Logger.WriteLine(LOG_IDENT, $"Restored original icon for {Path.GetFileName(exePath)}.");
                return true;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Restore failed: {ex.Message}");
                return false;
            }
        }

        public static bool HasBackup(string exePath) => File.Exists(exePath + BackupSuffix);

        public static bool ApplyIcon(string exePath, string icoPath)
        {
            try
            {
                if (!File.Exists(exePath) || !File.Exists(icoPath))
                {
                    App.Logger.WriteLine(LOG_IDENT, "Target executable or icon file not found.");
                    return false;
                }

                var entries = ParseIcoFile(icoPath, out byte[] fileBytes);
                if (entries.Count == 0)
                {
                    App.Logger.WriteLine(LOG_IDENT, "Icon file had no images.");
                    return false;
                }

                if (!GetMainIconGroup(exePath, out ushort groupId, out ushort lang))
                {
                    groupId = 1;
                    lang = 0;
                }

                string backup = exePath + BackupSuffix;
                ClearReadOnly(exePath);
                if (!File.Exists(backup))
                    File.Copy(exePath, backup, false);

                IntPtr hUpdate = BeginUpdateResource(exePath, false);
                if (hUpdate == IntPtr.Zero)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"BeginUpdateResource failed ({Marshal.GetLastWin32Error()})");
                    return false;
                }

                const ushort baseIconId = 4000;
                var grpEntries = new List<GrpIconDirEntry>();

                for (int i = 0; i < entries.Count; i++)
                {
                    var e = entries[i];
                    ushort iconId = (ushort)(baseIconId + i);

                    byte[] imageData = new byte[e.BytesInRes];
                    Array.Copy(fileBytes, e.ImageOffset, imageData, 0, e.BytesInRes);

                    if (!UpdateResource(hUpdate, RT_ICON, (IntPtr)iconId, lang, imageData, (uint)imageData.Length))
                    {
                        App.Logger.WriteLine(LOG_IDENT, $"UpdateResource (RT_ICON {iconId}) failed ({Marshal.GetLastWin32Error()})");
                        EndUpdateResource(hUpdate, true);
                        return false;
                    }

                    grpEntries.Add(new GrpIconDirEntry
                    {
                        Width = e.Width,
                        Height = e.Height,
                        ColorCount = e.ColorCount,
                        Reserved = 0,
                        Planes = e.Planes,
                        BitCount = e.BitCount,
                        BytesInRes = (uint)e.BytesInRes,
                        Id = iconId
                    });
                }

                byte[] groupData = BuildGroupIcon(grpEntries);

                if (!UpdateResource(hUpdate, RT_GROUP_ICON, (IntPtr)groupId, lang, groupData, (uint)groupData.Length))
                {
                    App.Logger.WriteLine(LOG_IDENT, $"UpdateResource (RT_GROUP_ICON {groupId}) failed ({Marshal.GetLastWin32Error()}).");
                    EndUpdateResource(hUpdate, true);
                    return false;
                }

                if (!EndUpdateResource(hUpdate, false))
                {
                    App.Logger.WriteLine(LOG_IDENT, $"EndUpdateResource failed ({Marshal.GetLastWin32Error()}). Restoring backup.");
                    Restore(exePath);
                    return false;
                }
                if (!IsValidExecutable(exePath))
                {
                    App.Logger.WriteLine(LOG_IDENT, "Patched executable failed validation; restoring backup.");
                    Restore(exePath);
                    return false;
                }

                App.Logger.WriteLine(LOG_IDENT, $"Applied icon to {Path.GetFileName(exePath)} (group {groupId}, lang {lang}, {entries.Count} images).");
                return true;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                try { Restore(exePath); } catch { }
                return false;
            }
        }

        private static void ClearReadOnly(string path)
        {
            try
            {
                var attr = File.GetAttributes(path);
                if (attr.HasFlag(FileAttributes.ReadOnly))
                    File.SetAttributes(path, attr & ~FileAttributes.ReadOnly);
            }
            catch { }
        }

        private struct IcoEntry
        {
            public byte Width;
            public byte Height;
            public byte ColorCount;
            public ushort Planes;
            public ushort BitCount;
            public int BytesInRes;
            public int ImageOffset;
        }

        private struct GrpIconDirEntry
        {
            public byte Width;
            public byte Height;
            public byte ColorCount;
            public byte Reserved;
            public ushort Planes;
            public ushort BitCount;
            public uint BytesInRes;
            public ushort Id;
        }

        private static List<IcoEntry> ParseIcoFile(string icoPath, out byte[] fileBytes)
        {
            fileBytes = File.ReadAllBytes(icoPath);
            var list = new List<IcoEntry>();

            if (fileBytes.Length < 6)
                return list;

            ushort type = BitConverter.ToUInt16(fileBytes, 2);
            ushort count = BitConverter.ToUInt16(fileBytes, 4);
            if (type != 1)
                return list;

            int offset = 6;
            for (int i = 0; i < count; i++)
            {
                if (offset + 16 > fileBytes.Length)
                    break;

                var e = new IcoEntry
                {
                    Width = fileBytes[offset + 0],
                    Height = fileBytes[offset + 1],
                    ColorCount = fileBytes[offset + 2],
                    Planes = BitConverter.ToUInt16(fileBytes, offset + 4),
                    BitCount = BitConverter.ToUInt16(fileBytes, offset + 6),
                    BytesInRes = BitConverter.ToInt32(fileBytes, offset + 8),
                    ImageOffset = BitConverter.ToInt32(fileBytes, offset + 12)
                };

                if (e.ImageOffset >= 0 && e.BytesInRes > 0 && e.ImageOffset + e.BytesInRes <= fileBytes.Length)
                    list.Add(e);

                offset += 16;
            }

            return list;
        }

        private static byte[] BuildGroupIcon(List<GrpIconDirEntry> entries)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            bw.Write((ushort)0);
            bw.Write((ushort)1);
            bw.Write((ushort)entries.Count);

            foreach (var e in entries)
            {
                bw.Write(e.Width);
                bw.Write(e.Height);
                bw.Write(e.ColorCount);
                bw.Write(e.Reserved);
                bw.Write(e.Planes);
                bw.Write(e.BitCount);
                bw.Write(e.BytesInRes);
                bw.Write(e.Id);
            }

            bw.Flush();
            return ms.ToArray();
        }

        private static bool GetMainIconGroup(string exePath, out ushort groupId, out ushort lang)
        {
            groupId = 1;
            lang = 0;

            ushort best = ushort.MaxValue;
            bool found = false;

            IntPtr hModule = LoadLibraryEx(exePath, IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE);
            if (hModule == IntPtr.Zero)
                return false;

            try
            {
                EnumResourceNames(hModule, RT_GROUP_ICON, (mod, type, name, param) =>
                {
                    if (((long)name >> 16) == 0)
                    {
                        ushort id = (ushort)name;
                        if (id < best)
                        {
                            best = id;
                            found = true;
                        }
                    }
                    return true;
                }, IntPtr.Zero);

                if (found)
                {
                    groupId = best;
                    ushort detectedLang = 0;
                    bool gotLang = false;

                    EnumResourceLanguages(hModule, RT_GROUP_ICON, (IntPtr)best, (mod, type, name, wLang, param) =>
                    {
                        detectedLang = wLang;
                        gotLang = true;
                        return false;
                    }, IntPtr.Zero);

                    if (gotLang)
                        lang = detectedLang;
                }
            }
            catch { }
            finally
            {
                FreeLibrary(hModule);
            }

            return found;
        }
    }
}
