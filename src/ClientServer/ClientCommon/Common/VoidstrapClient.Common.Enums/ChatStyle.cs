using System.ComponentModel;
using System.Text.Json.Serialization;

namespace VoidstrapClient.Common.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChatStyle
{
	Classic,
	Bubble,
	[Description("Classic and Bubble")]
	ClassicAndBubble
}
