using System.Text.Json;
using System.Text.Json.Serialization;

namespace BaBaBooeyStrap.Integrations.Watcher;

public sealed class BloxstrapRpcMessage
{
    [JsonPropertyName("command")]
    public string? Command { get; set; }

    [JsonPropertyName("data")]
    public JsonElement? Data { get; set; }

    // Optional flat payload fallback compatibility
    [JsonPropertyName("details")]
    public string? Details { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }
}

public sealed class RichPresencePayload
{
    [JsonPropertyName("details")]
    public string? Details { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("timeStart")]
    public long? TimeStart { get; set; }

    [JsonPropertyName("timeEnd")]
    public long? TimeEnd { get; set; }

    [JsonPropertyName("smallImage")]
    public RichPresenceImagePayload? SmallImage { get; set; }

    [JsonPropertyName("largeImage")]
    public RichPresenceImagePayload? LargeImage { get; set; }
}

public sealed class RichPresenceImagePayload
{
    [JsonPropertyName("assetId")]
    public ulong? AssetId { get; set; }

    [JsonPropertyName("hoverText")]
    public string? HoverText { get; set; }

    [JsonPropertyName("clear")]
    public bool? Clear { get; set; }

    [JsonPropertyName("reset")]
    public bool? Reset { get; set; }
}
