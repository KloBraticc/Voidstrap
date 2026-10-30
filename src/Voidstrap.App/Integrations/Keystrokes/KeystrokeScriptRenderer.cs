using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Voidstrap.Integrations.Keystrokes;

internal sealed class KeystrokeScriptRenderer(string themeDirectory)
{
    private const int MaxNodes = 2000;
    private const int MaxImages = 64;
    private const long MaxImageBytes = 16L * 1024 * 1024;

    private readonly Dictionary<string, ImageSource?> _images = new(StringComparer.OrdinalIgnoreCase);
    private int _nodes;

    public UIElement? Build(JsonNode? root)
    {
        _nodes = 0;
        return BuildNode(root);
    }

    private UIElement? BuildNode(JsonNode? node)
    {
        if (node is not JsonObject element || ++_nodes > MaxNodes)
            return null;
        JsonObject props = element["props"] as JsonObject ?? [];
        JsonArray children = element["children"] as JsonArray ?? [];
        if (props["visible"] is JsonValue visible && visible.TryGetValue(out bool isVisible) && !isVisible)
            return null;

        FrameworkElement? built = element["type"]?.ToString() switch
        {
            "box" => MakeBox(props, children),
            "stack" => MakeBorder(props, MakeStack(children)),
            "canvas" => MakeBorder(props, MakeCanvas(children)),
            "text" => MakeBorder(props, MakeText(props)),
            "image" => MakeImage(props),
            "rect" => MakeRect(props),
            "ellipse" => ApplyShape(new Ellipse(), props),
            "line" => MakeLine(props),
            "path" => MakePath(props),
            _ => null,
        };
        if (built != null)
            ApplyCommon(built, props);
        return built;
    }

    private FrameworkElement MakeBox(JsonObject props, JsonArray children)
    {
        bool isRow = string.Equals(props["direction"]?.ToString(), "row", StringComparison.OrdinalIgnoreCase);
        double gap = Number(props, "gap", 0);
        HorizontalAlignment crossH = Horizontal(props["align"]?.ToString()) ?? HorizontalAlignment.Center;
        VerticalAlignment crossV = Vertical(props["align"]?.ToString()) ?? VerticalAlignment.Center;
        StackPanel panel = new() { Orientation = isRow ? Orientation.Horizontal : Orientation.Vertical };
        bool first = true;
        foreach (JsonNode? child in children)
        {
            if (BuildNode(child) is not FrameworkElement item)
                continue;
            Thickness margin = item.Margin;
            if (!first)
                item.Margin = isRow
                    ? new Thickness(margin.Left + gap, margin.Top, margin.Right, margin.Bottom)
                    : new Thickness(margin.Left, margin.Top + gap, margin.Right, margin.Bottom);
            JsonObject? childProps = (child as JsonObject)?["props"] as JsonObject;
            if (isRow && childProps?["valign"] == null)
                item.VerticalAlignment = crossV;
            else if (!isRow && childProps?["halign"] == null)
                item.HorizontalAlignment = crossH;
            panel.Children.Add(item);
            first = false;
        }
        return MakeBorder(props, panel);
    }

    private Grid MakeStack(JsonArray children)
    {
        Grid grid = new();
        foreach (JsonNode? child in children)
            if (BuildNode(child) is UIElement item)
                grid.Children.Add(item);
        return grid;
    }

    private Canvas MakeCanvas(JsonArray children)
    {
        Canvas canvas = new();
        foreach (JsonNode? child in children)
        {
            if (BuildNode(child) is not UIElement item)
                continue;
            JsonObject props = (child as JsonObject)?["props"] as JsonObject ?? [];
            Canvas.SetLeft(item, Number(props, "x", 0));
            Canvas.SetTop(item, Number(props, "y", 0));
            canvas.Children.Add(item);
        }
        return canvas;
    }

    private FrameworkElement MakeBorder(JsonObject props, UIElement content)
    {
        Border border = new()
        {
            Child = content,
            Background = BackgroundBrush(props),
            BorderBrush = Paint(props["borderColor"]),
            BorderThickness = new Thickness(Number(props, "borderWidth", 0)),
            Padding = Spacing(props["padding"]),
            CornerRadius = new CornerRadius(Number(props, "radius", 0)),
        };
        if (props["clip"] is JsonValue clip && clip.TryGetValue(out bool clips))
            border.ClipToBounds = clips;
        return border;
    }

    private static TextBlock MakeText(JsonObject props)
    {
        TextBlock text = new()
        {
            Text = props["value"]?.ToString() ?? "",
            FontSize = Math.Clamp(Number(props, "size", 14), 1, 400),
            Foreground = Paint(props["color"]) ?? Brushes.White,
            FontWeight = Weight(props["weight"]?.ToString()),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (props["font"]?.ToString() is { Length: > 0 } font)
            text.FontFamily = new System.Windows.Media.FontFamily(font);
        return text;
    }

    private FrameworkElement MakeImage(JsonObject props)
    {
        return new Image
        {
            Source = LoadImage(props["src"]?.ToString()),
            Stretch = StretchOf(props["fit"]?.ToString()),
        };
    }

    private static FrameworkElement MakeRect(JsonObject props)
    {
        Rectangle shape = new();
        double radius = Number(props, "radius", 0);
        shape.RadiusX = radius;
        shape.RadiusY = radius;
        return ApplyShape(shape, props);
    }

    private static FrameworkElement MakeLine(JsonObject props)
    {
        return ApplyShape(
            new Line
            {
                X1 = Number(props, "x1", 0),
                Y1 = Number(props, "y1", 0),
                X2 = Number(props, "x2", 0),
                Y2 = Number(props, "y2", 0),
            },
            props
        );
    }

    private static FrameworkElement MakePath(JsonObject props)
    {
        return ApplyShape(
            new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(props["d"]?.ToString() ?? ""),
                Stretch = StretchOf(props["fit"]?.ToString() ?? "None"),
            },
            props
        );
    }

    private static Shape ApplyShape(Shape shape, JsonObject props)
    {
        shape.Fill = Paint(props["fill"]);
        shape.Stroke = Paint(props["stroke"]);
        shape.StrokeThickness = Number(props, "strokeWidth", 1);
        return shape;
    }

    private static void ApplyCommon(FrameworkElement element, JsonObject props)
    {
        if (props["width"] != null)
            element.Width = Math.Max(0, Number(props, "width", 0));
        if (props["height"] != null)
            element.Height = Math.Max(0, Number(props, "height", 0));
        if (props["margin"] != null)
            element.Margin = Spacing(props["margin"]);
        if (props["opacity"] != null)
            element.Opacity = Math.Clamp(Number(props, "opacity", 1), 0, 1);
        if (Horizontal(props["halign"]?.ToString()) is HorizontalAlignment horizontal)
            element.HorizontalAlignment = horizontal;
        if (Vertical(props["valign"]?.ToString()) is VerticalAlignment vertical)
            element.VerticalAlignment = vertical;
        if (props["zIndex"] != null)
            Panel.SetZIndex(element, (int)Number(props, "zIndex", 0));

        double scale = Number(props, "scale", 1);
        double scaleX = Number(props, "scaleX", scale);
        double scaleY = Number(props, "scaleY", scale);
        double rotate = Number(props, "rotate", 0);
        double moveX = Number(props, "translateX", 0);
        double moveY = Number(props, "translateY", 0);
        if (scaleX == 1 && scaleY == 1 && rotate == 0 && moveX == 0 && moveY == 0)
            return;
        TransformGroup group = new();
        group.Children.Add(new ScaleTransform(scaleX, scaleY));
        group.Children.Add(new RotateTransform(rotate));
        group.Children.Add(new TranslateTransform(moveX, moveY));
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = group;
    }

    private Brush? BackgroundBrush(JsonObject props)
    {
        if (LoadImage(props["backgroundImage"]?.ToString()) is ImageSource source)
        {
            return new ImageBrush(source)
            {
                Stretch = StretchOf(props["fit"]?.ToString()),
                Opacity = Math.Clamp(Number(props, "imageOpacity", 1), 0, 1),
            };
        }
        return Paint(props["background"]);
    }

    private static Brush? Paint(JsonNode? value)
    {
        if (value is JsonObject gradient)
            return Gradient(gradient);
        string? text = value?.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : new SolidColorBrush(KeystrokeTheme.ParseColor(text, Colors.Transparent));
    }

    private static Brush Gradient(JsonObject gradient)
    {
        GradientStopCollection stops = [];
        if (gradient["stops"] is JsonArray list)
        {
            int index = 0;
            foreach (JsonNode? stop in list)
            {
                JsonObject? item = stop as JsonObject;
                double fallback = list.Count > 1 ? index / (double)(list.Count - 1) : 0;
                stops.Add(new GradientStop(KeystrokeTheme.ParseColor(item?["color"]?.ToString() ?? stop?.ToString(), Colors.Transparent), item == null ? fallback : Number(item, "at", fallback)));
                index++;
            }
        }
        else
        {
            stops.Add(new GradientStop(KeystrokeTheme.ParseColor(gradient["from"]?.ToString(), Colors.Transparent), 0));
            stops.Add(new GradientStop(KeystrokeTheme.ParseColor(gradient["to"]?.ToString(), Colors.Transparent), 1));
        }
        if (gradient["radial"] is JsonValue radial && radial.TryGetValue(out bool isRadial) && isRadial)
            return new RadialGradientBrush(stops);
        return new LinearGradientBrush(stops, Number(gradient, "angle", 90));
    }

    private ImageSource? LoadImage(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        if (_images.TryGetValue(name, out ImageSource? cached))
            return cached;
        ImageSource? loaded = null;
        string root = System.IO.Path.GetFullPath(themeDirectory) + System.IO.Path.DirectorySeparatorChar;
        string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(themeDirectory, name));
        if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(full) && new FileInfo(full).Length <= MaxImageBytes)
        {
            try
            {
                BitmapImage bitmap = new();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bitmap.UriSource = new Uri(full, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                loaded = bitmap;
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
            {
                loaded = null;
            }
        }
        if (_images.Count >= MaxImages)
            _images.Clear();
        _images[name] = loaded;
        return loaded;
    }

    private static double Number(JsonObject props, string name, double fallback)
    {
        return props[name] is JsonValue value && value.TryGetValue(out double number) && double.IsFinite(number) ? number : fallback;
    }

    private static Thickness Spacing(JsonNode? node)
    {
        if (node is JsonArray list)
        {
            double At(int index) => index < list.Count && list[index] is JsonValue v && v.TryGetValue(out double n) ? n : 0;
            return list.Count switch
            {
                2 => new Thickness(At(1), At(0), At(1), At(0)),
                4 => new Thickness(At(3), At(0), At(1), At(2)),
                _ => new Thickness(At(0)),
            };
        }
        return node is JsonValue value && value.TryGetValue(out double all) ? new Thickness(all) : new Thickness(0);
    }

    private static FontWeight Weight(string? name) =>
        name switch
        {
            "Normal" or "normal" => FontWeights.Normal,
            "Medium" or "medium" => FontWeights.Medium,
            "Bold" or "bold" => FontWeights.Bold,
            "ExtraBold" or "extrabold" => FontWeights.ExtraBold,
            _ => FontWeights.SemiBold,
        };

    private static Stretch StretchOf(string? name) =>
        name switch
        {
            "Fill" or "fill" => Stretch.Fill,
            "UniformToFill" or "cover" => Stretch.UniformToFill,
            "None" or "none" => Stretch.None,
            _ => Stretch.Uniform,
        };

    private static HorizontalAlignment? Horizontal(string? name) =>
        name switch
        {
            "start" or "left" => HorizontalAlignment.Left,
            "center" => HorizontalAlignment.Center,
            "end" or "right" => HorizontalAlignment.Right,
            "stretch" => HorizontalAlignment.Stretch,
            _ => null,
        };

    private static VerticalAlignment? Vertical(string? name) =>
        name switch
        {
            "start" or "top" => VerticalAlignment.Top,
            "center" => VerticalAlignment.Center,
            "end" or "bottom" => VerticalAlignment.Bottom,
            "stretch" => VerticalAlignment.Stretch,
            _ => null,
        };
}
