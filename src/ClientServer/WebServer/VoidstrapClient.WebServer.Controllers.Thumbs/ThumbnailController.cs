using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using VoidstrapClient.Common;
using VoidstrapClient.WebServer.Enums;

namespace VoidstrapClient.WebServer.Controllers.Thumbs;

public class ThumbnailController : ControllerBase
{
	private const int MinWidth = 1;

	private const int MinHeight = 1;

	private const int MaxWidth = 1000;

	private const int MaxHeight = 1000;
	private const int MaxSourceDimension = 10000;
	private const long MaxSourcePixels = 64000000;

	private static readonly int ProcessorCount = Environment.ProcessorCount;
	private static readonly int MaxConcurrentResizes = Math.Clamp(ProcessorCount, 2, 8);
	private static readonly SemaphoreSlim _semaphore = new SemaphoreSlim(MaxConcurrentResizes, MaxConcurrentResizes);

	protected virtual bool PreserveHeight => true;

	private byte[] ResizeThumbnailInternal(byte[] imageBytes, int width, int height, ThumbnailFormat format)
	{
		using MemoryStream stream = new MemoryStream(imageBytes);
		using Image image = Image.FromStream(stream);
		if (image.Width < 1 || image.Height < 1 || image.Width > MaxSourceDimension || image.Height > MaxSourceDimension || (long)image.Width * image.Height > MaxSourcePixels)
		{
			throw new InvalidDataException("The source thumbnail dimensions are invalid");
		}
		float num = (float)width / (float)image.Width;
		float num2 = (float)height / (float)image.Height;
		float num3 = ((!PreserveHeight) ? ((num2 < num) ? num2 : num) : num2);
		int num4 = (int)((float)image.Width * num3);
		int num5 = (int)((float)image.Height * num3);
		int x = 0;
		int y = 0;
		if (num2 < num || PreserveHeight)
		{
			x = (width - num4) / 2;
		}
		else if (!PreserveHeight)
		{
			y = (height - num5) / 2;
		}
		using Image image2 = new Bitmap(width, height);
		using (Graphics graphics = Graphics.FromImage(image2))
		{
			if (format == ThumbnailFormat.Jpeg)
			{
				graphics.Clear(Color.White);
			}
			graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
			graphics.DrawImage(image, x, y, num4, num5);
		}
		using MemoryStream memoryStream = new MemoryStream();
		image2.Save(memoryStream, (format == ThumbnailFormat.Png) ? ImageFormat.Png : ImageFormat.Jpeg);
		memoryStream.Position = 0L;
		return memoryStream.ToArray();
	}

	private async Task<(bool Success, byte[] ResizedThumbnail)> ResizeThumbnailAsync(byte[] imageBytes, int width, int height, ThumbnailFormat format, CancellationToken cancellationToken)
	{
		await _semaphore.WaitAsync(cancellationToken);
		try
		{
			return (true, ResizeThumbnailInternal(imageBytes, width, height, format));
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception value)
		{
			Logger.Instance.Warn($"Failed to resize thumbnail: {value}");
			return (false, Array.Empty<byte>());
		}
		finally
		{
			_semaphore.Release();
		}
	}

	protected async Task<IActionResult> ThumbnailAsync(byte[] imageBytes, int width, int height, ThumbnailFormat format, CancellationToken cancellationToken)
	{
		(bool success, byte[] resizedThumbnail) = await ResizeThumbnailAsync(imageBytes, width, height, format, cancellationToken);
		if (success)
		{
			string contentType = ((format == ThumbnailFormat.Png) ? "image/png" : "image/jpeg");
			return File(resizedThumbnail, contentType);
		}
		return UnprocessableEntity();
	}

	protected static bool IsValidSize(int width, int height)
	{
		if (width < 1 || width > 1000)
		{
			return false;
		}
		if (height < 1 || height > 1000)
		{
			return false;
		}
		return true;
	}

	protected static bool IsValidFormat(ThumbnailFormat format)
	{
		return format is ThumbnailFormat.Png or ThumbnailFormat.Jpeg;
	}
}
