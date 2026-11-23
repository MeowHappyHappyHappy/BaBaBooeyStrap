using System.Text.Json.Serialization;
using BaBaBooeyStrap.Core.Models.Enums;

namespace BaBaBooeyStrap.Core.Models;

public class FastFlag
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;

    [JsonIgnore]
    public FastFlagType Type => InferType(Name);

    public static FastFlagType InferType(string flagName)
    {
        if (string.IsNullOrWhiteSpace(flagName)) return FastFlagType.Unknown;
        if (flagName.StartsWith("FFlag", StringComparison.OrdinalIgnoreCase) ||
            flagName.StartsWith("DFFlag", StringComparison.OrdinalIgnoreCase) ||
            flagName.StartsWith("SFFlag", StringComparison.OrdinalIgnoreCase))
            return FastFlagType.Boolean;
        if (flagName.StartsWith("FInt", StringComparison.OrdinalIgnoreCase) ||
            flagName.StartsWith("DFInt", StringComparison.OrdinalIgnoreCase) ||
            flagName.StartsWith("SFInt", StringComparison.OrdinalIgnoreCase))
            return FastFlagType.Integer;
        if (flagName.StartsWith("FString", StringComparison.OrdinalIgnoreCase) ||
            flagName.StartsWith("DFString", StringComparison.OrdinalIgnoreCase) ||
            flagName.StartsWith("SFString", StringComparison.OrdinalIgnoreCase))
            return FastFlagType.String;
        if (flagName.StartsWith("FFLog", StringComparison.OrdinalIgnoreCase) ||
            flagName.StartsWith("FLog", StringComparison.OrdinalIgnoreCase) ||
            flagName.StartsWith("DFLog", StringComparison.OrdinalIgnoreCase) ||
            flagName.StartsWith("SFLog", StringComparison.OrdinalIgnoreCase))
            return FastFlagType.Log;
        return FastFlagType.Unknown;
    }
}
