using System.Text.Json.Serialization;

namespace BaBaBooeyStrap.Core.Models;

public class RobloxCookies
{
    [JsonPropertyName("CookiesVersion")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("CookiesData")]
    public string Cookies { get; set; } = string.Empty;
}
