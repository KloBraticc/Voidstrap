using System.Text.Json.Serialization;

namespace VoidstrapClient.WebServer.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum SignatureType
{
	None,
	Legacy,
	RbxSig,
	RbxSig2,
	RbxSig4
}
