using System.Globalization;
using System.Text;

namespace Voidstrap.Platform.Linux;

public static class LinuxColorLut
{
	public const int GridSize = 33;

	public static string LutFile => Path.Combine(LinuxEffectLayers.ConfigDirectory, "VoidstrapLook.cube");

	public static OperationResult Write(string path, float[]? matrix)
	{
		try
		{
			string? directory = Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(directory))
				Directory.CreateDirectory(directory);

			StringBuilder builder = new(GridSize * GridSize * GridSize * 24);
			builder.Append("TITLE \"Voidstrap\"\n");
			builder.Append("LUT_3D_SIZE ").Append(GridSize.ToString(CultureInfo.InvariantCulture)).Append('\n');
			builder.Append("DOMAIN_MIN 0.0 0.0 0.0\n");
			builder.Append("DOMAIN_MAX 1.0 1.0 1.0\n");

			float step = 1f / (GridSize - 1);

			for (int blue = 0; blue < GridSize; blue++)
			{
				for (int green = 0; green < GridSize; green++)
				{
					for (int red = 0; red < GridSize; red++)
					{
						float r = red * step;
						float g = green * step;
						float b = blue * step;

						if (matrix is not null)
							Transform(matrix, ref r, ref g, ref b);

						Append(builder, r);
						builder.Append(' ');
						Append(builder, g);
						builder.Append(' ');
						Append(builder, b);
						builder.Append('\n');
					}
				}
			}

			File.WriteAllText(path, builder.ToString());
			return OperationResult.Success();
		}
		catch (Exception ex)
		{
			return OperationResult.Fail("ColorLutWriteFailed", "The colour lookup table could not be written: " + ex.Message);
		}
	}

	private static void Transform(float[] matrix, ref float r, ref float g, ref float b)
	{
		float sourceRed = r;
		float sourceGreen = g;
		float sourceBlue = b;

		float outRed = sourceRed * matrix[0] + sourceGreen * matrix[5] + sourceBlue * matrix[10] + matrix[20];
		float outGreen = sourceRed * matrix[1] + sourceGreen * matrix[6] + sourceBlue * matrix[11] + matrix[21];
		float outBlue = sourceRed * matrix[2] + sourceGreen * matrix[7] + sourceBlue * matrix[12] + matrix[22];

		r = outRed;
		g = outGreen;
		b = outBlue;
	}

	private static void Append(StringBuilder builder, float value)
	{
		float clamped = value < 0f ? 0f : value > 1f ? 1f : value;
		builder.Append(clamped.ToString("0.000000", CultureInfo.InvariantCulture));
	}
}
