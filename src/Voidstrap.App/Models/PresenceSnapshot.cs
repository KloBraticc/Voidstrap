using System;
using System.Collections.Generic;

namespace Voidstrap.Models;

public class PresenceButton
{
    public string Label { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;
}

public class PresenceSnapshot
{
    public bool Connected { get; set; }

    public bool Active { get; set; }

    public string Details { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;

    public string LargeImageKey { get; set; } = string.Empty;

    public string LargeImageText { get; set; } = string.Empty;

    public string SmallImageKey { get; set; } = string.Empty;

    public string SmallImageText { get; set; } = string.Empty;

    public DateTime? Start { get; set; }

    public DateTime? End { get; set; }

    public int PartySize { get; set; }

    public int PartyMax { get; set; }

    public string PartyId { get; set; } = string.Empty;

    public List<PresenceButton> Buttons { get; } = new List<PresenceButton>();

    public bool HasParty => PartySize > 0 && PartyMax > 0;

    public string PartyText => HasParty ? "(" + PartySize + " of " + PartyMax + ")" : string.Empty;
}
