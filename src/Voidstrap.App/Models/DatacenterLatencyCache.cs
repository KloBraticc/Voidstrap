namespace Voidstrap.Models;

public sealed class DatacenterLatencyCache
{
    public string Network { get; set; } = "";

    public string Network6 { get; set; } = "";

    public Dictionary<string, DatacenterLatencyResult> Results { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class DatacenterLatencyResult
{
    public int Ms { get; set; }

    public DateTime MeasuredUtc { get; set; }
}
