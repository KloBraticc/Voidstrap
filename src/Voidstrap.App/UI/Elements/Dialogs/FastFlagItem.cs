using System.Collections.Generic;

namespace Voidstrap.UI.Elements.Dialogs;

public class FastFlagItem
{
	public string Name { get; set; } = null!;

	public string Value { get; set; } = null!;

	public List<string> VisibleTags { get; set; } = new List<string>();
}
