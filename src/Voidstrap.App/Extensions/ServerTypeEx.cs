using Voidstrap.Enums;
using Voidstrap.Resources;

namespace Voidstrap.Extensions;

internal static class ServerTypeEx
{
	public static string ToTranslatedString(this ServerType value)
	{
		return value switch
		{
			ServerType.Public => Strings.Enums_ServerType_Public, 
			ServerType.Private => Strings.Enums_ServerType_Private, 
			ServerType.Reserved => Strings.Enums_ServerType_Reserved, 
			_ => "No Server Type Detected?", 
		};
	}

	public static string ToConnectedString(this ServerType value)
	{
		return value switch
		{
			ServerType.Public => Strings.ContextMenu_ServerInformation_Notification_Title_Public,
			ServerType.Private => Strings.ContextMenu_ServerInformation_Notification_Title_Private,
			ServerType.Reserved => Strings.ContextMenu_ServerInformation_Notification_Title_Reserved,
			_ => Strings.ContextMenu_ServerInformation_Notification_Title_Public,
		};
	}
}
