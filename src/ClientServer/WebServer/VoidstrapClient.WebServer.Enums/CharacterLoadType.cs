using System.Text.Json.Serialization;

namespace VoidstrapClient.WebServer.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum CharacterLoadType
{
	Fetch,
	Whole
}
