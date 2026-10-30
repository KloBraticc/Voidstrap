namespace Voidstrap.Models;

public sealed class DownloadProgressInfo
{
    public double Percent { get; set; }

    public long BytesDone { get; set; }

    public long TotalBytes { get; set; }

    public double SpeedBytesPerSec { get; set; }

    public int PackagesDone { get; set; }

    public int TotalPackages { get; set; }

    public string BinaryType { get; set; } = "";
}
