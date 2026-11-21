using System.Text.Json.Serialization;

namespace BaBaBooeyStrap.Core.Models.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AppLanguage
{
    Auto,
    TraditionalChinese,
    English
}
