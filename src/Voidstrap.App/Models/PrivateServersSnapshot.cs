using Voidstrap.Integrations;

namespace Voidstrap.Models;

internal sealed class PrivateServersSnapshot
{
    public long UserId { get; set; }

    public List<AccountPrivateServer> Servers { get; set; } = [];

    public Dictionary<long, string> Banners { get; set; } = [];
}
