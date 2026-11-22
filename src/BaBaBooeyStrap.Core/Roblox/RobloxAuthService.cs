using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace BaBaBooeyStrap.Core.Roblox;

public sealed class RobloxAuthService : IRobloxAuthService
{
    private readonly HttpClient _httpClient;
    private readonly bool _disposeClient;

    public RobloxAuthService(HttpClient? httpClient = null)
    {
        if (httpClient != null)
        {
            _httpClient = httpClient;
            _disposeClient = false;
        }
        else
        {
            var handler = new SocketsHttpHandler
            {
                UseCookies = false,
                AllowAutoRedirect = true
            };
            _httpClient = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(15)
            };
            _disposeClient = true;
        }
    }

    public async Task<AuthenticatedUser> GetAuthenticatedUserAsync(string cookie, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cookie);

        using var request = CreateRequest(HttpMethod.Get, "https://users.roblox.com/v1/users/authenticated", cookie);
        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new RobloxApiException(response.StatusCode, $"Failed to get authenticated user: {response.StatusCode}", errorBody);
        }

        string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        long id = root.GetProperty("id").GetInt64();
        string name = root.GetProperty("name").GetString() ?? string.Empty;
        string displayName = root.TryGetProperty("displayName", out var dispProp)
            ? dispProp.GetString() ?? name
            : name;

        return new AuthenticatedUser(id, name, displayName);
    }

    public async Task<string> GenerateAuthTicketAsync(string cookie, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cookie);

        using var response = await SendWithCsrfRetryAsync(
            HttpMethod.Post,
            "https://auth.roblox.com/v1/authentication-ticket",
            cookie,
            "{}",
            ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new RobloxApiException(response.StatusCode, $"Failed to generate auth ticket: {response.StatusCode}", errorBody);
        }

        // Try extracting from response headers
        if (response.Headers.TryGetValues("rbx-authentication-ticket", out var ticketHeaders))
        {
            string? ticket = ticketHeaders.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(ticket))
            {
                return ticket;
            }
        }

        if (response.Content.Headers.TryGetValues("rbx-authentication-ticket", out var contentTicketHeaders))
        {
            string? ticket = contentTicketHeaders.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(ticket))
            {
                return ticket;
            }
        }

        // Fallback: check JSON response body
        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("ticket", out var ticketProp))
                {
                    string? ticket = ticketProp.GetString();
                    if (!string.IsNullOrWhiteSpace(ticket))
                    {
                        return ticket;
                    }
                }
            }
            catch { }
        }

        throw new RobloxApiException(HttpStatusCode.OK, "Auth ticket was not present in response headers or body.", body);
    }

    public async Task<ResolvedShareLink> ResolveShareLinkAsync(string cookie, string shareCode, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cookie);
        ArgumentException.ThrowIfNullOrWhiteSpace(shareCode);

        string payload = JsonSerializer.Serialize(new
        {
            linkId = shareCode.Trim(),
            linkType = "Server"
        });

        using var response = await SendWithCsrfRetryAsync(
            HttpMethod.Post,
            "https://apis.roblox.com/sharelinks/v1/resolve-link",
            cookie,
            payload,
            ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new RobloxApiException(response.StatusCode, $"Failed to resolve share link: {response.StatusCode}", errorBody);
        }

        string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        long placeId = 0;
        string? linkCode = null;

        if (root.TryGetProperty("placeId", out var pProp))
        {
            placeId = pProp.GetInt64();
        }
        if (root.TryGetProperty("linkCode", out var lProp))
        {
            linkCode = lProp.GetString();
        }

        // Support nested privateServerInviteData object
        if (root.TryGetProperty("privateServerInviteData", out var inviteData) &&
            inviteData.ValueKind == JsonValueKind.Object)
        {
            if (inviteData.TryGetProperty("placeId", out var nestedPlace))
            {
                placeId = nestedPlace.GetInt64();
            }
            if (inviteData.TryGetProperty("linkCode", out var nestedLink))
            {
                linkCode = nestedLink.GetString();
            }
            else if (inviteData.TryGetProperty("inviteCode", out var nestedInvite))
            {
                linkCode = nestedInvite.GetString();
            }
            else if (inviteData.TryGetProperty("code", out var nestedCode))
            {
                linkCode = nestedCode.GetString();
            }
        }

        if (placeId <= 0 || string.IsNullOrWhiteSpace(linkCode))
        {
            throw new RobloxApiException(HttpStatusCode.OK, "Failed to parse placeId or linkCode from share link response.", json);
        }

        return new ResolvedShareLink(placeId, linkCode);
    }

    public async Task<string?> GetAvatarThumbnailUrlAsync(long userId, string size = "150x150", bool isCircular = true, CancellationToken ct = default)
    {
        string circularStr = isCircular ? "true" : "false";
        string url = $"https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds={userId}&size={size}&format=Png&isCircular={circularStr}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", "BaBaBooeyStrap/1.0");

        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("data", out var dataArr) && dataArr.GetArrayLength() > 0)
        {
            var first = dataArr[0];
            if (first.TryGetProperty("imageUrl", out var imgProp))
            {
                return imgProp.GetString();
            }
        }

        return null;
    }

    public async Task<long> ResolveUserIdByUsernameAsync(string username, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        string cleanUsername = username.Trim().TrimStart('@').Trim();
        if (string.IsNullOrWhiteSpace(cleanUsername))
        {
            throw new ArgumentException("Username cannot be empty.", nameof(username));
        }

        string payload = JsonSerializer.Serialize(new
        {
            usernames = new[] { cleanUsername },
            excludeBannedUsers = false
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://users.roblox.com/v1/usernames/users");
        request.Headers.Add("User-Agent", "BaBaBooeyStrap/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new RobloxApiException(response.StatusCode, $"Failed to resolve username '{cleanUsername}': {response.StatusCode}", body);
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("data", out var dataArr) &&
            dataArr.ValueKind == JsonValueKind.Array &&
            dataArr.GetArrayLength() > 0)
        {
            var first = dataArr[0];
            if (first.TryGetProperty("id", out var idProp) &&
                idProp.ValueKind == JsonValueKind.Number &&
                idProp.TryGetInt64(out long userId) &&
                userId > 0)
            {
                return userId;
            }
        }

        throw new RobloxApiException(HttpStatusCode.NotFound, $"Roblox user '{cleanUsername}' was not found.", body);
    }

    public async Task<RobloxUserPresence> GetUserPresenceAsync(string cookie, long userId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cookie);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);

        string payload = JsonSerializer.Serialize(new
        {
            userIds = new[] { userId }
        });

        using var response = await SendWithCsrfRetryAsync(
            HttpMethod.Post,
            "https://presence.roblox.com/v1/presence/users",
            cookie,
            payload,
            ct).ConfigureAwait(false);

        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new RobloxApiException(response.StatusCode, $"Failed to query user presence: {response.StatusCode}", body);
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("userPresences", out var presencesArr) &&
            presencesArr.ValueKind == JsonValueKind.Array &&
            presencesArr.GetArrayLength() > 0)
        {
            JsonElement? matched = null;
            foreach (var entry in presencesArr.EnumerateArray())
            {
                if (entry.TryGetProperty("userId", out var uidProp) &&
                    uidProp.ValueKind == JsonValueKind.Number &&
                    uidProp.TryGetInt64(out long entryUid) &&
                    entryUid == userId)
                {
                    matched = entry;
                    break;
                }
            }

            var target = matched ?? presencesArr[0];
            int presenceType = target.TryGetProperty("userPresenceType", out var ptProp) && ptProp.ValueKind == JsonValueKind.Number && ptProp.TryGetInt32(out int pt)
                ? pt
                : 0;
            long? placeId = TryGetOptionalInt64(target, "placeId");
            long? rootPlaceId = TryGetOptionalInt64(target, "rootPlaceId");
            long? universeId = TryGetOptionalInt64(target, "universeId");
            string? jobId = TryGetOptionalNonEmptyString(target, "gameId");
            string? lastLocation = TryGetOptionalNonEmptyString(target, "lastLocation");

            return new RobloxUserPresence(userId, presenceType, placeId, rootPlaceId, jobId, universeId, lastLocation);
        }

        return new RobloxUserPresence(userId, 0, null, null, null, null, null);
    }

    public async Task<RobloxGameInfo?> GetGameInfoByPlaceIdAsync(long placeId, CancellationToken ct = default)
    {
        if (placeId <= 0)
        {
            return null;
        }

        try
        {
            long? universeId = null;
            using (var uniReq = CreateAnonymousGetRequest($"https://apis.roblox.com/universes/v1/places/{placeId}/universe"))
            using (var uniRes = await _httpClient.SendAsync(uniReq, ct).ConfigureAwait(false))
            {
                if (!uniRes.IsSuccessStatusCode)
                {
                    return null;
                }

                string uniJson = await uniRes.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var uniDoc = JsonDocument.Parse(uniJson);
                universeId = TryGetOptionalInt64(uniDoc.RootElement, "universeId");
            }

            if (!universeId.HasValue || universeId.Value <= 0)
            {
                return null;
            }

            string gameName = $"Place {placeId}";
            using (var gameReq = CreateAnonymousGetRequest($"https://games.roblox.com/v1/games?universeIds={universeId.Value}"))
            using (var gameRes = await _httpClient.SendAsync(gameReq, ct).ConfigureAwait(false))
            {
                if (gameRes.IsSuccessStatusCode)
                {
                    string gameJson = await gameRes.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using var gameDoc = JsonDocument.Parse(gameJson);
                    if (gameDoc.RootElement.TryGetProperty("data", out var dataArr) &&
                        dataArr.ValueKind == JsonValueKind.Array &&
                        dataArr.GetArrayLength() > 0)
                    {
                        string? fetchedName = TryGetOptionalNonEmptyString(dataArr[0], "name");
                        if (!string.IsNullOrWhiteSpace(fetchedName))
                        {
                            gameName = fetchedName;
                        }
                    }
                }
            }

            string? thumbnailUrl = null;
            using (var thumbReq = CreateAnonymousGetRequest(
                $"https://thumbnails.roblox.com/v1/games/icons?universeIds={universeId.Value}&returnPolicy=PlaceHolder&size=150x150&format=Png&isCircular=false"))
            using (var thumbRes = await _httpClient.SendAsync(thumbReq, ct).ConfigureAwait(false))
            {
                if (thumbRes.IsSuccessStatusCode)
                {
                    string thumbJson = await thumbRes.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using var thumbDoc = JsonDocument.Parse(thumbJson);
                    if (thumbDoc.RootElement.TryGetProperty("data", out var thumbArr) &&
                        thumbArr.ValueKind == JsonValueKind.Array &&
                        thumbArr.GetArrayLength() > 0)
                    {
                        thumbnailUrl = TryGetOptionalNonEmptyString(thumbArr[0], "imageUrl");
                    }
                }
            }

            return new RobloxGameInfo(placeId, universeId.Value, gameName, thumbnailUrl);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static HttpRequestMessage CreateAnonymousGetRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", "BaBaBooeyStrap/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static long? TryGetOptionalInt64(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) &&
            prop.ValueKind == JsonValueKind.Number &&
            prop.TryGetInt64(out long val) &&
            val > 0)
        {
            return val;
        }
        return null;
    }

    private static string? TryGetOptionalNonEmptyString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) &&
            prop.ValueKind == JsonValueKind.String)
        {
            string? str = prop.GetString();
            return string.IsNullOrWhiteSpace(str) ? null : str.Trim();
        }
        return null;
    }

    private async Task<HttpResponseMessage> SendWithCsrfRetryAsync(
        HttpMethod method,
        string url,
        string cookie,
        string? jsonPayload,
        CancellationToken ct)
    {
        using var request = CreateRequest(method, url, cookie, jsonPayload);
        var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            string? csrfToken = null;
            if (TryGetHeaderValue(response, "x-csrf-token", out var tokenValue))
            {
                csrfToken = tokenValue;
            }
            else if (TryGetHeaderValue(response, "X-CSRF-TOKEN", out var upperTokenValue))
            {
                csrfToken = upperTokenValue;
            }

            if (!string.IsNullOrWhiteSpace(csrfToken))
            {
                response.Dispose();

                using var retryRequest = CreateRequest(method, url, cookie, jsonPayload);
                retryRequest.Headers.Add("X-CSRF-TOKEN", csrfToken);
                return await _httpClient.SendAsync(retryRequest, ct).ConfigureAwait(false);
            }
        }

        return response;
    }

    private static bool TryGetHeaderValue(HttpResponseMessage response, string headerName, out string? value)
    {
        if (response.Headers.TryGetValues(headerName, out var values))
        {
            value = values.FirstOrDefault();
            return !string.IsNullOrWhiteSpace(value);
        }

        if (response.Content.Headers.TryGetValues(headerName, out var contentValues))
        {
            value = contentValues.FirstOrDefault();
            return !string.IsNullOrWhiteSpace(value);
        }

        value = null;
        return false;
    }

    public static string NormalizeCookie(string cookie)
    {
        if (string.IsNullOrWhiteSpace(cookie))
        {
            return string.Empty;
        }

        string trimmed = cookie.Trim().Trim('"', '\'');
        const string cookieHeaderPrefix = "Cookie:";
        if (trimmed.StartsWith(cookieHeaderPrefix, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[cookieHeaderPrefix.Length..].Trim().Trim('"', '\'');
        }

        int semiIdx = trimmed.IndexOf(';');
        if (semiIdx >= 0)
        {
            string[] parts = trimmed.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string? robloPart = parts.FirstOrDefault(p => p.StartsWith(".ROBLOSECURITY=", StringComparison.OrdinalIgnoreCase));
            trimmed = robloPart ?? parts.FirstOrDefault() ?? string.Empty;
        }

        const string prefix = ".ROBLOSECURITY=";
        if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[prefix.Length..];
        }

        return trimmed.Trim().Trim('"', '\'').TrimEnd(';').Trim();
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string url, string cookie, string? jsonPayload = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Cookie", $".ROBLOSECURITY={NormalizeCookie(cookie)}");
        request.Headers.Add("Referer", "https://www.roblox.com/");
        request.Headers.Add("Origin", "https://www.roblox.com");
        request.Headers.Add("User-Agent", "BaBaBooeyStrap/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrEmpty(jsonPayload))
        {
            request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
        }

        return request;
    }

    public void Dispose()
    {
        if (_disposeClient)
        {
            _httpClient.Dispose();
        }
    }
}
