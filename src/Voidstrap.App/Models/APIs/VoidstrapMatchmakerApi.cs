using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Voidstrap.Models.APIs
{
    public class MatchmakerServer
    {
        [JsonPropertyName("id")]
        public string JobId { get; set; } = "";

        [JsonPropertyName("playing")]
        public int Playing { get; set; }

        [JsonPropertyName("maxPlayers")]
        public int MaxPlayers { get; set; }

        [JsonPropertyName("ping")]
        public int Ping { get; set; }

        [JsonPropertyName("fps")]
        public double Fps { get; set; }

        [JsonPropertyName("region")]
        public string Region { get; set; } = "";

        [JsonPropertyName("city")]
        public string City { get; set; } = "";

        [JsonPropertyName("country")]
        public string Country { get; set; } = "";
    }

    public class MatchmakerTimeResponse
    {
        [JsonPropertyName("time")]
        public long Time { get; set; }

        [JsonPropertyName("servers")]
        public List<MatchmakerServer> Servers { get; set; } = new();
    }
}
