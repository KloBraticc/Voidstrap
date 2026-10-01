using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;

namespace Voidstrap.UI;

public static class LinuxImageFiltering
{
	private const string LogIdent = "LinuxImageFiltering";

	private const string SingleTapSampling = """
    var texColor = textureSampleGrad(texTexture, texSampler, input.texCoord, textureCoordDx, textureCoordDy);
    if (input.color.a < 0.0 || (input.patchKind > 2.5 && input.patchOpacity < 0.0)) {
        texColor = sample_bicubic(input.texCoord, input.cubicResampler);
    }

""";

	private const string FootprintSampling = """
    let minifiedTaps = minified_tap_counts(textureCoordDx, textureCoordDy);
    var texColor = vec4<f32>(0.0);
    if (minifiedTaps.x * minifiedTaps.y > 1u) {
        texColor = sample_minified(input.texCoord, textureCoordDx, textureCoordDy, minifiedTaps);
    } else if (input.color.a < 0.0 || (input.patchKind > 2.5 && input.patchOpacity < 0.0)) {
        texColor = sample_bicubic(input.texCoord, input.cubicResampler);
    } else {
        texColor = textureSampleGrad(texTexture, texSampler, input.texCoord, textureCoordDx, textureCoordDy);
    }

""";

	private const string FragmentEntry = """
fn texture_fs_main(input: VertexOutput) -> vec4<f32> {

""";

	private const string FootprintFunctions = """
fn minified_tap_counts(dx: vec2<f32>, dy: vec2<f32>) -> vec2<u32> {
    if (textureNumLevels(texTexture) > 1u) {
        return vec2<u32>(1u, 1u);
    }
    let size = vec2<f32>(textureDimensions(texTexture));
    let spanX = length(dx * size);
    let spanY = length(dy * size);
    return vec2<u32>(
        u32(clamp(ceil(spanX - 0.25), 1.0, 8.0)),
        u32(clamp(ceil(spanY - 0.25), 1.0, 8.0)));
}

fn sample_minified(uv: vec2<f32>, dx: vec2<f32>, dy: vec2<f32>, taps: vec2<u32>) -> vec4<f32> {
    let size = vec2<f32>(textureDimensions(texTexture));
    let halfTexel = vec2<f32>(0.5) / size;
    let cell = floor(uv);
    let low = cell + halfTexel;
    let high = cell + vec2<f32>(1.0) - halfTexel;
    let stepX = dx / f32(taps.x);
    let stepY = dy / f32(taps.y);
    let origin = uv - 0.5 * (dx + dy) + 0.5 * (stepX + stepY);
    var color = vec4<f32>(0.0);
    for (var y: u32 = 0u; y < taps.y; y = y + 1u) {
        for (var x: u32 = 0u; x < taps.x; x = x + 1u) {
            let tap = clamp(origin + stepX * f32(x) + stepY * f32(y), low, high);
            color = color + textureSampleLevel(texTexture, texSampler, tap, 0.0);
        }
    }
    return color / f32(taps.x * taps.y);
}


""";

	private static bool _installed;

	public static void Install()
	{
		if (_installed || !OperatingSystem.IsLinux())
			return;

		_installed = true;
		if (Environment.GetEnvironmentVariable("VOIDSTRAP_IMAGE_FILTERING") == "0")
		{
			App.Logger.WriteLine(LogIdent, "Image filtering is turned off by VOIDSTRAP_IMAGE_FILTERING");
			return;
		}
		if (Voidstrap.Utility.LinuxStartup.SafeMode)
		{
			App.Logger.WriteLine(LogIdent, "Safe mode is on, keeping single sample image scaling");
			return;
		}

		Type? shaders = Type.GetType("ProGPU.Backend.Shaders, ProGPU.Backend", false);
		FieldInfo? field = shaders?.GetField("TextureShader", BindingFlags.Public | BindingFlags.Static);
		if (shaders is null || field is null || field.FieldType != typeof(string))
		{
			App.Logger.WriteLine(LogIdent, "The renderer image shader was not found, keeping single sample image scaling");
			return;
		}

		RuntimeHelpers.RunClassConstructor(shaders.TypeHandle);
		if (field.GetValue(null) is not string source)
		{
			App.Logger.WriteLine(LogIdent, "The renderer image shader was empty, keeping single sample image scaling");
			return;
		}

		string normalized = StripLineComments(source.Replace("\r\n", "\n", StringComparison.Ordinal));
		string sampling = Normalize(SingleTapSampling);
		string entry = Normalize(FragmentEntry);
		if (CountOccurrences(normalized, sampling) != 1 || CountOccurrences(normalized, entry) != 1)
		{
			App.Logger.WriteLine(LogIdent, "The renderer image shader changed, keeping single sample image scaling");
			return;
		}

		string functions = Normalize(FootprintFunctions);
		if (Voidstrap.Utility.LinuxStartup.UsesOpenGl)
			functions = functions.Replace("textureNumLevels(texTexture) > 1u", "false", StringComparison.Ordinal);

		string patched = normalized
			.Replace(sampling, Normalize(FootprintSampling), StringComparison.Ordinal)
			.Replace(entry, functions + entry, StringComparison.Ordinal);
		DynamicMethod setter = new("SetTextureShaderSource", null, [typeof(string)], shaders.Module, true);
		ILGenerator il = setter.GetILGenerator();
		il.Emit(OpCodes.Ldarg_0);
		il.Emit(OpCodes.Stsfld, field);
		il.Emit(OpCodes.Ret);
		setter.CreateDelegate<Action<string>>()(patched);

		bool applied = ReferenceEquals(field.GetValue(null), patched);
		App.Logger.WriteLine(LogIdent, applied
			? "Scaled down images now average every source pixel they cover"
			: "The renderer image shader could not be replaced, keeping single sample image scaling");
	}

	private static string Normalize(string value)
	{
		return value.Replace("\r\n", "\n", StringComparison.Ordinal);
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
