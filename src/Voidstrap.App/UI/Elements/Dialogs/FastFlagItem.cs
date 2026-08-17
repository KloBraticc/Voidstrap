using System.Collections.Generic;

namespace Voidstrap.UI.Elements.Dialogs;

public class FastFlagItem
{
	public string Name { get; set; }

	public string Value { get; set; }

	public List<string> VisibleTags { get; set; } = new List<string>();
}
