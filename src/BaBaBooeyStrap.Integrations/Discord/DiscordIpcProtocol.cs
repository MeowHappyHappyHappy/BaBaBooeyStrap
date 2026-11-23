using System.Buffers.Binary;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BaBaBooeyStrap.Integrations.Discord;

internal static class DiscordIpcProtocol
{
    public const int MaxFrameLength = 65536;
    public const uint OpcodeHandshake = 0;
    public const uint OpcodeFrame = 1;
    public const uint OpcodeClose = 2;
    public const uint OpcodePing = 3;
    public const uint OpcodePong = 4;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions ClearJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string BuildHandshakeJson(string clientId) =>
        JsonSerializer.Serialize(new { v = 1, client_id = clientId }, JsonOptions);

    public static string BuildCloseJson() =>
        JsonSerializer.Serialize(new { code = 1000, message = "Normal closure" }, JsonOptions);

    public static string BuildClearActivityJson(int pid) =>
        JsonSerializer.Serialize(new
        {
            cmd = "SET_ACTIVITY",
            args = new
            {
                pid,
                activity = (object?)null
            },
            nonce = Guid.NewGuid().ToString()
        }, ClearJsonOptions);

    public static string BuildRobloxDeepLink(
        string placeId,
        string? jobId = null,
        string? accessCode = null,
        string? launchData = null)
    {
        if (string.IsNullOrWhiteSpace(placeId)) return string.Empty;

        var sb = new StringBuilder($"roblox://experiences/start?placeId={Uri.EscapeDataString(placeId)}");
        if (!string.IsNullOrWhiteSpace(accessCode))
        {
            sb.Append($"&accessCode={Uri.EscapeDataString(accessCode)}");
        }
        else if (!string.IsNullOrWhiteSpace(jobId))
        {
            sb.Append($"&gameInstanceId={Uri.EscapeDataString(jobId)}");
        }

        if (!string.IsNullOrWhiteSpace(launchData))
        {
            string trimmed = launchData.Length > 200 ? launchData[..200] : launchData;
            sb.Append($"&launchData={Uri.EscapeDataString(trimmed)}");
        }

        return sb.ToString();
    }

    public static string FormatPresenceText(string? text, bool padSingleChar = false)
    {
        string sanitized = SanitizeControlChars(text);
        if (string.IsNullOrEmpty(sanitized)) return string.Empty;

        string result = sanitized.Length > 128 ? sanitized[..128] : sanitized;
        if (Encoding.UTF8.GetByteCount(result) > 128)
        {
            result = TruncateUtf8(result, 128);
        }

        if (padSingleChar && result.Length == 1)
        {
            result += "\u2800\u2800";
        }

        return result;
    }

    public static string BuildSetActivityJson(DiscordActivity activity, DiscordRpcOptions options, int pid)
    {
        string details = FormatPresenceText(activity.Details, padSingleChar: true);
        string state = FormatPresenceText(activity.State, padSingleChar: true);

        object? timestamps = activity.StartTimestamps.HasValue
            ? new { start = activity.StartTimestamps.Value }
            : null;

        string? largeImage = SanitizeKey(activity.LargeImageKey, 512);
        string? largeText = string.IsNullOrWhiteSpace(activity.LargeImageText) ? null : FormatPresenceText(activity.LargeImageText, padSingleChar: true);
        string? smallImage = SanitizeKey(activity.SmallImageKey, 512);
        string? smallText = string.IsNullOrWhiteSpace(activity.SmallImageText) ? null : FormatPresenceText(activity.SmallImageText, padSingleChar: true);

        bool hasAssets = largeImage != null || largeText != null || smallImage != null || smallText != null;

        object? assets = hasAssets
            ? new
            {
                large_image = largeImage,
                large_text = largeText,
                small_image = smallImage,
                small_text = smallText
            }
            : null;

        List<object>? buttons = null;
        if (!options.HideButtons)
        {
            if (TrySanitizeButtonUrl(activity.DeepLinkUrl, allowRobloxScheme: true, out string safeDeepLink))
            {
                buttons ??= new List<object>();
                buttons.Add(new { label = "Join server", url = safeDeepLink });
            }

            if (TrySanitizeButtonUrl(options.GamePageUrl, allowRobloxScheme: false, out string safeGamePageUrl))
            {
                buttons ??= new List<object>();
                buttons.Add(new { label = "See game page", url = safeGamePageUrl });
            }
        }

        // Discord enforces mutual exclusion between buttons and secrets
        string? safeJoinSecret = SanitizeKey(activity.JoinSecret, 512);
        object? secrets = (buttons == null || buttons.Count == 0) && safeJoinSecret != null
            ? new { join = safeJoinSecret }
            : null;

        string SerializeEnvelope(object? currentAssets, List<object>? currentButtons, object? currentSecrets)
        {
            var activityBody = new
            {
                details = string.IsNullOrEmpty(details) ? null : details,
                state = string.IsNullOrEmpty(state) ? null : state,
                status_display_type = (int)options.StatusDisplayType,
                timestamps,
                assets = currentAssets,
                buttons = currentButtons,
                secrets = currentSecrets
            };

            var envelope = new
            {
                cmd = "SET_ACTIVITY",
                args = new
                {
                    pid,
                    activity = activityBody
                },
                nonce = Guid.NewGuid().ToString()
            };

            return JsonSerializer.Serialize(envelope, JsonOptions);
        }

        string json = SerializeEnvelope(assets, buttons, secrets);
        if (Encoding.UTF8.GetByteCount(json) > MaxFrameLength)
        {
            json = SerializeEnvelope(assets, null, null);
        }
        if (Encoding.UTF8.GetByteCount(json) > MaxFrameLength)
        {
            json = SerializeEnvelope(null, null, null);
        }
        return json;
    }

    public static async Task WriteFrameAsync(Stream stream, uint opcode, string json, CancellationToken ct)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        if (payload.Length > MaxFrameLength)
        {
            throw new InvalidDataException($"Discord IPC frame exceeds maximum size ({payload.Length} > {MaxFrameLength}).");
        }

        byte[] frame = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(0, 4), opcode);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4, 4), payload.Length);
        payload.CopyTo(frame, 8);

        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<(uint Opcode, string Json)> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        byte[] header = new byte[8];
        await stream.ReadExactlyAsync(header.AsMemory(0, 8), ct).ConfigureAwait(false);

        uint opcode = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4));
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));

        if (length < 0 || length > MaxFrameLength)
        {
            throw new InvalidDataException($"Invalid Discord IPC frame length: {length}");
        }

        if (length == 0)
        {
            return (opcode, string.Empty);
        }

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload.AsMemory(0, length), ct).ConfigureAwait(false);
        return (opcode, Encoding.UTF8.GetString(payload));
    }

    public static bool IsReadyDispatch(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!doc.RootElement.TryGetProperty("evt", out var evtProp)) return false;
            if (evtProp.ValueKind != JsonValueKind.String) return false;
            return string.Equals(evtProp.GetString(), "READY", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string TruncateUtf8(string value, int maxBytes)
    {
        if (string.IsNullOrEmpty(value) || Encoding.UTF8.GetByteCount(value) <= maxBytes)
        {
            return value;
        }

        var sb = new StringBuilder();
        int byteCount = 0;
        foreach (Rune rune in value.EnumerateRunes())
        {
            int runeBytes = rune.Utf8SequenceLength;
            if (byteCount + runeBytes > maxBytes) break;
            sb.Append(rune.ToString());
            byteCount += runeBytes;
        }
        return sb.ToString();
    }

    private static string SanitizeControlChars(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        bool hasControl = false;
        foreach (char c in text)
        {
            if (char.IsControl(c))
            {
                hasControl = true;
                break;
            }
        }
        if (!hasControl) return text;

        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (!char.IsControl(c))
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static string? SanitizeKey(string? key, int maxBytes)
    {
        string clean = SanitizeControlChars(key).Trim();
        return string.IsNullOrEmpty(clean) ? null : TruncateUtf8(clean, maxBytes);
    }

    private static bool TrySanitizeButtonUrl(string? rawUrl, bool allowRobloxScheme, out string safeUrl)
    {
        safeUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(rawUrl)) return false;

        string trimmed = rawUrl.Trim();
        foreach (char c in trimmed)
        {
            if (char.IsControl(c)) return false;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        bool validScheme = allowRobloxScheme
            ? string.Equals(uri.Scheme, "roblox", StringComparison.OrdinalIgnoreCase)
            : (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)) &&
              !string.IsNullOrWhiteSpace(uri.Host);

        if (!validScheme) return false;
        if (Encoding.UTF8.GetByteCount(trimmed) > 512) return false;

        safeUrl = trimmed;
        return true;
    }
}
