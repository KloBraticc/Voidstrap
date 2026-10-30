using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Voidstrap.Integrations.Keystrokes;

internal static class KeystrokeScriptTemplates
{
    public const string Reference = """
        render(input)            required, returns what to draw, runs on every key change
        onKey(name, down)        optional, runs on every key change before render
        overlay.fps = 30         optional, redraw up to 60 times a second for animation

        input.down("W")          true while held. Names: A to Z, 0 to 9, F1 to F12, Num0 to Num9,
                                 Space, Shift, Ctrl, Alt, Tab, Enter, Esc, Up, Down, Left, Right,
                                 LMB, RMB, MMB and more
        input.since("W")         milliseconds since the key last changed
        input.count("W")         presses since the overlay started
        input.show("W")          false when the overlay settings hide this key
        input.cps.lmb, .rmb      clicks per second
        input.mouse.x, .y        smoothed movement from -1 to 1
        input.mouse.dx, .dy      raw movement, .wheel scroll
        input.time, input.dt     clock and time since the last frame, in milliseconds
        input.accent             system accent color
        input.settings           showKeys, showMouseButtons, showSpaceAndShift, showCps

        row(props, ...children)     column(props, ...children)     box(props, ...children)
        stack(props, ...children)   layers children on top of each other
        canvas(props, ...children)  places children by x and y
        text("W", props)   image("file.png", props)   rect(props)   ellipse(props)
        line(props)        path("M0,0 L10,10", props)

        Any element: width height margin opacity scale scaleX scaleY rotate translateX translateY
                     x y zIndex halign valign visible
        row column box stack canvas text: padding background backgroundImage imageOpacity fit
                     borderColor borderWidth radius gap align clip
        text: size color weight font        image: fit (fill, cover, none)
        rect ellipse line path: fill stroke strokeWidth, rect: radius, line: x1 y1 x2 y2
        Colors: "#RRGGBB", "#AARRGGBB", "Accent", { from, to, angle }, { stops: [...], radial: true }
        """;

    public const string Classic = """
        const size = { W: [46, 46], A: [46, 46], S: [46, 46], D: [46, 46], LMB: [71, 46], RMB: [71, 46], Shift: [51, 34], Space: [91, 34] };
        const labels = { Shift: "SHIFT", Space: "SPACE" };

        function key(input, name, footer) {
          if (!input.show(name)) return null;
          const down = input.down(name);
          return box(
            {
              width: size[name][0], height: size[name][1], radius: 8, borderWidth: 1,
              borderColor: "#363638", background: down ? "Accent" : "#202020", scale: down ? 0.96 : 1,
            },
            column(
              { halign: "center", valign: "center" },
              text(labels[name] || name, { size: 14, color: "#FFFFFF" }),
              footer ? text(footer, { size: 10, color: down ? "#FFFFFF" : "#A0A0A5" }) : null
            )
          );
        }

        function render(input) {
          const cps = input.settings.showCps;
          return column(
            { gap: 3 },
            row({ gap: 3 }, key(input, "W")),
            row({ gap: 3 }, key(input, "A"), key(input, "S"), key(input, "D")),
            row({ gap: 3 }, key(input, "LMB", cps && input.cps.lmb + " CPS"), key(input, "RMB", cps && input.cps.rmb + " CPS")),
            row({ gap: 3 }, key(input, "Shift"), key(input, "Space"))
          );
        }
        """;

    public const string Minimal = """
        const keys = ["W", "A", "S", "D", "Space", "LMB", "RMB"];

        function render(input) {
          return row(
            { gap: 2, padding: 4, radius: 6, background: "#B0101014" },
            keys.filter(input.show).map(name =>
              box(
                { padding: [2, 8], radius: 4, background: input.down(name) ? "Accent" : "#00000000" },
                text(name, { size: 12, color: input.down(name) ? "#FFFFFF" : "#808088" })
              )
            )
          );
        }
        """;

    public const string Mouse = """
        overlay.fps = 60;
        const radius = 44;

        function render(input) {
          const dot = 22;
          return row(
            { gap: 10 },
            canvas(
              { width: radius * 2, height: radius * 2 },
              ellipse({ width: radius * 2, height: radius * 2, fill: "#202020", stroke: "#363638", strokeWidth: 1.25 }),
              ellipse({
                x: radius - dot / 2 + input.mouse.x * (radius - dot / 2),
                y: radius - dot / 2 + input.mouse.y * (radius - dot / 2),
                width: dot, height: dot, fill: "Accent",
                scale: input.down("LMB") || input.down("RMB") ? 1.25 : 1,
              })
            ),
            column(
              { gap: 3 },
              ["LMB", "RMB"].map(name =>
                box(
                  { width: 56, height: 41, radius: 8, background: input.down(name) ? "Accent" : "#202020" },
                  text(name, { size: 12 })
                )
              )
            )
          );
        }
        """;

    public const string Cps = """
        const history = [];
        let last = 0;
        overlay.fps = 30;

        function render(input) {
          const value = input.cps.lmb + input.cps.rmb;
          if (input.time - last > 100) {
            last = input.time;
            history.push(value);
            if (history.length > 40) history.shift();
          }
          const points = history.map((v, i) => (i ? "L" : "M") + i * 4 + "," + (40 - Math.min(v, 20) * 2)).join(" ");
          return row(
            { gap: 10, padding: 10, radius: 10, background: "#D0101014" },
            column(
              text(value, { size: 28, weight: "Bold" }),
              text("CPS", { size: 10, color: "#A0A0A5" })
            ),
            canvas(
              { width: 160, height: 40 },
              history.length > 1 ? path(points, { stroke: "Accent", strokeWidth: 2 }) : null
            )
          );
        }
        """;

    public const string Animated = """
        overlay.fps = 60;

        function glow(input, name) {
          const fade = Math.max(0, 1 - input.since(name) / 400);
          const strength = input.down(name) ? 1 : fade;
          return stack(
            { width: 48, height: 48 },
            rect({ radius: 12, fill: "Accent", opacity: strength }),
            box(
              { radius: 12, borderWidth: 1, borderColor: "#50FFFFFF", background: "#30101014", scale: 1 - strength * 0.08 },
              text(name, { size: 16, weight: "Bold" })
            )
          );
        }

        function render(input) {
          const float = Math.sin(input.time / 700) * 2;
          return column(
            { gap: 4, translateY: float },
            row(glow(input, "W")),
            row({ gap: 4 }, glow(input, "A"), glow(input, "S"), glow(input, "D"))
          );
        }
        """;

    public static string FromTheme(KeystrokeTheme theme)
    {
        JsonSerializerOptions options = new() { WriteIndented = true };
        object config = new
        {
            background = theme.KeyBackground,
            pressed = theme.KeyPressedBackground,
            border = theme.KeyBorderColor,
            borderWidth = theme.KeyBorderThickness,
            radius = theme.KeyCornerRadius,
            textColor = theme.KeyTextColor,
            pressedText = theme.KeyPressedTextColor,
            cpsColor = theme.CpsTextColor,
            pressedCps = theme.KeyPressedCpsTextColor,
            pressScale = theme.KeyScaleOnPress,
            font = theme.FontFamily,
            weight = theme.FontWeight,
            backgroundImage = theme.KeyBackgroundImage,
            pressedImage = theme.KeyPressedBackgroundImage,
            fit = theme.KeyImageStretch,
            imageOpacity = theme.KeyImageOpacity,
            mouseCircle = theme.ShowMouseCircle ? theme.MouseCircleSize : 0,
        };
        Dictionary<string, object?> caps = theme.Keycaps.ToDictionary(
            pair => pair.Key,
            pair =>
                (object?)new
                {
                    image = pair.Value.Image,
                    pressedImage = pair.Value.PressedImage,
                    width = pair.Value.Width,
                    height = pair.Value.Height,
                    radius = pair.Value.CornerRadius,
                    text = pair.Value.Text,
                    hideText = pair.Value.HideText,
                }
        );
        foreach (KeyValuePair<string, string> image in theme.KeyImages)
            caps.TryAdd(image.Key, new { image = image.Value, pressedImage = theme.KeyPressedImages.GetValueOrDefault(image.Key) });

        StringBuilder code = new();
        code.Append(CultureInfo.InvariantCulture, $"const theme = {JsonSerializer.Serialize(config, options)};\n");
        code.Append(CultureInfo.InvariantCulture, $"const caps = {JsonSerializer.Serialize(caps, options)};\n");
        code.Append(CultureInfo.InvariantCulture, $"const layout = {JsonSerializer.Serialize(theme.GetEffectiveLayoutRows(), options)};\n");
        if (theme.ShowMouseCircle)
            code.Append("overlay.fps = 60;\n");
        code.Append(
            """

            const defaults = { LMB: [71, 46], RMB: [71, 46], Shift: [51, 34], Space: [91, 34] };
            const labels = { Shift: "SHIFT", Space: "SPACE" };

            function key(input, name) {
              if (!input.show(name)) return null;
              const cap = caps[name] || {};
              const down = input.down(name);
              const size = defaults[name] || [46, 46];
              const image = down ? cap.pressedImage || theme.pressedImage : cap.image || theme.backgroundImage;
              const showCps = input.settings.showCps && (name === "LMB" || name === "RMB");
              return box(
                {
                  width: cap.width || size[0], height: cap.height || size[1],
                  radius: cap.radius === null || cap.radius === undefined ? theme.radius : cap.radius,
                  borderWidth: theme.borderWidth, borderColor: theme.border,
                  background: down ? theme.pressed : theme.background,
                  backgroundImage: image, fit: theme.fit, imageOpacity: theme.imageOpacity,
                  scale: down ? theme.pressScale : 1,
                },
                column(
                  { halign: "center", valign: "center" },
                  cap.hideText ? null : text(cap.text || labels[name] || name, {
                    size: 14, font: theme.font, weight: theme.weight, color: down ? theme.pressedText : theme.textColor,
                  }),
                  showCps ? text(input.cps[name.toLowerCase()] + " CPS", {
                    size: 10, font: theme.font, color: down ? theme.pressedCps : theme.cpsColor,
                  }) : null
                )
              );
            }

            function mouseCircle(input) {
              const size = theme.mouseCircle;
              if (!size) return null;
              const dot = Math.max(12, Math.round(size * 0.27));
              const travel = (size - dot) / 2;
              return canvas(
                { width: size, height: size },
                ellipse({ width: size, height: size, fill: theme.background, stroke: theme.border, strokeWidth: theme.borderWidth }),
                ellipse({
                  x: travel + input.mouse.x * travel, y: travel + input.mouse.y * travel, width: dot, height: dot,
                  fill: theme.pressed, scale: input.down("LMB") || input.down("RMB") ? 1.2 : 1,
                })
              );
            }

            function render(input) {
              return row(
                { gap: 14 },
                mouseCircle(input),
                column({ gap: 3 }, layout.map(keys => row({ gap: 3 }, keys.map(name => key(input, name)))))
              );
            }

            """
        );
        return code.ToString();
    }
}
