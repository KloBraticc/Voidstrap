using System.Collections.Generic;
using System.Text.Json.Serialization;
using Voidstrap.Models.Persistable;

namespace Voidstrap.Utility;

[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(State))]
[JsonSerializable(typeof(RobloxState))]
[JsonSerializable(typeof(DownloadStats))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(Voidstrap.Models.RobloxWindowIconOverride))]
[JsonSerializable(typeof(List<Voidstrap.Models.RobloxWindowIconOverride>))]
internal sealed partial class PersistedJsonContext : JsonSerializerContext
{
}
