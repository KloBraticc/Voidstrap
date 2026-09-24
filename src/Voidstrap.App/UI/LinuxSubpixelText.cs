using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;

namespace Voidstrap.UI;

public static class LinuxSubpixelText
{
	private const string LogIdent = "LinuxSubpixelText";

	private const string GrayscaleBranch = """
    if (input.textMode > 1.5) {
        let atlasDims = textureDimensions(atlasTexture);
        let atlasSize = vec2<f32>(f32(atlasDims.x), f32(atlasDims.y));
        let subpixelOffset = vec2<f32>(1.0 / max(atlasSize.x * 3.0, 1.0), 0.0);
        let redCoverage = textureSampleGrad(atlasTexture, atlasSampler, atlasCoord - subpixelOffset, atlasCoordDx, atlasCoordDy).r;
        let greenCoverage = alpha;
        let blueCoverage = textureSampleGrad(atlasTexture, atlasSampler, atlasCoord + subpixelOffset, atlasCoordDx, atlasCoordDy).r;
        let rgbCoverage = vec3<f32>(
            text_coverage_to_alpha(redCoverage, input.strokeThickness, gamma, false),
            text_coverage_to_alpha(greenCoverage, input.strokeThickness, gamma, false),
            text_coverage_to_alpha(blueCoverage, input.strokeThickness, gamma, false)) * input.color.a * maskAlpha;
""";

	private const string SubpixelBranch = """
    if (!aliasedText) {
        let subpixelOffset = atlasCoordDx / 3.0;
        let farLeft = textureSampleGrad(atlasTexture, atlasSampler, atlasCoord - 2.0 * subpixelOffset, atlasCoordDx, atlasCoordDy).r;
        let nearLeft = textureSampleGrad(atlasTexture, atlasSampler, atlasCoord - subpixelOffset, atlasCoordDx, atlasCoordDy).r;
        let nearRight = textureSampleGrad(atlasTexture, atlasSampler, atlasCoord + subpixelOffset, atlasCoordDx, atlasCoordDy).r;
        let farRight = textureSampleGrad(atlasTexture, atlasSampler, atlasCoord + 2.0 * subpixelOffset, atlasCoordDx, atlasCoordDy).r;
        let filtered = vec3<f32>(
            farLeft + 2.0 * nearLeft + alpha,
            nearLeft + 2.0 * alpha + nearRight,
            alpha + 2.0 * nearRight + farRight) * 0.25;
        let rgbCoverage = vec3<f32>(
            text_coverage_to_alpha(filtered.r, input.strokeThickness, gamma, false),
            text_coverage_to_alpha(filtered.g, input.strokeThickness, gamma, false),
            text_coverage_to_alpha(filtered.b, input.strokeThickness, gamma, false)) * input.color.a * maskAlpha;
""";

	private static bool _installed;

	public static void Install()
	{
		if (_installed || !OperatingSystem.IsLinux())
			return;

		_installed = true;
		if (Environment.GetEnvironmentVariable("VOIDSTRAP_SUBPIXEL_TEXT") == "0")
		{
			App.Logger.WriteLine(LogIdent, "Subpixel text is turned off by VOIDSTRAP_SUBPIXEL_TEXT");
			return;
		}
		if (Voidstrap.Utility.LinuxStartup.SafeMode)
		{
			App.Logger.WriteLine(LogIdent, "Safe mode is on, keeping grayscale text");
			return;
		}

		Type? shaders = Type.GetType("ProGPU.Backend.Shaders, ProGPU.Backend", false);
		FieldInfo? field = shaders?.GetField("TextShader", BindingFlags.Public | BindingFlags.Static);
		if (shaders is null || field is null || field.FieldType != typeof(string))
		{
			App.Logger.WriteLine(LogIdent, "The renderer text shader was not found, keeping grayscale text");
			return;
		}

		RuntimeHelpers.RunClassConstructor(shaders.TypeHandle);
		if (field.GetValue(null) is not string source)
		{
			App.Logger.WriteLine(LogIdent, "The renderer text shader was empty, keeping grayscale text");
			return;
		}

		string normalized = StripLineComments(source.Replace("\r\n", "\n", StringComparison.Ordinal));
		string anchor = GrayscaleBranch.Replace("\r\n", "\n", StringComparison.Ordinal);
		if (CountOccurrences(normalized, anchor) != 2)
		{
			App.Logger.WriteLine(LogIdent, "The renderer text shader changed, keeping grayscale text");
			return;
		}

		string patched = normalized.Replace(anchor, SubpixelBranch.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
		DynamicMethod setter = new("SetTextShaderSource", null, [typeof(string)], shaders.Module, true);
		ILGenerator il = setter.GetILGenerator();
		il.Emit(OpCodes.Ldarg_0);
		il.Emit(OpCodes.Stsfld, field);
		il.Emit(OpCodes.Ret);
		setter.CreateDelegate<Action<string>>()(patched);

		bool applied = ReferenceEquals(field.GetValue(null), patched);
		App.Logger.WriteLine(LogIdent, applied
			? "Subpixel text rendering is on"
			: "The renderer text shader could not be replaced, keeping grayscale text");
	}

	private static int CountOccurrences(string source, string value)
	{
		int count = 0;
		int index = source.IndexOf(value, StringComparison.Ordinal);
		while (index >= 0)
		{
			count++;
			index = source.IndexOf(value, index + value.Length, StringComparison.Ordinal);
		}
		return count;
	}

	private static string StripLineComments(string source)
	{
		StringBuilder builder = new(source.Length);
		foreach (string line in source.Split('\n'))
		{
			if (!line.TrimStart().StartsWith("//", StringComparison.Ordinal))
				builder.Append(line).Append('\n');
		}
		return builder.ToString();
	}
}
