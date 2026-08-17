namespace Voidstrap.Utility
{
    public static class WebsiteUrl
    {
        public static string Absolute(string? value)
        {
            string v = value ?? "";
            if (v.Length == 0) return "";
            if (v[0] != '/') return v;
            return App.WebsiteBaseUrl.TrimEnd('/') + v;
        }
    }
}
