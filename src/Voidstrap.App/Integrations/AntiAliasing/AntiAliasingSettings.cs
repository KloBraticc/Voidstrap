using System;

namespace Voidstrap.Integrations.AntiAliasing
{
    public static class AntiAliasingSettings
    {
        public static readonly string[] MethodNames = new string[] { "Off", "FXAA", "FXAA Ultra", "SMAA", "SMAA Ultra", "DLAA", "NFAA", "TSAA" };

        public static int MethodIndex => Math.Clamp(App.Settings.Prop.AntiAliasingMethodIndex, 0, MethodNames.Length - 1);
    }
}
