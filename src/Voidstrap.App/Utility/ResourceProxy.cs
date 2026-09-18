using System.Globalization;
using System.Reflection;
using System.Resources;
using Voidstrap.Resources;
using Voidstrap.Utility;

namespace Voidstrap.Utility
{
    public class ResourceProxy : ResourceManager
    {
        private readonly ResourceManager _baseManager;

        public ResourceProxy(ResourceManager baseManager)
        {
            _baseManager = baseManager;
        }

        public override string? GetString(string name)
        {
            return GetString(name, null);
        }

        public override string? GetString(string name, CultureInfo? culture)
        {
            string? original = _baseManager.GetString(name, culture ?? Locale.CurrentCulture);

            if (App.Settings.Prop.AutoTranslate && !string.IsNullOrEmpty(original))
            {
                try
                {
                    return TranslationService.Translate(original!, App.Settings.Prop.AutoTranslateLanguage);
                }
                catch
                {
                    return original;
                }
            }

            return original;
        }

        public override ResourceSet? GetResourceSet(CultureInfo culture, bool createIfNotExists, bool tryParents)
        {
            return _baseManager.GetResourceSet(culture, createIfNotExists, tryParents);
        }

        public override object? GetObject(string name)
        {
            return _baseManager.GetObject(name);
        }

        public override object? GetObject(string name, CultureInfo? culture)
        {
            return _baseManager.GetObject(name, culture ?? Locale.CurrentCulture);
        }

        public override void ReleaseAllResources()
        {
            _baseManager.ReleaseAllResources();
        }

        public static void Inject()
        {
            var field = typeof(Strings).GetField("resourceMan", BindingFlags.Static | BindingFlags.NonPublic);
            if (field == null)
            {
                return;
            }
            ResourceManager originalManager = Strings.ResourceManager;
            if (originalManager is not ResourceProxy)
            {
                field.SetValue(null, new ResourceProxy(originalManager));
            }
        }
    }
}
