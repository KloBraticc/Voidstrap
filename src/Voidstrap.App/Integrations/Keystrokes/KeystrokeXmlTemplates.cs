using System.Xml.Linq;

namespace Voidstrap.Integrations.Keystrokes;

internal static class KeystrokeXmlTemplates
{
    public const string Reference = """
        One file, Theme.xml, in the same XML as launcher themes.

        <VoidstrapKeystrokes Version="1">   the root, also takes Margin, Opacity and Background
        <KeyStyle ... />                    look shared by every Key, placed directly under the root
        <Key Input="W" ... />               one key, anything set here wins over KeyStyle
        <CpsText Input="LMB" />             clicks per second as text, Format="{0} CPS"
        <MouseCircle Size="88" />           circle with a dot that follows the mouse

        Key and KeyStyle:
          Input                 W, A, S, D, Space, Shift, Ctrl, Alt, Tab, Enter, Esc, LMB, RMB, MMB,
                                A to Z, 0 to 9, F1 to F12, Num0 to Num9, Up, Down, Left, Right
          Text  HideText        label, defaults to the key name
          Width  Height  Margin  CornerRadius  BorderThickness
          Background  PressedBackground  BorderBrush
          Foreground  PressedForeground  CpsForeground  PressedCpsForeground
          FontSize  FontWeight  FontFamily  CpsFontSize  CpsFormat  ShowCps
          PressedScale          size while held, 0.96 by default, 1 turns it off
          Image  PressedImage   picture from the theme folder, theme://name.png
          ImageStretch  ImageOpacity
          Elements placed inside a Key are drawn behind its label.

        MouseCircle: Size  DotSize  DotFill  Background  BorderBrush  BorderThickness

        Layout and drawing, exactly as in launcher themes:
          Grid (RowDefinition, ColumnDefinition, Grid.Row, Grid.Column)  StackPanel (Orientation)
          Border  TextBlock  Image  Ellipse  Rectangle  Line
          SolidColorBrush  LinearGradientBrush  GradientStop  ImageBrush
          ScaleTransform  RotateTransform  TranslateTransform  SkewTransform
          BlurEffect  DropShadowEffect
          Any element: Name Width Height Margin HorizontalAlignment VerticalAlignment Opacity Visibility

        Colors: #RRGGBB, #AARRGGBB, a color name, Accent, or {ResourceName} such as
        {SystemAccentColorPrimaryBrush}. A gradient goes in a child, for example
        <Key.Background><LinearGradientBrush>...</LinearGradientBrush></Key.Background>.
        Keys hidden in the overlay settings disappear on their own, and so does the CPS line.
        """;

    public const string Classic = """
        <VoidstrapKeystrokes Version="1">
          <KeyStyle Background="#202020" PressedBackground="Accent" BorderBrush="#363638" BorderThickness="1"
                    CornerRadius="8" Foreground="#FFFFFF" PressedForeground="#FFFFFF"
                    CpsForeground="#A0A0A5" PressedCpsForeground="#FFFFFF"
                    FontFamily="Segoe UI" FontWeight="SemiBold" PressedScale="0.96" />
          <StackPanel Orientation="Vertical">
            <StackPanel Orientation="Horizontal" HorizontalAlignment="Center" Margin="0,0,0,3">
              <Key Input="W" />
            </StackPanel>
            <StackPanel Orientation="Horizontal" HorizontalAlignment="Center" Margin="0,0,0,3">
              <Key Input="A" Margin="0,0,3,0" />
              <Key Input="S" Margin="0,0,3,0" />
              <Key Input="D" />
            </StackPanel>
            <StackPanel Orientation="Horizontal" HorizontalAlignment="Center" Margin="0,0,0,3">
              <Key Input="LMB" Margin="0,0,3,0" />
              <Key Input="RMB" />
            </StackPanel>
            <StackPanel Orientation="Horizontal" HorizontalAlignment="Center">
              <Key Input="Shift" Margin="0,0,3,0" />
              <Key Input="Space" />
            </StackPanel>
          </StackPanel>
        </VoidstrapKeystrokes>
        """;

    public const string Minimal = """
        <VoidstrapKeystrokes Version="1">
          <KeyStyle Background="#00000000" PressedBackground="Accent" BorderThickness="0" CornerRadius="4"
                    Foreground="#808088" PressedForeground="#FFFFFF" FontSize="12" Height="22" Width="34"
                    PressedScale="1" ShowCps="False" Margin="1,0,1,0" />
          <Border Background="#B0101014" CornerRadius="6" Padding="4">
            <StackPanel Orientation="Horizontal">
              <Key Input="W" />
              <Key Input="A" />
              <Key Input="S" />
              <Key Input="D" />
              <Key Input="Space" Text="SPACE" Width="52" />
              <Key Input="LMB" />
              <Key Input="RMB" />
            </StackPanel>
          </Border>
        </VoidstrapKeystrokes>
        """;

    public const string Mouse = """
        <VoidstrapKeystrokes Version="1">
          <KeyStyle Background="#202020" PressedBackground="Accent" BorderBrush="#363638" CornerRadius="8"
                    Foreground="#FFFFFF" FontSize="12" Width="56" Height="41" ShowCps="False" />
          <StackPanel Orientation="Horizontal">
            <MouseCircle Size="88" Background="#202020" BorderBrush="#363638" BorderThickness="1.25"
                         DotFill="Accent" Margin="0,0,10,0" />
            <StackPanel Orientation="Vertical" VerticalAlignment="Center">
              <Key Input="LMB" Margin="0,0,0,3" />
              <Key Input="RMB" />
            </StackPanel>
          </StackPanel>
        </VoidstrapKeystrokes>
        """;

    public const string Cps = """
        <VoidstrapKeystrokes Version="1">
          <Border Background="#C0101014" BorderBrush="#363638" BorderThickness="1" CornerRadius="8" Padding="12,8,12,8">
            <StackPanel Orientation="Horizontal">
              <CpsText Input="LMB" Format="{0} L" FontSize="16" FontWeight="SemiBold" Foreground="#FFFFFF" />
              <TextBlock Text="  |  " FontSize="16" Foreground="#606068" />
              <CpsText Input="RMB" Format="{0} R" FontSize="16" FontWeight="SemiBold" Foreground="#FFFFFF" />
            </StackPanel>
          </Border>
        </VoidstrapKeystrokes>
        """;

    public static string FromTheme(KeystrokeTheme theme)
    {
        XElement style = new("KeyStyle");
        style.SetAttributeValue("Background", theme.KeyBackground);
        style.SetAttributeValue("PressedBackground", theme.KeyPressedBackground);
        style.SetAttributeValue("BorderBrush", theme.KeyBorderColor);
        style.SetAttributeValue("BorderThickness", Number(theme.KeyBorderThickness));
        style.SetAttributeValue("CornerRadius", Number(theme.KeyCornerRadius));
        style.SetAttributeValue("Foreground", theme.KeyTextColor);
        style.SetAttributeValue("PressedForeground", theme.KeyPressedTextColor);
        style.SetAttributeValue("CpsForeground", theme.CpsTextColor);
        style.SetAttributeValue("PressedCpsForeground", theme.KeyPressedCpsTextColor);
        style.SetAttributeValue("FontFamily", theme.FontFamily);
        style.SetAttributeValue("FontWeight", theme.FontWeight);
        style.SetAttributeValue("PressedScale", Number(theme.KeyScaleOnPress));
        SetImage(style, "Image", theme.KeyBackgroundImage);
        SetImage(style, "PressedImage", theme.KeyPressedBackgroundImage);
        if (!string.IsNullOrWhiteSpace(theme.KeyImageStretch) && theme.KeyImageStretch != "Uniform")
            style.SetAttributeValue("ImageStretch", theme.KeyImageStretch);
        if (theme.KeyImageOpacity < 0.999)
            style.SetAttributeValue("ImageOpacity", Number(theme.KeyImageOpacity));

        XElement rows = new("StackPanel", new XAttribute("Orientation", "Vertical"));
        List<List<string>> layout = theme.GetEffectiveLayoutRows();
        for (int r = 0; r < layout.Count; r++)
        {
            XElement row = new("StackPanel", new XAttribute("Orientation", "Horizontal"), new XAttribute("HorizontalAlignment", "Center"));
            if (r < layout.Count - 1)
                row.SetAttributeValue("Margin", "0,0,0,3");
            List<string> keys = layout[r];
            for (int i = 0; i < keys.Count; i++)
            {
                string name = keys[i];
                XElement key = new("Key", new XAttribute("Input", name));
                if (i < keys.Count - 1)
                    key.SetAttributeValue("Margin", "0,0,3,0");
                if (theme.Keycaps.TryGetValue(name, out KeycapConfig? cap))
                {
                    if (!string.IsNullOrEmpty(cap.Text))
                        key.SetAttributeValue("Text", cap.Text);
                    if (cap.HideText)
                        key.SetAttributeValue("HideText", "True");
                    if (cap.Width is > 0)
                        key.SetAttributeValue("Width", Number(cap.Width.Value));
                    if (cap.Height is > 0)
                        key.SetAttributeValue("Height", Number(cap.Height.Value));
                    if (cap.CornerRadius.HasValue)
                        key.SetAttributeValue("CornerRadius", Number(cap.CornerRadius.Value));
                    SetImage(key, "Image", cap.Image);
                    SetImage(key, "PressedImage", cap.PressedImage);
                }
                if (key.Attribute("Image") == null && theme.KeyImages.TryGetValue(name, out string? image))
                    SetImage(key, "Image", image);
                if (key.Attribute("PressedImage") == null && theme.KeyPressedImages.TryGetValue(name, out string? pressed))
                    SetImage(key, "PressedImage", pressed);
                row.Add(key);
            }
            rows.Add(row);
        }

        XElement body = rows;
        if (theme.ShowMouseCircle)
        {
            XElement circle = new("MouseCircle");
            circle.SetAttributeValue("Size", Number(theme.MouseCircleSize > 0 ? theme.MouseCircleSize : 88));
            circle.SetAttributeValue("Background", theme.KeyBackground);
            circle.SetAttributeValue("BorderBrush", theme.KeyBorderColor);
            circle.SetAttributeValue("DotFill", theme.KeyPressedBackground);
            circle.SetAttributeValue("Margin", "0,0,14,0");
            body = new XElement("StackPanel", new XAttribute("Orientation", "Horizontal"), circle, rows);
        }

        XElement root = new("VoidstrapKeystrokes", new XAttribute("Version", "1"), style, body);
        return root.ToString();
    }

    private static void SetImage(XElement element, string attribute, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path.Trim()))
            return;
        element.SetAttributeValue(attribute, "theme://" + path.Trim().Replace('\\', '/'));
    }

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
