using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Xml.Linq;

using Wpf.Ui.Markup;

using Voidstrap.UI.Elements.Controls;
using Voidstrap;

namespace Voidstrap.UI.Elements.Bootstrapper
{
    public partial class CustomDialog
    {
        #region Transformation
        private static Transform HandleXmlElement_ScaleTransform(CustomDialog dialog, XElement xmlElement)
        {
            var st = new ScaleTransform();

            st.ScaleX = ParseXmlAttribute<double>(xmlElement, "ScaleX", 1);
            st.ScaleY = ParseXmlAttribute<double>(xmlElement, "ScaleY", 1);
            st.CenterX = ParseXmlAttribute<double>(xmlElement, "CenterX", 0);
            st.CenterY = ParseXmlAttribute<double>(xmlElement, "CenterY", 0);

            return st;
        }

        private static Transform HandleXmlElement_SkewTransform(CustomDialog dialog, XElement xmlElement)
        {
            var st = new SkewTransform();

            st.AngleX = ParseXmlAttribute<double>(xmlElement, "AngleX", 0);
            st.AngleY = ParseXmlAttribute<double>(xmlElement, "AngleY", 0);
            st.CenterX = ParseXmlAttribute<double>(xmlElement, "CenterX", 0);
            st.CenterY = ParseXmlAttribute<double>(xmlElement, "CenterY", 0);

            return st;
        }

        private static Transform HandleXmlElement_RotateTransform(CustomDialog dialog, XElement xmlElement)
        {
            var rt = new RotateTransform();

            rt.Angle = ParseXmlAttribute<double>(xmlElement, "Angle", 0);
            rt.CenterX = ParseXmlAttribute<double>(xmlElement, "CenterX", 0);
            rt.CenterY = ParseXmlAttribute<double>(xmlElement, "CenterY", 0);

            return rt;
        }

        private static Transform HandleXmlElement_TranslateTransform(CustomDialog dialog, XElement xmlElement)
        {
            var tt = new TranslateTransform();

            tt.X = ParseXmlAttribute<double>(xmlElement, "X", 0);
            tt.Y = ParseXmlAttribute<double>(xmlElement, "Y", 0);

            return tt;
        }
        #endregion

        #region Effects
        private static BlurEffect HandleXmlElement_BlurEffect(CustomDialog dialog, XElement xmlElement)
        {
            var effect = new BlurEffect();

            effect.KernelType = ParseXmlAttribute<KernelType>(xmlElement, "KernelType", KernelType.Gaussian);
            effect.Radius = ParseXmlAttribute<double>(xmlElement, "Radius", 5);
            effect.RenderingBias = ParseXmlAttribute<RenderingBias>(xmlElement, "RenderingBias", RenderingBias.Performance);

            return effect;
        }

        private static DropShadowEffect HandleXmlElement_DropShadowEffect(CustomDialog dialog, XElement xmlElement)
        {
            var effect = new DropShadowEffect();

            effect.BlurRadius = ParseXmlAttribute<double>(xmlElement, "BlurRadius", 5);
            effect.Direction = ParseXmlAttribute<double>(xmlElement, "Direction", 315);
            effect.Opacity = ParseXmlAttribute<double>(xmlElement, "Opacity", 1);
            effect.ShadowDepth = ParseXmlAttribute<double>(xmlElement, "ShadowDepth", 5);
            effect.RenderingBias = ParseXmlAttribute<RenderingBias>(xmlElement, "RenderingBias", RenderingBias.Performance);

            var color = GetColorFromXElement(xmlElement, "Color");
            if (color is Color)
                effect.Color = (Color)color;

            return effect;
        }
        #endregion

        #region Brushes
        private static void HandleXml_Brush(Brush brush, XElement xmlElement)
        {
            brush.Opacity = ParseXmlAttribute<double>(xmlElement, "Opacity", 1.0);
        }

        private static Brush HandleXmlElement_SolidColorBrush(CustomDialog dialog, XElement xmlElement)
        {
            var brush = new SolidColorBrush();
            HandleXml_Brush(brush, xmlElement);

            object? color = GetColorFromXElement(xmlElement, "Color");
            if (color is Color)
                brush.Color = (Color)color;

            return brush;
        }

        private static Brush HandleXmlElement_ImageBrush(CustomDialog dialog, XElement xmlElement)
        {
            var imageBrush = new ImageBrush();
            HandleXml_Brush(imageBrush, xmlElement);

            imageBrush.AlignmentX = ParseXmlAttribute<AlignmentX>(xmlElement, "AlignmentX", AlignmentX.Center);
            imageBrush.AlignmentY = ParseXmlAttribute<AlignmentY>(xmlElement, "AlignmentY", AlignmentY.Center);

            imageBrush.Stretch = ParseXmlAttribute<Stretch>(xmlElement, "Stretch", Stretch.Fill);
            imageBrush.TileMode = ParseXmlAttribute<TileMode>(xmlElement, "TileMode", TileMode.None);

            imageBrush.ViewboxUnits = ParseXmlAttribute<BrushMappingMode>(xmlElement, "ViewboxUnits", BrushMappingMode.RelativeToBoundingBox);
            imageBrush.ViewportUnits = ParseXmlAttribute<BrushMappingMode>(xmlElement, "ViewportUnits", BrushMappingMode.RelativeToBoundingBox);

            var viewbox = GetRectFromXElement(xmlElement, "Viewbox");
            if (viewbox is Rect)
                imageBrush.Viewbox = (Rect)viewbox;

            var viewport = GetRectFromXElement(xmlElement, "Viewport");
            if (viewport is Rect)
                imageBrush.Viewport = (Rect)viewport;

            var sourceData = GetImageSourceData(dialog, "ImageSource", xmlElement);

            if (sourceData.IsIcon)
            {
                // bind the icon property
                Binding binding = new Binding("Icon") { Mode = BindingMode.OneWay };
                BindingOperations.SetBinding(imageBrush, ImageBrush.ImageSourceProperty, binding);
            }
            else
            {
                BitmapSource? bitmapImage;
                try
                {
                    bitmapImage = Voidstrap.Utility.SafeImaging.FromUri(sourceData.Uri!);
                }
                catch (Exception ex)
                {
                    throw new CustomThemeException(ex, "CustomTheme.Errors.ElementTypeCreationFailed", "Image", "BitmapImage", ex.Message);
                }

                imageBrush.ImageSource = bitmapImage;
            }

            return imageBrush;
        }

        private static GradientStop HandleXmlElement_GradientStop(CustomDialog dialog, XElement xmlElement)
        {
            var gs = new GradientStop();

            object? color = GetColorFromXElement(xmlElement, "Color");
            if (color is Color)
                gs.Color = (Color)color;

            gs.Offset = ParseXmlAttribute<double>(xmlElement, "Offset", 0.0);

            return gs;
        }

        private static Brush HandleXmlElement_LinearGradientBrush(CustomDialog dialog, XElement xmlElement)
        {
            var brush = new LinearGradientBrush();
            HandleXml_Brush(brush, xmlElement);

            object? startPoint = GetPointFromXElement(xmlElement, "StartPoint");
            if (startPoint is Point)
                brush.StartPoint = (Point)startPoint;

            object? endPoint = GetPointFromXElement(xmlElement, "EndPoint");
            if (endPoint is Point)
                brush.EndPoint = (Point)endPoint;

            brush.ColorInterpolationMode = ParseXmlAttribute<ColorInterpolationMode>(xmlElement, "ColorInterpolationMode", ColorInterpolationMode.SRgbLinearInterpolation);
            brush.MappingMode = ParseXmlAttribute<BrushMappingMode>(xmlElement, "MappingMode", BrushMappingMode.RelativeToBoundingBox);
            brush.SpreadMethod = ParseXmlAttribute<GradientSpreadMethod>(xmlElement, "SpreadMethod", GradientSpreadMethod.Pad);

            foreach (var child in xmlElement.Elements())
                brush.GradientStops.Add(HandleXml<GradientStop>(dialog, child));

            return brush;
        }

        private static void ApplyBrush_UIElement(CustomDialog dialog, FrameworkElement uiElement, string name, DependencyProperty dependencyProperty, XElement xmlElement)
        {
            // check if attribute exists
            object? brushAttr = GetBrushFromXElement(xmlElement, name);
            if (brushAttr is Brush)
            {
                uiElement.SetValue(dependencyProperty, brushAttr);
                return;
            }
            else if (brushAttr is string)
            {
                uiElement.SetResourceReference(dependencyProperty, brushAttr);
                return;
            }

            // check if element exists
            var brushElement = xmlElement.Element($"{xmlElement.Name}.{name}");
            if (brushElement == null)
                return;

            var first = brushElement.FirstNode as XElement;
            if (first == null)
                throw new CustomThemeException("CustomTheme.Errors.ElementAttributeMissingChild", xmlElement.Name, name);

            var brush = HandleXml<Brush>(dialog, first);
            uiElement.SetValue(dependencyProperty, brush);
        }
        #endregion

        #region Shapes
        private static void HandleXmlElement_Shape(CustomDialog dialog, Shape shape, XElement xmlElement)
        {
            HandleXmlElement_FrameworkElement(dialog, shape, xmlElement);

            ApplyBrush_UIElement(dialog, shape, "Fill", Shape.FillProperty, xmlElement);
            ApplyBrush_UIElement(dialog, shape, "Stroke", Shape.StrokeProperty, xmlElement);

            shape.Stretch = ParseXmlAttribute<Stretch>(xmlElement, "Stretch", Stretch.Fill);

            shape.StrokeDashCap = ParseXmlAttribute<PenLineCap>(xmlElement, "StrokeDashCap", PenLineCap.Flat);
            shape.StrokeDashOffset = ParseXmlAttribute<double>(xmlElement, "StrokeDashOffset", 0);
            shape.StrokeEndLineCap = ParseXmlAttribute<PenLineCap>(xmlElement, "StrokeEndLineCap", PenLineCap.Flat);
            shape.StrokeLineJoin = ParseXmlAttribute<PenLineJoin>(xmlElement, "StrokeLineJoin", PenLineJoin.Miter);
            shape.StrokeMiterLimit = ParseXmlAttribute<double>(xmlElement, "StrokeMiterLimit", 10);
            shape.StrokeStartLineCap = ParseXmlAttribute<PenLineCap>(xmlElement, "StrokeStartLineCap", PenLineCap.Flat);
            shape.StrokeThickness = ParseXmlAttribute<double>(xmlElement, "StrokeThickness", 1);
        }

        private static Ellipse HandleXmlElement_Ellipse(CustomDialog dialog, XElement xmlElement)
        {
            var ellipse = new Ellipse();
            HandleXmlElement_Shape(dialog, ellipse, xmlElement);

            return ellipse;
        }

        private static Line HandleXmlElement_Line(CustomDialog dialog, XElement xmlElement)
        {
            var line = new Line();
            HandleXmlElement_Shape(dialog, line, xmlElement);

            line.X1 = ParseXmlAttribute<double>(xmlElement, "X1", 0);
            line.X2 = ParseXmlAttribute<double>(xmlElement, "X2", 0);
            line.Y1 = ParseXmlAttribute<double>(xmlElement, "Y1", 0);
            line.Y2 = ParseXmlAttribute<double>(xmlElement, "Y2", 0);

            return line;
        }

        private static Rectangle HandleXmlElement_Rectangle(CustomDialog dialog, XElement xmlElement)
        {
            var rectangle = new Rectangle();
            HandleXmlElement_Shape(dialog, rectangle, xmlElement);

            rectangle.RadiusX = ParseXmlAttribute<double>(xmlElement, "RadiusX", 0);
            rectangle.RadiusY = ParseXmlAttribute<double>(xmlElement, "RadiusY", 0);

            return rectangle;
        }

        #endregion

        #region Elements
        private static void HandleXmlElement_FrameworkElement(CustomDialog dialog, FrameworkElement uiElement, XElement xmlElement)
        {
            // prevent two elements from having the same name
            string? name = xmlElement.Attribute("Name")?.Value?.ToString();
            if (name != null)
            {
                if (dialog.UsedNames.Contains(name))
                    throw new Exception($"{xmlElement.Name} has duplicate name {name}");

                dialog.UsedNames.Add(name);
            }

            uiElement.Name = name;

            uiElement.Visibility = ParseXmlAttribute<Visibility>(xmlElement, "Visibility", Visibility.Visible);
            uiElement.IsEnabled = ParseXmlAttribute<bool>(xmlElement, "IsEnabled", true);

            object? margin = GetThicknessFromXElement(xmlElement, "Margin");
            if (margin != null)
                uiElement.Margin = (Thickness)margin;

            uiElement.Height = ParseXmlAttribute<double>(xmlElement, "Height", double.NaN);
            uiElement.Width = ParseXmlAttribute<double>(xmlElement, "Width", double.NaN);

            uiElement.HorizontalAlignment = ParseXmlAttribute<HorizontalAlignment>(xmlElement, "HorizontalAlignment", HorizontalAlignment.Stretch);
            uiElement.VerticalAlignment = ParseXmlAttribute<VerticalAlignment>(xmlElement, "VerticalAlignment", VerticalAlignment.Stretch);

            uiElement.Opacity = ParseXmlAttribute<double>(xmlElement, "Opacity", 1);
            ApplyBrush_UIElement(dialog, uiElement, "OpacityMask", FrameworkElement.OpacityMaskProperty, xmlElement);

            object? renderTransformOrigin = GetPointFromXElement(xmlElement, "RenderTransformOrigin");
            if (renderTransformOrigin is Point)
                uiElement.RenderTransformOrigin = (Point)renderTransformOrigin;

            int zIndex = ParseXmlAttributeClamped(xmlElement, "Panel.ZIndex", defaultValue: 0, min: 0, max: 1000);
            Panel.SetZIndex(uiElement, zIndex);

            int gridRow = ParseXmlAttribute<int>(xmlElement, "Grid.Row", 0);
            Grid.SetRow(uiElement, gridRow);
            int gridRowSpan = ParseXmlAttribute<int>(xmlElement, "Grid.RowSpan", 1);
            Grid.SetRowSpan(uiElement, gridRowSpan);

            int gridColumn = ParseXmlAttribute<int>(xmlElement, "Grid.Column", 0);
            Grid.SetColumn(uiElement, gridColumn);
            int gridColumnSpan = ParseXmlAttribute<int>(xmlElement, "Grid.ColumnSpan", 1);
            Grid.SetColumnSpan(uiElement, gridColumnSpan);

            ApplyTransformations_UIElement(dialog, uiElement, xmlElement);
            ApplyEffects_UIElement(dialog, uiElement, xmlElement);
        }

        private static void HandleXmlElement_Control(CustomDialog dialog, Control uiElement, XElement xmlElement)
        {
            HandleXmlElement_FrameworkElement(dialog, uiElement, xmlElement);

            object? padding = GetThicknessFromXElement(xmlElement, "Padding");
            if (padding != null)
                uiElement.Padding = (Thickness)padding;

            object? borderThickness = GetThicknessFromXElement(xmlElement, "BorderThickness");
            if (borderThickness != null)
                uiElement.BorderThickness = (Thickness)borderThickness;

            ApplyBrush_UIElement(dialog, uiElement, "Foreground", Control.ForegroundProperty, xmlElement);

            ApplyBrush_UIElement(dialog, uiElement, "Background", Control.BackgroundProperty, xmlElement);

            ApplyBrush_UIElement(dialog, uiElement, "BorderBrush", Control.BorderBrushProperty, xmlElement);

            var fontSize = ParseXmlAttributeNullable<double>(xmlElement, "FontSize");
            if (fontSize is double)
                uiElement.FontSize = (double)fontSize;
            uiElement.FontWeight = GetFontWeightFromXElement(xmlElement);
            uiElement.FontStyle = GetFontStyleFromXElement(xmlElement);

            // NOTE: font family can both be the name of the font or a uri
            var fontFamily = ResolveFontFamily(dialog, xmlElement.Attribute("FontFamily")?.Value, xmlElement);
            if (fontFamily != null)
                uiElement.FontFamily = fontFamily;
        }

        private static Voidstrap.Models.BackdropType ResolveBackdropAttribute(XElement xmlElement)
        {
            var backdrop = ParseXmlAttributeNullable<Voidstrap.Models.BackdropType>(xmlElement, "Backdrop");
            if (backdrop != null)
                return backdrop.Value;

            var legacy = ParseXmlAttributeNullable<Wpf.Ui.Appearance.BackgroundType>(xmlElement, "WindowBackdropType");
            if (legacy == null)
                return Voidstrap.Models.BackdropType.None;

            return legacy.Value switch
            {
                Wpf.Ui.Appearance.BackgroundType.Aero => Voidstrap.Models.BackdropType.Aero,
                Wpf.Ui.Appearance.BackgroundType.Mica => Voidstrap.Models.BackdropType.Mica,
                Wpf.Ui.Appearance.BackgroundType.Acrylic => Voidstrap.Models.BackdropType.Acrylic,
                Wpf.Ui.Appearance.BackgroundType.Tabbed => Voidstrap.Models.BackdropType.MicaAlt,
                Wpf.Ui.Appearance.BackgroundType.Auto => Voidstrap.Models.BackdropType.Default,
                _ => Voidstrap.Models.BackdropType.None
            };
        }

        private static UIElement HandleXmlElement_BloxstrapCustomBootstrapper(CustomDialog dialog, XElement xmlElement)
        {
            xmlElement.SetAttributeValue("Visibility", "Collapsed"); // don't show the bootstrapper yet!!!
            xmlElement.SetAttributeValue("IsEnabled", "True");
            HandleXmlElement_Control(dialog, dialog, xmlElement);

            dialog.Opacity = 1;

            // transfer effect to element grid
            dialog.ElementGrid.RenderTransform = dialog.RenderTransform;
            dialog.RenderTransform = null;
            dialog.ElementGrid.LayoutTransform = dialog.LayoutTransform;
            dialog.LayoutTransform = null;

            dialog.ElementGrid.Effect = dialog.Effect;
            dialog.Effect = null;

            var theme = ParseXmlAttribute<Theme>(xmlElement, "Theme", Theme.Default);
            if (theme == Theme.Default)
                theme = App.Settings.Prop.Theme2;

            var wpfUiTheme = theme.GetFinal() == Theme.Light ? Wpf.Ui.Appearance.ThemeType.Light : Wpf.Ui.Appearance.ThemeType.Dark;

            for (int i = dialog.Resources.MergedDictionaries.Count - 1; i >= 0; i--)
            {
                if (dialog.Resources.MergedDictionaries[i] is ThemesDictionary || dialog.Resources.MergedDictionaries[i] is ControlsDictionary)
                    dialog.Resources.MergedDictionaries.RemoveAt(i);
            }

            dialog.Resources.MergedDictionaries.Insert(0, new ThemesDictionary() { Theme = wpfUiTheme });
            dialog.Resources.MergedDictionaries.Insert(1, new ControlsDictionary());

            CopySystemAccent(dialog);
            dialog.DefaultBorderThemeOverwrite = wpfUiTheme;

            dialog.WindowCornerPreference = ParseXmlAttribute<Wpf.Ui.Appearance.WindowCornerPreference>(xmlElement, "WindowCornerPreference", Wpf.Ui.Appearance.WindowCornerPreference.Round);

            bool glass = ParseXmlAttribute<bool>(xmlElement, "Glass", true);

            Voidstrap.Models.BackdropType backdrop = ResolveBackdropAttribute(xmlElement);

            bool opaque = !glass || backdrop == Voidstrap.Models.BackdropType.None;

            Voidstrap.UI.WindowBackdrop.SetOverride(dialog, opaque ? null : backdrop, opaque);

            // disable default window border if border is modified
            if (xmlElement.Attribute("BorderBrush") != null || xmlElement.Attribute("BorderThickness") != null)
                dialog.DefaultBorderEnabled = false;

            // set the margin & padding on the element grid
            dialog.ElementGrid.Margin = dialog.Margin;
            dialog.Margin = new Thickness(0, 0, 0, 0);
            dialog.Padding = new Thickness(0, 0, 0, 0);

            string? title = xmlElement.Attribute("Title")?.Value?.ToString() ?? "Voidstrap";
            dialog.Title = title;

            bool ignoreTitleBarInset = ParseXmlAttribute<bool>(xmlElement, "IgnoreTitleBarInset", false);
            if (ignoreTitleBarInset)
            {
                Grid.SetRow(dialog.ElementGrid, 0);
                Grid.SetRowSpan(dialog.ElementGrid, 2);
            }

            return new DummyFrameworkElement();
        }

        private static void CopySystemAccent(CustomDialog dialog)
        {
            if (Application.Current == null)
                return;

            string[] keys =
            {
                "SystemAccentColor",
                "SystemAccentColorPrimary",
                "SystemAccentColorSecondary",
                "SystemAccentColorTertiary"
            };

            foreach (string key in keys)
            {
                try
                {
                    if (Application.Current.Resources[key] is Color color)
                        dialog.Resources[key] = color;
                }
                catch (Exception ex)
                {
                    App.Logger?.WriteLine("CustomDialog::CopySystemAccent", "Could not copy " + key + ": " + ex.Message);
                }
            }
        }

        private static UIElement HandleXmlElement_BloxstrapCustomBootstrapper_Fake(CustomDialog dialog, XElement xmlElement)
        {
            // this only exists to error out the theme if someone tries to use two BloxstrapCustomBootstrappers
            throw new Exception($"{xmlElement.Parent!.Name} cannot have a child of {xmlElement.Name}");
        }

        private static DummyFrameworkElement HandleXmlElement_TitleBar(CustomDialog dialog, XElement xmlElement)
        {
            xmlElement.SetAttributeValue("Name", "TitleBar"); // prevent two titlebars from existing
            xmlElement.SetAttributeValue("IsEnabled", "True");
            HandleXmlElement_Control(dialog, dialog.RootTitleBar, xmlElement);

            // get rid of all effects
            dialog.RootTitleBar.RenderTransform = null;
            dialog.RootTitleBar.LayoutTransform = null;

            dialog.RootTitleBar.Effect = null;

            Panel.SetZIndex(dialog.RootTitleBar, 1001); // always show above others

            // properties we dont want modifiable
            dialog.RootTitleBar.Height = double.NaN;
            dialog.RootTitleBar.Width = double.NaN;
            dialog.RootTitleBar.HorizontalAlignment = HorizontalAlignment.Stretch;
            dialog.RootTitleBar.Margin = new Thickness(0, 0, 0, 0);

            dialog.RootTitleBar.ShowMinimize = ParseXmlAttribute<bool>(xmlElement, "ShowMinimize", true);
            dialog.RootTitleBar.ShowClose = ParseXmlAttribute<bool>(xmlElement, "ShowClose", true);

            string? title = xmlElement.Attribute("Title")?.Value?.ToString() ?? "Voidstrap";
            dialog.RootTitleBar.Title = title;

            return new DummyFrameworkElement(); // dont add anything
        }

        private static UIElement HandleXmlElement_Button(CustomDialog dialog, XElement xmlElement)
        {
            var button = new Button();
            HandleXmlElement_Control(dialog, button, xmlElement);

            button.Content = GetContentFromXElement(dialog, xmlElement);

            if (xmlElement.Attribute("Name")?.Value == "CancelButton")
            {
                Binding cancelEnabledBinding = new Binding("CancelEnabled") { Mode = BindingMode.OneWay };
                BindingOperations.SetBinding(button, Button.IsEnabledProperty, cancelEnabledBinding);

                Binding cancelCommandBinding = new Binding("CancelInstallCommand");
                BindingOperations.SetBinding(button, Button.CommandProperty, cancelCommandBinding);
            }

            return button;
        }

        private static void HandleXmlElement_RangeBase(CustomDialog dialog, RangeBase rangeBase, XElement xmlElement)
        {
            HandleXmlElement_Control(dialog, rangeBase, xmlElement);

            rangeBase.Value = ParseXmlAttribute<double>(xmlElement, "Value", 0);
            rangeBase.Maximum = ParseXmlAttribute<double>(xmlElement, "Maximum", 100);
        }

        private static UIElement HandleXmlElement_ProgressBar(CustomDialog dialog, XElement xmlElement)
        {
            var progressBar = new Wpf.Ui.Controls.ProgressBar();
            ProgressBarStyler.Apply(progressBar);
            HandleXmlElement_RangeBase(dialog, progressBar, xmlElement);

            progressBar.IsIndeterminate = ParseXmlAttribute<bool>(xmlElement, "IsIndeterminate", false);

            object? cornerRadius = GetCornerRadiusFromXElement(xmlElement, "CornerRadius");
            if (cornerRadius != null)
                progressBar.CornerRadius = (CornerRadius)cornerRadius;

            object? indicatorCornerRadius = GetCornerRadiusFromXElement(xmlElement, "IndicatorCornerRadius");
            if (indicatorCornerRadius != null)
                progressBar.IndicatorCornerRadius = (CornerRadius)indicatorCornerRadius;

            if (xmlElement.Attribute("Name")?.Value == "PrimaryProgressBar")
            {
                Binding isIndeterminateBinding = new Binding("ProgressIndeterminate") { Mode = BindingMode.OneWay };
                BindingOperations.SetBinding(progressBar, ProgressBar.IsIndeterminateProperty, isIndeterminateBinding);

                Binding maximumBinding = new Binding("ProgressMaximum") { Mode = BindingMode.OneWay };
                BindingOperations.SetBinding(progressBar, ProgressBar.MaximumProperty, maximumBinding);

                Binding valueBinding = new Binding("ProgressValue") { Mode = BindingMode.OneWay };
                BindingOperations.SetBinding(progressBar, ProgressBar.ValueProperty, valueBinding);
            }

            return progressBar;
        }

        private static UIElement HandleXmlElement_ProgressRing(CustomDialog dialog, XElement xmlElement)
        {
            var progressBar = new Wpf.Ui.Controls.ProgressRing();
            HandleXmlElement_RangeBase(dialog, progressBar, xmlElement);

            progressBar.IsIndeterminate = ParseXmlAttribute<bool>(xmlElement, "IsIndeterminate", false);

            if (xmlElement.Attribute("Name")?.Value == "PrimaryProgressRing")
            {
                Binding isIndeterminateBinding = new Binding("ProgressIndeterminate") { Mode = BindingMode.OneWay };
                BindingOperations.SetBinding(progressBar, Wpf.Ui.Controls.ProgressRing.IsIndeterminateProperty, isIndeterminateBinding);

                Binding maximumBinding = new Binding("ProgressMaximum") { Mode = BindingMode.OneWay };
                BindingOperations.SetBinding(progressBar, Wpf.Ui.Controls.ProgressRing.MaximumProperty, maximumBinding);

                Binding valueBinding = new Binding("ProgressValue") { Mode = BindingMode.OneWay };
                BindingOperations.SetBinding(progressBar, Wpf.Ui.Controls.ProgressRing.ValueProperty, valueBinding);
            }

            return progressBar;
        }

        private static void HandleXmlElement_TextBlock_Base(CustomDialog dialog, TextBlock textBlock, XElement xmlElement)
        {
            HandleXmlElement_FrameworkElement(dialog, textBlock, xmlElement);

            ApplyBrush_UIElement(dialog, textBlock, "Foreground", TextBlock.ForegroundProperty, xmlElement);

            ApplyBrush_UIElement(dialog, textBlock, "Background", TextBlock.BackgroundProperty, xmlElement);

            var fontSize = ParseXmlAttributeNullable<double>(xmlElement, "FontSize");
            if (fontSize is double)
                textBlock.FontSize = (double)fontSize;
            textBlock.FontWeight = GetFontWeightFromXElement(xmlElement);
            textBlock.FontStyle = GetFontStyleFromXElement(xmlElement);

            textBlock.LineHeight = ParseXmlAttribute<double>(xmlElement, "LineHeight", double.NaN);
            textBlock.LineStackingStrategy = ParseXmlAttribute<LineStackingStrategy>(xmlElement, "LineStackingStrategy", LineStackingStrategy.MaxHeight);

            textBlock.TextAlignment = ParseXmlAttribute<TextAlignment>(xmlElement, "TextAlignment", TextAlignment.Center);
            textBlock.TextTrimming = ParseXmlAttribute<TextTrimming>(xmlElement, "TextTrimming", TextTrimming.None);
            textBlock.TextWrapping = ParseXmlAttribute<TextWrapping>(xmlElement, "TextWrapping", TextWrapping.NoWrap);
            textBlock.TextDecorations = GetTextDecorationsFromXElement(xmlElement);

            textBlock.IsHyphenationEnabled = ParseXmlAttribute<bool>(xmlElement, "IsHyphenationEnabled", false);
            textBlock.BaselineOffset = ParseXmlAttribute<double>(xmlElement, "BaselineOffset", double.NaN);

            // NOTE: font family can both be the name of the font or a uri
            var fontFamily = ResolveFontFamily(dialog, xmlElement.Attribute("FontFamily")?.Value, xmlElement);
            if (fontFamily != null)
                textBlock.FontFamily = fontFamily;

            object? padding = GetThicknessFromXElement(xmlElement, "Padding");
            if (padding != null)
                textBlock.Padding = (Thickness)padding;
        }

        private static UIElement HandleXmlElement_TextBlock(CustomDialog dialog, XElement xmlElement)
        {
            var textBlock = new TextBlock();
            HandleXmlElement_TextBlock_Base(dialog, textBlock, xmlElement);

            textBlock.Text = GetTranslatedText(xmlElement.Attribute("Text")?.Value);

            if (xmlElement.Attribute("Name")?.Value == "StatusText")
            {
                Binding textBinding = new Binding("Message") { Mode = BindingMode.OneWay };
                BindingOperations.SetBinding(textBlock, TextBlock.TextProperty, textBinding);
            }

            return textBlock;
        }

        private static UIElement HandleXmlElement_MarkdownTextBlock(CustomDialog dialog, XElement xmlElement)
        {
            var textBlock = new MarkdownTextBlock();
            HandleXmlElement_TextBlock_Base(dialog, textBlock, xmlElement);

            string? text = GetTranslatedText(xmlElement.Attribute("Text")?.Value);
            if (text != null)
                textBlock.MarkdownText = text;

            return textBlock;
        }

        private static UIElement HandleXmlElement_Image(CustomDialog dialog, XElement xmlElement)
        {
            var image = new Image();
            HandleXmlElement_FrameworkElement(dialog, image, xmlElement);

            image.Stretch = ParseXmlAttribute<Stretch>(xmlElement, "Stretch", Stretch.Uniform);
            image.StretchDirection = ParseXmlAttribute<StretchDirection>(xmlElement, "StretchDirection", StretchDirection.Both);

            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality); // should this be modifiable by the user?

            var sourceData = GetImageSourceData(dialog, "Source", xmlElement);

            if (sourceData.IsIcon)
            {
                // bind the icon property
                Binding binding = new Binding("Icon") { Mode = BindingMode.OneWay };
                BindingOperations.SetBinding(image, Image.SourceProperty, binding);
            }
            else
            {
                bool isAnimated = ParseXmlAttribute<bool>(xmlElement, "IsAnimated", false);
                if (!isAnimated)
                {
                    BitmapSource? bitmapImage;
                    try
                    {
                        bitmapImage = Voidstrap.Utility.SafeImaging.FromUri(sourceData.Uri!);
                    }
                    catch (Exception ex)
                    {
                        throw new CustomThemeException(ex, "CustomTheme.Errors.ElementTypeCreationFailed", "Image", "BitmapImage", ex.Message);
                    }

                    image.Source = bitmapImage;
                }
                else if (Voidstrap.Utility.Platform.IsWindows)
                {
                    XamlAnimatedGif.AnimationBehavior.SetSourceUri(image, sourceData.Uri!);
                }
                else
                {
                    image.Source = Voidstrap.Utility.SafeImaging.FromUri(sourceData.Uri!);
                }
            }

            return image;
        }

        private static RowDefinition HandleXmlElement_RowDefinition(CustomDialog dialog, XElement xmlElement)
        {
            var rowDefinition = new RowDefinition();

            var height = GetGridLengthFromXElement(xmlElement, "Height");
            if (height != null)
                rowDefinition.Height = (GridLength)height;

            rowDefinition.MinHeight = ParseXmlAttribute<double>(xmlElement, "MinHeight", 0);
            rowDefinition.MaxHeight = ParseXmlAttribute<double>(xmlElement, "MaxHeight", double.PositiveInfinity);

            return rowDefinition;
        }

        private static ColumnDefinition HandleXmlElement_ColumnDefinition(CustomDialog dialog, XElement xmlElement)
        {
            var columnDefinition = new ColumnDefinition();

            var width = GetGridLengthFromXElement(xmlElement, "Width");
            if (width != null)
                columnDefinition.Width = (GridLength)width;

            columnDefinition.MinWidth = ParseXmlAttribute<double>(xmlElement, "MinWidth", 0);
            columnDefinition.MaxWidth = ParseXmlAttribute<double>(xmlElement, "MaxWidth", double.PositiveInfinity);

            return columnDefinition;
        }

        private static void HandleXmlElement_Grid_RowDefinitions(Grid grid, CustomDialog dialog, XElement xmlElement)
        {
            foreach (var element in xmlElement.Elements())
            {
                var rowDefinition = HandleXml<RowDefinition>(dialog, element);
                grid.RowDefinitions.Add(rowDefinition);
            }
        }

        private static void HandleXmlElement_Grid_ColumnDefinitions(Grid grid, CustomDialog dialog, XElement xmlElement)
        {
            foreach (var element in xmlElement.Elements())
            {
                var columnDefinition = HandleXml<ColumnDefinition>(dialog, element);
                grid.ColumnDefinitions.Add(columnDefinition);
            }
        }

        private static Grid HandleXmlElement_Grid(CustomDialog dialog, XElement xmlElement)
        {
            var grid = new Grid();
            HandleXmlElement_FrameworkElement(dialog, grid, xmlElement);

            bool rowsSet = false;
            bool columnsSet = false;

            foreach (var element in xmlElement.Elements())
            {
                if (element.Name == "Grid.RowDefinitions")
                {
                    if (rowsSet)
                        throw new CustomThemeException("CustomTheme.Errors.ElementAttributeMultipleDefinitions", "Grid", "RowDefinitions");
                    rowsSet = true;

                    HandleXmlElement_Grid_RowDefinitions(grid, dialog, element);
                }
                else if (element.Name == "Grid.ColumnDefinitions")
                {
                    if (columnsSet)
                        throw new CustomThemeException("CustomTheme.Errors.ElementAttributeMultipleDefinitions", "Grid", "ColumnDefinitions");
                    columnsSet = true;

                    HandleXmlElement_Grid_ColumnDefinitions(grid, dialog, element);
                }
                else if (element.Name.ToString().StartsWith("Grid."))
                {
                    continue; // ignore others
                }
                else
                {
                    var uiElement = HandleXml<FrameworkElement>(dialog, element);
                    grid.Children.Add(uiElement);
                }
            }

            return grid;
        }

        private static StackPanel HandleXmlElement_StackPanel(CustomDialog dialog, XElement xmlElement)
        {
            var stackPanel = new StackPanel();
            HandleXmlElement_FrameworkElement(dialog, stackPanel, xmlElement);

            stackPanel.Orientation = ParseXmlAttribute<Orientation>(xmlElement, "Orientation", Orientation.Vertical);

            foreach (var element in xmlElement.Elements())
            {
                var uiElement = HandleXml<FrameworkElement>(dialog, element);
                stackPanel.Children.Add(uiElement);
            }

            return stackPanel;
        }

        private const string WebPanelHostSuffix = ".theme.voidstrap.invalid";

        private const string WebPanelBrowserArgs =
            "--disable-background-networking --disable-sync --disable-extensions "
            + "--disable-component-update --no-first-run --no-default-browser-check "
            + "--disable-features=OptimizationHints,Translate,MediaRouter";

        private static Task<Microsoft.Web.WebView2.Core.CoreWebView2Environment>? _webPanelEnvironment;
        private static readonly object _webPanelEnvironmentLock = new();

        internal static Task<Microsoft.Web.WebView2.Core.CoreWebView2Environment> GetWebPanelEnvironment()
        {
            lock (_webPanelEnvironmentLock)
            {
                if (_webPanelEnvironment == null || _webPanelEnvironment.IsFaulted || _webPanelEnvironment.IsCanceled)
                {
                    _webPanelEnvironment = Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
                        null,
                        Paths.WebViewData,
                        new Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions(Voidstrap.Utility.RenderAcceleration.ApplyToBrowserArguments(WebPanelBrowserArgs)));
                }

                return _webPanelEnvironment;
            }
        }

        private static UIElement HandleXmlElement_WebPanel(CustomDialog dialog, XElement xmlElement)
        {
            var host = new Border();
            HandleXmlElement_FrameworkElement(dialog, host, xmlElement);

            ApplyBrush_UIElement(dialog, host, "Background", Border.BackgroundProperty, xmlElement);

            object? cornerRadius = GetCornerRadiusFromXElement(xmlElement, "CornerRadius");
            if (cornerRadius != null)
                host.CornerRadius = (CornerRadius)cornerRadius;

            string declaredSource = GetXmlAttribute(xmlElement, "Source");

            if (!declaredSource.StartsWith("theme://", StringComparison.OrdinalIgnoreCase))
                throw new CustomThemeException("CustomTheme.Errors.ElementAttributeParseError", xmlElement.Name, "Source", "theme Uri");

            string? source = GetFullPath(dialog, declaredSource);

            if (string.IsNullOrEmpty(source) || !System.IO.File.Exists(source))
                throw new CustomThemeException("CustomTheme.Errors.ElementAttributeParseError", xmlElement.Name, "Source", "Uri");

            string extension = System.IO.Path.GetExtension(source).ToLowerInvariant();
            if (extension != ".html" && extension != ".htm")
                throw new CustomThemeException("CustomTheme.Errors.ElementAttributeParseError", xmlElement.Name, "Source", "Uri");

            var view = new Microsoft.Web.WebView2.Wpf.WebView2();
            view.DefaultBackgroundColor = System.Drawing.Color.Transparent;
            host.Child = view;
            host.ClipToBounds = true;

            if (ParseXmlAttribute<bool>(xmlElement, "CanDrag", true))
                dialog.AllowWebPanelDrag = true;

            InitialiseWebPanel(dialog, view, System.IO.Path.GetDirectoryName(source)!, System.IO.Path.GetFileName(source));

            return host;
        }

        private const string WebPanelApi = """
            window.voidstrap = (function () {
                var state = { status: "", progress: 0, max: 100, indeterminate: true, cancelEnabled: false };
                var listeners = [];
                function emit() {
                    var snapshot = {
                        status: state.status,
                        progress: state.progress,
                        max: state.max,
                        indeterminate: state.indeterminate,
                        cancelEnabled: state.cancelEnabled,
                        percent: state.max > 0 ? Math.max(0, Math.min(100, (state.progress / state.max) * 100)) : 0
                    };
                    for (var i = 0; i < listeners.length; i++) {
                        try { listeners[i](snapshot); } catch (e) {}
                    }
                    try {
                        window.dispatchEvent(new CustomEvent("voidstrap", { detail: snapshot }));
                    } catch (e) {}
                }
                return {
                    get status() { return state.status; },
                    get progress() { return state.progress; },
                    get max() { return state.max; },
                    get indeterminate() { return state.indeterminate; },
                    get cancelEnabled() { return state.cancelEnabled; },
                    get percent() { return state.max > 0 ? Math.max(0, Math.min(100, (state.progress / state.max) * 100)) : 0; },
                    get accent() { return window.__vsAccentColor || "#0078d4"; },
                    onUpdate: function (fn) { if (typeof fn === "function") { listeners.push(fn); fn(this); } },
                    cancel: function () {
                        try { window.chrome.webview.postMessage("voidstrap:cancel"); } catch (e) {}
                    },
                    drag: function () {
                        try { window.chrome.webview.postMessage("voidstrap:drag"); } catch (e) {}
                    },
                    __apply: function (next) {
                        if (!next) return;
                        if (typeof next.status === "string") state.status = next.status;
                        if (typeof next.progress === "number") state.progress = next.progress;
                        if (typeof next.max === "number") state.max = next.max;
                        if (typeof next.indeterminate === "boolean") state.indeterminate = next.indeterminate;
                        if (typeof next.cancelEnabled === "boolean") state.cancelEnabled = next.cancelEnabled;
                        emit();
                    }
                };
            })();

            (function () {
                var accent = window.__vsAccentColor || "#0078d4";
                var style = document.createElement("style");
                style.textContent = ":root{--vs-accent:" + accent + "}progress{accent-color:var(--vs-accent)}progress::-webkit-progress-value{background:var(--vs-accent)}";
                (document.head || document.documentElement).appendChild(style);
            })();

            document.addEventListener("mousedown", function (e) {
                if (e.button !== 0) return;

                var node = e.target;
                while (node && node !== document.documentElement) {
                    if (node.matches && node.matches("a,button,input,select,textarea,label,[contenteditable],[data-no-drag]")) return;
                    node = node.parentElement;
                }

                window.voidstrap.drag();
            }, true);
            """;

        private static string GetWindowsAccentColorHex()
        {
            try
            {
                using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
                if (key?.GetValue("AccentColor") is int abgr)
                {
                    byte r = (byte)(abgr & 0xFF);
                    byte g = (byte)((abgr >> 8) & 0xFF);
                    byte b = (byte)((abgr >> 16) & 0xFF);
                    return $"#{r:X2}{g:X2}{b:X2}";
                }
            }
            catch
            {
            }
            try
            {
                System.Windows.Media.Color c = Voidstrap.Utility.SystemAccent.GetGlassColor();
                return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            }
            catch
            {
            }
            return "#0078d4";
        }

        private static async void InitialiseWebPanel(CustomDialog dialog, Microsoft.Web.WebView2.Wpf.WebView2 view, string folder, string file)
        {
            CancellationToken token = dialog.WebPanelLifetimeToken;
            view.Tag = dialog;
            view.Unloaded += WebPanel_Unloaded;

            try
            {
                var environment = await GetWebPanelEnvironment().ConfigureAwait(true);
                if (token.IsCancellationRequested || dialog.WebPanelLifetimeEnded)
                    return;

                await view.EnsureCoreWebView2Async(environment);
                if (token.IsCancellationRequested || dialog.WebPanelLifetimeEnded)
                    return;

                var core = view.CoreWebView2;
                if (core == null)
                {
                    CleanupWebPanel(view);
                    return;
                }

                var settings = core.Settings;
                settings.AreDefaultContextMenusEnabled = false;
                settings.AreDevToolsEnabled = false;
                settings.AreHostObjectsAllowed = false;
                settings.IsWebMessageEnabled = true;
                settings.IsStatusBarEnabled = false;
                settings.IsBuiltInErrorPageEnabled = false;
                settings.IsZoomControlEnabled = false;
                settings.AreBrowserAcceleratorKeysEnabled = false;
                settings.IsPasswordAutosaveEnabled = false;
                settings.IsGeneralAutofillEnabled = false;
                settings.IsSwipeNavigationEnabled = false;

                string host = "theme-" + Guid.NewGuid().ToString("N") + WebPanelHostSuffix;
                core.SetVirtualHostNameToFolderMapping(
                    host,
                    folder,
                    Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.DenyCors);

                core.NewWindowRequested += WebPanel_NewWindowRequested;
                core.NavigationStarting += WebPanel_NavigationStarting;
                core.DownloadStarting += WebPanel_DownloadStarting;
                core.PermissionRequested += WebPanel_PermissionRequested;
                core.ContextMenuRequested += WebPanel_ContextMenuRequested;
                core.AddWebResourceRequestedFilter("*", Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += WebPanel_WebResourceRequested;

                await core.AddScriptToExecuteOnDocumentCreatedAsync("window.__vsAccentColor = " + System.Text.Json.JsonSerializer.Serialize(GetWindowsAccentColorHex()) + ";");

                await core.AddScriptToExecuteOnDocumentCreatedAsync(WebPanelApi);
                if (token.IsCancellationRequested || dialog.WebPanelLifetimeEnded)
                    return;

                dialog.RegisterWebPanel(view);
                core.Navigate("https://" + host + "/" + Uri.EscapeDataString(file));
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                    App.Logger.WriteException("CustomDialog::InitialiseWebPanel", ex);

                CleanupWebPanel(view);
            }
            finally
            {
                if (token.IsCancellationRequested || dialog.WebPanelLifetimeEnded)
                    CleanupWebPanel(view);
            }
        }

        private static bool IsWebPanelUri(string uri)
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed)
                || parsed.Scheme != Uri.UriSchemeHttps
                || !parsed.Host.EndsWith(WebPanelHostSuffix, StringComparison.OrdinalIgnoreCase))
                return false;

            string identifier = parsed.Host[..^WebPanelHostSuffix.Length];
            return identifier.StartsWith("theme-", StringComparison.OrdinalIgnoreCase)
                && Guid.TryParseExact(identifier[6..], "N", out _);
        }

        private static readonly string[] AllowedWebPanelHosts = { "cdn.jsdelivr.net" };

        private static bool IsAllowedWebPanelHost(string uri)
        {
            return Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed)
                && parsed.Scheme == Uri.UriSchemeHttps
                && AllowedWebPanelHosts.Contains(parsed.Host, StringComparer.OrdinalIgnoreCase);
        }

        private static void WebPanel_NewWindowRequested(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true;
        }

        private static void WebPanel_NavigationStarting(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationStartingEventArgs e)
        {
            if (!IsWebPanelUri(e.Uri))
                e.Cancel = true;
        }

        private static void WebPanel_DownloadStarting(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2DownloadStartingEventArgs e)
        {
            e.Cancel = true;
        }

        private static void WebPanel_PermissionRequested(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2PermissionRequestedEventArgs e)
        {
            e.State = Microsoft.Web.WebView2.Core.CoreWebView2PermissionState.Deny;
        }

        private static void WebPanel_ContextMenuRequested(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2ContextMenuRequestedEventArgs e)
        {
            e.Handled = true;
        }

        private static void WebPanel_WebResourceRequested(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebResourceRequestedEventArgs e)
        {
            if (IsWebPanelUri(e.Request.Uri))
                return;

            if (IsAllowedWebPanelHost(e.Request.Uri))
                return;

            if (sender is Microsoft.Web.WebView2.Core.CoreWebView2 core)
                e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", string.Empty);
        }

        private static void WebPanel_Unloaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Microsoft.Web.WebView2.Wpf.WebView2 view)
                return;

            CleanupWebPanel(view);
        }

        private static void CleanupWebPanel(Microsoft.Web.WebView2.Wpf.WebView2 view)
        {
            view.Unloaded -= WebPanel_Unloaded;

            if (view.Tag is not CustomDialog owner)
                return;

            view.Tag = null;
            owner.UnregisterWebPanel(view);

            var core = view.CoreWebView2;
            if (core != null)
            {
                core.NewWindowRequested -= WebPanel_NewWindowRequested;
                core.NavigationStarting -= WebPanel_NavigationStarting;
                core.DownloadStarting -= WebPanel_DownloadStarting;
                core.PermissionRequested -= WebPanel_PermissionRequested;
                core.ContextMenuRequested -= WebPanel_ContextMenuRequested;
                core.WebResourceRequested -= WebPanel_WebResourceRequested;
            }

            view.Dispose();
        }

        private static Border HandleXmlElement_Border(CustomDialog dialog, XElement xmlElement)
        {
            var border = new Border();
            HandleXmlElement_FrameworkElement(dialog, border, xmlElement);

            ApplyBrush_UIElement(dialog, border, "Background", Border.BackgroundProperty, xmlElement);
            ApplyBrush_UIElement(dialog, border, "BorderBrush", Border.BorderBrushProperty, xmlElement);

            object? borderThickness = GetThicknessFromXElement(xmlElement, "BorderThickness");
            if (borderThickness != null)
                border.BorderThickness = (Thickness)borderThickness;

            object? padding = GetThicknessFromXElement(xmlElement, "Padding");
            if (padding != null)
                border.Padding = (Thickness)padding;

            object? cornerRadius = GetCornerRadiusFromXElement(xmlElement, "CornerRadius");
            if (cornerRadius != null)
                border.CornerRadius = (CornerRadius)cornerRadius;

            var children = xmlElement.Elements().Where(x => !x.Name.ToString().StartsWith("Border."));
            if (children.Any())
            {
                if (children.Count() > 1)
                    throw new CustomThemeException("CustomTheme.Errors.ElementMultipleChildren", "Border");

                border.Child = HandleXml<UIElement>(dialog, children.First());
            }

            return border;
        }
        #endregion
    }
}
