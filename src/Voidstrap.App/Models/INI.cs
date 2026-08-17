namespace Voidstrap.Models
{
    public static class IniFile
    {
        public static Dictionary<string, string> Read(string path)
        {
            var dict = new Dictionary<string, string>();
            if (string.IsNullOrWhiteSpace(path))
                return dict;

            string[] lines;
            try
            {
                if (!File.Exists(path))
                    return dict;
                lines = File.ReadAllLines(path);
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine("IniFile::Read", $"Could not read {path}: {ex.Message}");
                return dict;
            }

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("[") || line.StartsWith(";"))
                    continue;

                var split = line.Split('=', 2);
                if (split.Length == 2)
                    dict[split[0].Trim()] = split[1].Trim();
            }
            return dict;
        }

        public static bool Write(string path, Dictionary<string, string> data)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;
            try
            {
                string? directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                using var sw = new StreamWriter(path);
                sw.WriteLine("[Crosshair]");
                foreach (var kv in data)
                    sw.WriteLine($"{kv.Key}={kv.Value}");
                return true;
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine("IniFile::Write", $"Could not save {path}: {ex.Message}");
                return false;
            }
        }
    }
}
