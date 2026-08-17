using System;
using System.IO;
using System.Text.Json;

namespace Voidstrap.Utility
{
    internal static class WebsiteCache
    {
        private static string AccountKey()
        {
            string? token = WebsiteAuth.GetToken();
            if (string.IsNullOrEmpty(token) || token.Length < 8)
                return "anon";
            string tail = token.Substring(token.Length - 8);
            foreach (char c in tail)
            {
                if (!char.IsLetterOrDigit(c))
                    return "anon";
            }
            return tail;
        }

        private static string PathFor(string name)
        {
            return Path.Combine(Paths.Config, "WebsiteCache_" + name + "_" + AccountKey() + ".json");
        }

        public static void Save<T>(string name, T value)
        {
            try
            {
                File.WriteAllText(PathFor(name), JsonSerializer.Serialize(value));
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("WebsiteCache::Save", name + " could not be cached: " + ex.Message);
            }
        }

        public static T? Load<T>(string name) where T : class
        {
            try
            {
                string path = PathFor(name);
                if (!File.Exists(path))
                    return null;
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("WebsiteCache::Load", name + " could not be read: " + ex.Message);
                return null;
            }
        }

        public static void Clear(string name)
        {
            try
            {
                string path = PathFor(name);
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("WebsiteCache::Clear", name + " could not be cleared: " + ex.Message);
            }
        }
    }
}
