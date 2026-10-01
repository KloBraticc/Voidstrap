using System.Text.Json;
using Voidstrap.UI.Elements.Dialogs;

namespace Voidstrap.Utility;

internal sealed class LinuxFlagProfiles
{
    private const int MaximumBytes = 16 * 1024 * 1024;
    private const int MaximumFlags = 100000;
    private readonly string _directory;

    internal LinuxFlagProfiles(string directory)
    {
        _directory = Path.GetFullPath(directory);
    }

    internal List<FlagProfile> Load()
    {
        Directory.CreateDirectory(_directory);
        List<FlagProfile> profiles = [];
        foreach (string file in Directory.EnumerateFiles(_directory))
        {
            if (!string.Equals(Path.GetExtension(file), ".json", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                profiles.Add(Read(file));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            {
                App.Logger?.WriteLine("FlagProfiles::Load", "The profile " + Path.GetFileName(file) + " could not be loaded: " + ex.Message);
            }
        }
        return profiles;
    }

    internal FlagProfile Save(string name, IReadOnlyDictionary<string, string> flags)
    {
        Validate(flags);
        string contents = JsonSerializer.Serialize(flags, JsonOptions.Indented);
        if (System.Text.Encoding.UTF8.GetByteCount(contents) > MaximumBytes)
            throw new InvalidDataException("The profile exceeds the allowed size.");
        Directory.CreateDirectory(_directory);
        string stem = Path.GetFileNameWithoutExtension(SafeFileName(name));
        string temporary = Path.Combine(_directory, ".profile." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, contents);
            for (int suffix = 1; suffix <= 10000; suffix++)
            {
                string filename = stem + (suffix == 1 ? "" : " (" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")") + ".json";
                string target = Resolve(filename);
                if (Directory.EnumerateFiles(_directory).Any(file => string.Equals(Path.GetFileName(file), filename, StringComparison.OrdinalIgnoreCase)))
                    continue;
                try
                {
                    File.Move(temporary, target);
                }
                catch (IOException) when (File.Exists(target))
                {
                    continue;
                }
                FlagProfile stored = Read(target);
                if (stored.Flags.Count != flags.Count || flags.Any(flag => !stored.Flags.TryGetValue(flag.Key, out string? value) || value != flag.Value))
                    throw new InvalidDataException("The complete flag list could not be saved.");
                return stored;
            }
            throw new IOException("No unused profile name is available.");
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    internal FlagProfile Rename(string filename, string name)
    {
        string source = Resolve(filename);
        Read(source);
        string target = Resolve(SafeFileName(name));
        if (source == target)
            return Read(source);
        if (Directory.EnumerateFiles(_directory).Any(file => file != source && string.Equals(Path.GetFileName(file), Path.GetFileName(target), StringComparison.OrdinalIgnoreCase)))
            throw new IOException("A profile with that name already exists.");
        File.Move(source, target);
        return Read(target);
    }

    internal void Delete(string filename)
    {
        string file = Resolve(filename);
        if (new FileInfo(file).LinkTarget is not null)
            throw new IOException("The profile cannot be a symbolic link.");
        File.Delete(file);
    }

    private static FlagProfile Read(string file)
    {
        FileInfo info = new(file);
        if (info.LinkTarget is not null || info.Length <= 0 || info.Length > MaximumBytes)
            throw new InvalidDataException("The profile file is invalid or exceeds the allowed size.");
        Dictionary<string, JsonElement> raw = JsonFile.Deserialize<Dictionary<string, JsonElement>>(file, JsonOptions.Tolerant, MaximumBytes);
        Dictionary<string, string> flags = new(StringComparer.Ordinal);
        foreach ((string key, JsonElement value) in raw)
        {
            flags[key] = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? "",
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "True",
                JsonValueKind.False => "False",
                _ => throw new InvalidDataException("A profile flag has an unsupported value.")
            };
        }
        Validate(flags);
        string filename = Path.GetFileName(file);
        return new FlagProfile { Id = "local:" + filename, Name = Path.GetFileNameWithoutExtension(filename), Flags = flags, LocalFileName = filename };
    }

    private static void Validate(IReadOnlyDictionary<string, string> flags)
    {
        if (flags.Count > MaximumFlags || flags.Any(flag => string.IsNullOrWhiteSpace(flag.Key) || flag.Value is null))
            throw new InvalidDataException("The profile flag list is invalid or exceeds the allowed size.");
    }

    private string Resolve(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename) || filename != Path.GetFileName(filename) || filename.IndexOfAny(['/', '\\']) >= 0
            || !string.Equals(Path.GetExtension(filename), ".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The profile filename is invalid.");
        return Path.Combine(_directory, filename);
    }

    private static string SafeFileName(string name)
    {
        string safe = name.Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars())
            safe = safe.Replace(invalid, '_');
        safe = safe.Replace('\\', '_').TrimEnd('.');
        if (string.IsNullOrWhiteSpace(safe) || safe.Any(char.IsControl))
            throw new InvalidDataException("The profile name is invalid.");
        return safe + ".json";
    }
}
