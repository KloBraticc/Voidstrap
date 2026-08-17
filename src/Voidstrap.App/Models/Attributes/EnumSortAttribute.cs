using System;

namespace Voidstrap.Models.Attributes;

internal class EnumSortAttribute : Attribute
{
	public int Order { get; set; }
}
