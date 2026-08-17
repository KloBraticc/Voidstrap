using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Voidstrap.UI.Chat
{
    public class MenuContainerViewModel : ObservableObject
    {
        private double _brightness = App.Settings.Prop.Brightness;
        private double _saturation = App.Settings.Prop.Saturation;
        private double _contrast = App.Settings.Prop.Contrast;
        private double _colorTemperature = App.Settings.Prop.ColorTemperature;

        public double Brightness
        {
            get => _brightness;
            set
            {
                double clamped = Math.Clamp(value, 0, 100);
                if (SetProperty(ref _brightness, clamped))
                {
                    App.Settings.Prop.Brightness = clamped;
                    App.Settings.Save();
                    OnPropertyChanged(nameof(BrightnessDisplay));
                }
            }
        }

        public string BrightnessDisplay =>
            Brightness == 50
                ? "Disabled"
                : $"{Brightness:0}";

        public double Saturation
        {
            get => _saturation;
            set
            {
                double clamped = Math.Clamp(value, 0, 200);
                if (SetProperty(ref _saturation, clamped))
                {
                    App.Settings.Prop.Saturation = clamped;
                    App.Settings.Save();
                    OnPropertyChanged(nameof(SaturationDisplay));
                }
            }
        }

        public string SaturationDisplay =>
            Saturation == 100
                ? "Disabled"
                : $"{Saturation:0}";

        public double Contrast
        {
            get => _contrast;
            set
            {
                double clamped = Math.Clamp(value, 0, 200);
                if (SetProperty(ref _contrast, clamped))
                {
                    App.Settings.Prop.Contrast = clamped;
                    App.Settings.Save();
                    OnPropertyChanged(nameof(ContrastDisplay));
                }
            }
        }

        public string ContrastDisplay =>
            Contrast == 100
                ? "Disabled"
                : $"{Contrast:0}";

        public double ColorTemperature
        {
            get => _colorTemperature;
            set
            {
                double clamped = Math.Clamp(value, -100, 100);
                if (SetProperty(ref _colorTemperature, clamped))
                {
                    App.Settings.Prop.ColorTemperature = clamped;
                    App.Settings.Save();
                    OnPropertyChanged(nameof(ColorTemperatureDisplay));
                }
            }
        }

        public string ColorTemperatureDisplay =>
            ColorTemperature == 0
                ? "Disabled"
                : $"{ColorTemperature:0}";
    }
}
