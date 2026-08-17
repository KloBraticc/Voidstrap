using System;
using Voidstrap.Resources;

namespace Voidstrap.Models.Persistable;

public class DownloadStats
{
	private string _downloadingStringFormat = Strings.Bootstrapper_Status_Downloading + " {1}MB / {2}MB";

	public string DownloadingStringFormat
	{
		get
		{
			return _downloadingStringFormat;
		}
		set
		{
			if (string.IsNullOrEmpty(value))
			{
				throw new ArgumentException("DownloadingStringFormat cannot be null or empty.", "value");
			}
			_downloadingStringFormat = value;
		}
	}

	public string GetFormattedDownloadStatus(string currentFile, int downloadedSize, int totalSize)
	{
		return string.Format(DownloadingStringFormat, currentFile, downloadedSize, totalSize);
	}
}
