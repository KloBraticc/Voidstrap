using Voidstrap.Utility;
using static Voidstrap.Models.Persistable.AppSettings;

namespace Voidstrap.Integrations
{
    public static class InGameResolutionApplier
    {
        public static void Apply(ResolutionSetting res)
        {
            int code = DisplaySystem.ApplyMode(res.Monitor, res.Width, res.Height, res.RefreshRate);

            if (code != DisplaySystem.Success)
            {
                App.Logger.WriteLine(
                    "InGameResolution",
                    $"Failed to apply resolution {res.Width}x{res.Height}@{res.RefreshRate} on '{res.Monitor ?? "primary"}': {DisplaySystem.DescribeError(code)}"
                );
            }
        }
    }
}
