using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace BaBaBooeyStrap.Core.Roblox;

public sealed class RobloxAuthService : IRobloxAuthService
{
    private const int MaxJsonResponseBytes = 2 * 1024 * 1024;
    private const int MaxErrorBodyChars = 512;

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
            var handler = CreateDefaultHandler();
            _httpClient = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(15)
            };
            _disposeClient = true;
        }
    }

    internal static SocketsHttpHandler CreateDefaultHandler() => new()
    {
        UseCookies = false,
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        AutomaticDecompression = DecompressionMethods.All
    };

    public async Task<AuthenticatedUser> GetAuthenticatedUserAsync(string cookie, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cookie);

        using var request = CreateRequest(HttpMethod.Get, "https://users.roblox.com/v1/users/authenticated", cookie);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string errorBody = await ReadTruncatedErrorAsync(response.Content, ct).ConfigureAwait(false);
            throw new RobloxApiException(response.StatusCode, $"Failed to get authenticated user: {response.StatusCode}", errorBody);
        }

        string json = await ReadBoundedStringAsync(response.Content, MaxJsonResponseBytes, ct).ConfigureAwait(false);
        using var doc = ParseJsonSafe(json, response.StatusCode, "reading authenticated user");
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("id", out var idProp) ||
            idProp.ValueKind != JsonValueKind.Number ||
            !idProp.TryGetInt64(out long id) ||
            !root.TryGetProperty("name", out var nameProp) ||
            nameProp.ValueKind != JsonValueKind.String)
        {
            throw new RobloxApiException(response.StatusCode, "Unexpected authenticated user JSON payload.");
        }

        string name = nameProp.GetString() ?? string.Empty;
        string displayName = root.TryGetProperty("displayName", out var dispProp) && dispProp.ValueKind == JsonValueKind.String
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
            throw new RobloxApiException(response.StatusCode, $"Failed to generate auth ticket: {response.StatusCode}");
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
        string body = await ReadBoundedStringAsync(response.Content, 64 * 1024, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("ticket", out var ticketProp) &&
                    ticketProp.ValueKind == JsonValueKind.String)
                {
                    string? ticket = ticketProp.GetString();
                    if (!string.IsNullOrWhiteSpace(ticket))
                    {
                        return ticket;
                    }
                }
            }
            catch (JsonException) { }
        }

        throw new RobloxApiException(HttpStatusCode.OK, "Auth ticket was not present in response headers or body.");
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
            string errorBody = await ReadTruncatedErrorAsync(response.Content, ct).ConfigureAwait(false);
            throw new RobloxApiException(response.StatusCode, $"Failed to resolve share link: {response.StatusCode}", errorBody);
        }

        string json = await ReadBoundedStringAsync(response.Content, MaxJsonResponseBytes, ct).ConfigureAwait(false);
        using var doc = ParseJsonSafe(json, response.StatusCode, "resolving share link");
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new RobloxApiException(response.StatusCode, "Unexpected share link JSON payload.");
        }

        long placeId = TryGetOptionalInt64(root, "placeId") ?? 0;
        string? linkCode = TryGetOptionalNonEmptyString(root, "linkCode");

        // Support nested privateServerInviteData object
        if (root.TryGetProperty("privateServerInviteData", out var inviteData) &&
            inviteData.ValueKind == JsonValueKind.Object)
        {
            long? nestedPlace = TryGetOptionalInt64(inviteData, "placeId");
            if (nestedPlace.HasValue)
            {
                placeId = nestedPlace.Value;
            }
            string? nestedCode = TryGetOptionalNonEmptyString(inviteData, "linkCode")
                ?? TryGetOptionalNonEmptyString(inviteData, "inviteCode")
                ?? TryGetOptionalNonEmptyString(inviteData, "code");
            if (!string.IsNullOrWhiteSpace(nestedCode))
            {
                linkCode = nestedCode;
            }
        }

        if (placeId <= 0 || string.IsNullOrWhiteSpace(linkCode))
        {
            throw new RobloxApiException(HttpStatusCode.OK, "Failed to parse placeId or linkCode from share link response.", TruncateErrorBody(json));
        }

        return new ResolvedShareLink(placeId, linkCode);
    }

    public async Task<string?> GetAvatarThumbnailUrlAsync(long userId, string size = "150x150", bool isCircular = true, CancellationToken ct = default)
    {
        if (userId <= 0)
        {
            return null;
        }

        string safeSize = Uri.EscapeDataString(string.IsNullOrWhiteSpace(size) ? "150x150" : size.Trim());
        string circularStr = isCircular ? "true" : "false";
        string url = $"https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds={userId}&size={safeSize}&format=Png&isCircular={circularStr}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", "BaBaBooeyStrap/1.0");

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        string json = await ReadBoundedStringAsync(response.Content, MaxJsonResponseBytes, ct).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("data", out var dataArr) &&
                dataArr.ValueKind == JsonValueKind.Array &&
                dataArr.GetArrayLength() > 0)
            {
                var first = dataArr[0];
                if (first.ValueKind == JsonValueKind.Object)
                {
                    return TryGetOptionalNonEmptyString(first, "imageUrl");
                }
            }
        }
        catch (JsonException) { }

        return null;
    }

    public async Task<IReadOnlyDictionary<long, string>> GetAvatarThumbnailUrlsAsync(
        IEnumerable<long> userIds,
        string size = "150x150",
        bool isCircular = true,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(userIds);

        var distinctIds = userIds.Where(static id => id > 0).Distinct().ToList();
        var result = new Dictionary<long, string>();
        if (distinctIds.Count == 0)
        {
            return result;
        }

        string safeSize = Uri.EscapeDataString(string.IsNullOrWhiteSpace(size) ? "150x150" : size.Trim());
        string circularStr = isCircular ? "true" : "false";

        foreach (long[] chunk in distinctIds.Chunk(100))
        {
            ct.ThrowIfCancellationRequested();

            string url = $"https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds={string.Join(",", chunk)}&size={safeSize}&format=Png&isCircular={circularStr}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "BaBaBooeyStrap/1.0");

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                continue;
            }

            string json = await ReadBoundedStringAsync(response.Content, MaxJsonResponseBytes, ct).ConfigureAwait(false);
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                    !doc.RootElement.TryGetProperty("data", out var dataArr) ||
                    dataArr.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var entry in dataArr.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (!entry.TryGetProperty("targetId", out var targetIdProp) ||
                        targetIdProp.ValueKind != JsonValueKind.Number ||
                        !targetIdProp.TryGetInt64(out long targetId) ||
                        targetId <= 0)
                    {
                        continue;
                    }

                    string? imageUrl = TryGetOptionalNonEmptyString(entry, "imageUrl");
                    if (!string.IsNullOrWhiteSpace(imageUrl))
                    {
                        result[targetId] = imageUrl;
                    }
                }
            }
            catch (JsonException) { }
        }

        return result;
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

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        string body = await ReadBoundedStringAsync(response.Content, MaxJsonResponseBytes, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new RobloxApiException(response.StatusCode, $"Failed to resolve username '{cleanUsername}': {response.StatusCode}", TruncateErrorBody(body));
        }

        using var doc = ParseJsonSafe(body, response.StatusCode, "resolving username");
        if (doc.RootElement.ValueKind == JsonValueKind.Object &&
            doc.RootElement.TryGetProperty("data", out var dataArr) &&
            dataArr.ValueKind == JsonValueKind.Array &&
            dataArr.GetArrayLength() > 0)
        {
            var first = dataArr[0];
            if (first.ValueKind == JsonValueKind.Object &&
                first.TryGetProperty("id", out var idProp) &&
                idProp.ValueKind == JsonValueKind.Number &&
                idProp.TryGetInt64(out long userId) &&
                userId > 0)
            {
                return userId;
            }
        }

        throw new RobloxApiException(HttpStatusCode.NotFound, $"Roblox user '{cleanUsername}' was not found.", TruncateErrorBody(body));
    }

    public async Task<AuthenticatedUser?> GetUserByIdAsync(long userId, CancellationToken ct = default)
    {
        if (userId <= 0)
        {
            return null;
        }

        string url = $"https://users.roblox.com/v1/users/{userId}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", "BaBaBooeyStrap/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        string json = await ReadBoundedStringAsync(response.Content, MaxJsonResponseBytes, ct).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            long id = TryGetOptionalInt64(root, "id") ?? userId;
            string name = TryGetOptionalNonEmptyString(root, "name") ?? string.Empty;
            string displayName = TryGetOptionalNonEmptyString(root, "displayName") ?? name;

            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(displayName))
            {
                return null;
            }

            return new AuthenticatedUser(id, name, displayName);
        }
        catch (JsonException)
        {
            return null;
        }
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

        if (!response.IsSuccessStatusCode)
        {
            string errorBody = await ReadTruncatedErrorAsync(response.Content, ct).ConfigureAwait(false);
            throw new RobloxApiException(response.StatusCode, $"Failed to query user presence: {response.StatusCode}", errorBody);
        }

        string body = await ReadBoundedStringAsync(response.Content, MaxJsonResponseBytes, ct).ConfigureAwait(false);
        using var doc = ParseJsonSafe(body, response.StatusCode, "querying user presence");
        if (doc.RootElement.ValueKind == JsonValueKind.Object &&
            doc.RootElement.TryGetProperty("userPresences", out var presencesArr) &&
            presencesArr.ValueKind == JsonValueKind.Array &&
            presencesArr.GetArrayLength() > 0)
        {
            JsonElement? matched = null;
            foreach (var entry in presencesArr.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object &&
                    entry.TryGetProperty("userId", out var uidProp) &&
                    uidProp.ValueKind == JsonValueKind.Number &&
                    uidProp.TryGetInt64(out long entryUid) &&
                    entryUid == userId)
                {
                    matched = entry;
                    break;
                }
            }

            var target = matched ?? presencesArr[0];
            if (target.ValueKind == JsonValueKind.Object)
            {
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
            using (var uniRes = await _httpClient.SendAsync(uniReq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                if (!uniRes.IsSuccessStatusCode)
                {
                    return null;
                }

                string uniJson = await ReadBoundedStringAsync(uniRes.Content, MaxJsonResponseBytes, ct).ConfigureAwait(false);
                using var uniDoc = JsonDocument.Parse(uniJson);
                if (uniDoc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    universeId = TryGetOptionalInt64(uniDoc.RootElement, "universeId");
                }
            }

            if (!universeId.HasValue || universeId.Value <= 0)
            {
                return null;
            }

            string gameName = $"Place {placeId}";
            using (var gameReq = CreateAnonymousGetRequest($"https://games.roblox.com/v1/games?universeIds={universeId.Value}"))
            using (var gameRes = await _httpClient.SendAsync(gameReq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                if (gameRes.IsSuccessStatusCode)
                {
                    string gameJson = await ReadBoundedStringAsync(gameRes.Content, MaxJsonResponseBytes, ct).ConfigureAwait(false);
                    using var gameDoc = JsonDocument.Parse(gameJson);
                    if (gameDoc.RootElement.ValueKind == JsonValueKind.Object &&
                        gameDoc.RootElement.TryGetProperty("data", out var dataArr) &&
                        dataArr.ValueKind == JsonValueKind.Array &&
                        dataArr.GetArrayLength() > 0 &&
                        dataArr[0].ValueKind == JsonValueKind.Object)
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
            using (var thumbRes = await _httpClient.SendAsync(thumbReq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                if (thumbRes.IsSuccessStatusCode)
                {
                    string thumbJson = await ReadBoundedStringAsync(thumbRes.Content, MaxJsonResponseBytes, ct).ConfigureAwait(false);
                    using var thumbDoc = JsonDocument.Parse(thumbJson);
                    if (thumbDoc.RootElement.ValueKind == JsonValueKind.Object &&
                        thumbDoc.RootElement.TryGetProperty("data", out var thumbArr) &&
                        thumbArr.ValueKind == JsonValueKind.Array &&
                        thumbArr.GetArrayLength() > 0 &&
                        thumbArr[0].ValueKind == JsonValueKind.Object)
                    {
                        thumbnailUrl = TryGetOptionalNonEmptyString(thumbArr[0], "imageUrl");
                    }
                }
            }

            return new RobloxGameInfo(placeId, universeId.Value, gameName, thumbnailUrl);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
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
        HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        try
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                TimeSpan delay = GetRetryAfterDelay(response);
                response.Dispose();
                await Task.Delay(delay, ct).ConfigureAwait(false);

                using var retryRateLimitRequest = CreateRequest(method, url, cookie, jsonPayload);
                response = await _httpClient.SendAsync(retryRateLimitRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }

            if (response.StatusCode == HttpStatusCode.Forbidden &&
                TryGetHeaderValue(response, "x-csrf-token", out string? csrfToken) &&
                !string.IsNullOrWhiteSpace(csrfToken))
            {
                response.Dispose();

                using var retryRequest = CreateRequest(method, url, cookie, jsonPayload);
                retryRequest.Headers.Add("X-CSRF-TOKEN", csrfToken);
                return await _httpClient.SendAsync(retryRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }

            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static TimeSpan GetRetryAfterDelay(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            if (delta <= TimeSpan.Zero) return TimeSpan.FromMilliseconds(250);
            return delta > TimeSpan.FromSeconds(3) ? TimeSpan.FromSeconds(3) : delta;
        }

        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var computed = date - DateTimeOffset.UtcNow;
            if (computed <= TimeSpan.Zero) return TimeSpan.FromMilliseconds(250);
            return computed > TimeSpan.FromSeconds(3) ? TimeSpan.FromSeconds(3) : computed;
        }

        return TimeSpan.FromSeconds(1);
    }

    private static async Task<string> ReadBoundedStringAsync(HttpContent content, int maxBytes, CancellationToken ct)
    {
        if (content.Headers.ContentLength is { } contentLength && contentLength > maxBytes)
        {
            throw new RobloxApiException(HttpStatusCode.BadGateway, $"Response payload exceeded limit ({contentLength} bytes).");
        }

        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        byte[] buffer = new byte[8192];
        int totalRead = 0;

        while (true)
        {
            int read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read <= 0) break;

            totalRead += read;
            if (totalRead > maxBytes)
            {
                throw new RobloxApiException(HttpStatusCode.BadGateway, $"Response payload exceeded maximum {maxBytes} bytes.");
            }

            ms.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    private static async Task<string> ReadTruncatedErrorAsync(HttpContent content, CancellationToken ct)
    {
        try
        {
            string raw = await ReadBoundedStringAsync(content, 16 * 1024, ct).ConfigureAwait(false);
            return TruncateErrorBody(raw);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string TruncateErrorBody(string? body)
    {
        if (string.IsNullOrEmpty(body)) return string.Empty;
        return body.Length <= MaxErrorBodyChars ? body : body[..MaxErrorBodyChars];
    }

    private static JsonDocument ParseJsonSafe(string json, HttpStatusCode statusCode, string context)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new RobloxApiException(statusCode, $"Invalid JSON response while {context}.", TruncateErrorBody(json), ex);
        }
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

        trimmed = trimmed.Trim().Trim('"', '\'').TrimEnd(';').Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        foreach (char ch in trimmed)
        {
            if (char.IsControl(ch))
            {
                return string.Empty;
            }
        }

        return trimmed;
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string url, string cookie, string? jsonPayload = null)
    {
        string normalizedCookie = NormalizeCookie(cookie);
        if (string.IsNullOrWhiteSpace(normalizedCookie))
        {
            throw new ArgumentException("Roblox cookie is invalid or empty.", nameof(cookie));
        }

        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Cookie", $".ROBLOSECURITY={normalizedCookie}");
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
