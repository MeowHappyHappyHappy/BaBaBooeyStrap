using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using BaBaBooeyStrap.Core.Logging;

namespace BaBaBooeyStrap.Integrations.Matchmaking;

public sealed class RoValraService : IRoValraService
{
    private const double EarthRadiusKm = 6371.0;
    private const int LockStripeCount = 64;
    private const int MaxBestRegionsToQuery = 6;
    private const int MaxConcurrentRegionQueries = 3;
    private static readonly TimeSpan PositiveCacheTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromMinutes(2);

    private readonly record struct CacheEntry<T>(T Value, DateTime ExpiresUtc);
    private readonly record struct IpInfoLookup(bool Success, bool IsBogon, string City, string Region, string Country, double Latitude, double Longitude);
    private readonly record struct DataCenterEndpoint(string Country, double Latitude, double Longitude);

    private static readonly Dictionary<string, string> CountryNameToIso = new(StringComparer.OrdinalIgnoreCase)
    {
        ["United States"] = "US",
        ["United States of America"] = "US",
        ["United Kingdom"] = "GB",
        ["Germany"] = "DE",
        ["Japan"] = "JP",
        ["France"] = "FR",
        ["Canada"] = "CA",
        ["Australia"] = "AU",
        ["Singapore"] = "SG",
        ["Netherlands"] = "NL",
        ["Brazil"] = "BR",
        ["South Korea"] = "KR",
        ["Hong Kong"] = "HK",
        ["Taiwan"] = "TW",
        ["Ireland"] = "IE",
        ["Poland"] = "PL",
        ["Sweden"] = "SE",
        ["Finland"] = "FI",
        ["India"] = "IN"
    };

    private static readonly HashSet<string> UsEastRegions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Virginia", "New York", "New Jersey", "Florida", "Georgia", "North Carolina",
        "South Carolina", "Massachusetts", "Pennsylvania", "Maryland", "District of Columbia",
        "Connecticut", "Delaware", "Maine", "New Hampshire", "Rhode Island", "Vermont",
        "VA", "NY", "NJ", "FL", "GA", "NC", "SC", "MA", "PA", "MD", "DC"
    };

    private static readonly HashSet<string> UsWestRegions = new(StringComparer.OrdinalIgnoreCase)
    {
        "California", "Washington", "Oregon", "Nevada", "Arizona", "Utah", "Idaho",
        "Montana", "Alaska", "Hawaii",
        "CA", "WA", "OR", "NV", "AZ", "UT"
    };

    private static readonly HashSet<string> UsCentralRegions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Texas", "Illinois", "Ohio", "Missouri", "Minnesota", "Colorado", "Michigan",
        "Wisconsin", "Indiana", "Tennessee", "Louisiana", "Kansas", "Iowa", "Oklahoma",
        "TX", "IL", "OH", "MO", "MN", "CO", "MI", "WI", "IN", "TN", "LA"
    };

    private readonly HttpClient _httpClient;
    private readonly IPingProbe? _pingProbe;
    private readonly ILogger _logger;
    private readonly double? _clientLatitude;
    private readonly double? _clientLongitude;
    private readonly ConcurrentDictionary<string, CacheEntry<ServerLocationInfo?>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CacheEntry<IpInfoLookup>> _ipInfoCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CacheEntry<IReadOnlyList<DataCenterEndpoint>>> _dataCenterCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim[] _stripedLocks = Enumerable.Range(0, LockStripeCount)
        .Select(_ => new SemaphoreSlim(1, 1))
        .ToArray();

    public RoValraService(
        HttpClient? httpClient = null,
        IPingProbe? pingProbe = null,
        ILogger? logger = null,
        double? clientLatitude = null,
        double? clientLongitude = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        _pingProbe = pingProbe;
        _logger = logger ?? Logger.Current;
        _clientLatitude = clientLatitude;
        _clientLongitude = clientLongitude;
    }

    private SemaphoreSlim GetStripedLock(string key)
    {
        int index = (StringComparer.OrdinalIgnoreCase.GetHashCode(key) & 0x7FFFFFFF) % LockStripeCount;
        return _stripedLocks[index];
    }

    public async Task<ServerLocationInfo?> ResolveServerLocationAsync(string ip, CancellationToken ct = default)
    {
        if (!IsPublicRoutableIp(ip, out IPAddress? parsedIp) || parsedIp == null)
        {
            return null;
        }

        string normalizedIp = parsedIp.ToString();
        DateTime now = DateTime.UtcNow;
        if (_cache.TryGetValue(normalizedIp, out var cached) && cached.ExpiresUtc > now)
        {
            return cached.Value;
        }

        var ipLock = GetStripedLock(normalizedIp);
        await ipLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            now = DateTime.UtcNow;
            if (_cache.TryGetValue(normalizedIp, out cached) && cached.ExpiresUtc > now)
            {
                return cached.Value;
            }

            ServerLocationInfo? resolved = await FetchLocationInternalAsync(normalizedIp, ct).ConfigureAwait(false);
            TimeSpan ttl = resolved != null ? PositiveCacheTtl : NegativeCacheTtl;
            _cache[normalizedIp] = new CacheEntry<ServerLocationInfo?>(resolved, DateTime.UtcNow.Add(ttl));
            return resolved;
        }
        finally
        {
            ipLock.Release();
        }
    }

    private async Task<ServerLocationInfo?> FetchLocationInternalAsync(string ip, CancellationToken ct)
    {
        string city = string.Empty;
        string region = string.Empty;
        string country = string.Empty;
        double lat = 0;
        double lon = 0;
        bool hasAnyData = false;

        // 1. Query IPInfo (https://ipinfo.io/{ip}/json)
        var ipInfoResult = await QueryIpInfoAsync($"https://ipinfo.io/{Uri.EscapeDataString(ip)}/json", ct).ConfigureAwait(false);
        if (ipInfoResult.IsBogon)
        {
            return null;
        }

        if (ipInfoResult.Success)
        {
            city = ipInfoResult.City;
            region = ipInfoResult.Region;
            country = ipInfoResult.Country;
            lat = ipInfoResult.Latitude;
            lon = ipInfoResult.Longitude;
            hasAnyData = true;
        }

        // 2. Fallback or enrich via RoValra (https://apis.rovalra.com/v1/geolocation?ip={ip})
        if (!hasAnyData || string.IsNullOrWhiteSpace(city) || string.IsNullOrWhiteSpace(region))
        {
            var roValraResult = await QueryRoValraGeolocationAsync(ip, ct).ConfigureAwait(false);
            if (roValraResult.Success)
            {
                if (string.IsNullOrWhiteSpace(city)) city = roValraResult.City;
                if (string.IsNullOrWhiteSpace(region)) region = roValraResult.Region;
                if (string.IsNullOrWhiteSpace(country)) country = roValraResult.CountryCode;
                hasAnyData = true;
            }
        }

        if (!hasAnyData)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(city)) city = "Unknown";
        if (string.IsNullOrWhiteSpace(region)) region = "Unknown";
        if (string.IsNullOrWhiteSpace(country)) country = "Unknown";

        string regionCode = ResolveRegionCode(country, region, lat, lon);
        int estimatedPingMs = await EstimateServerPingAsync(ip, lat, lon, ct).ConfigureAwait(false);

        return new ServerLocationInfo(
            ip,
            city,
            region,
            country,
            regionCode,
            lat,
            lon,
            estimatedPingMs);
    }

    private async Task<(bool Success, bool IsBogon, string City, string Region, string Country, double Latitude, double Longitude)> QueryIpInfoAsync(
        string url,
        CancellationToken ct)
    {
        try
        {
            using var resp = await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return default;
            }

            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("bogon", out var bogonProp) && bogonProp.ValueKind == JsonValueKind.True)
            {
                return (false, true, string.Empty, string.Empty, string.Empty, 0, 0);
            }

            string city = root.TryGetProperty("city", out var cityProp) ? cityProp.GetString() ?? string.Empty : string.Empty;
            string region = root.TryGetProperty("region", out var regProp) ? regProp.GetString() ?? string.Empty : string.Empty;
            string country = root.TryGetProperty("country", out var ctyProp) ? ctyProp.GetString() ?? string.Empty : string.Empty;

            double lat = 0;
            double lon = 0;
            if (root.TryGetProperty("loc", out var locProp) && locProp.ValueKind == JsonValueKind.String)
            {
                ParseLatLong(locProp.GetString(), out lat, out lon);
            }

            bool hasFields = !string.IsNullOrWhiteSpace(city) ||
                             !string.IsNullOrWhiteSpace(region) ||
                             !string.IsNullOrWhiteSpace(country);

            return (hasFields, false, city, region, country, lat, lon);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Debug("RoValraService", $"IPInfo query failed for '{url}': {ex.Message}");
            return default;
        }
    }

    private async Task<(bool Success, string City, string Region, string CountryCode)> QueryRoValraGeolocationAsync(
        string ip,
        CancellationToken ct)
    {
        try
        {
            string url = $"https://apis.rovalra.com/v1/geolocation?ip={Uri.EscapeDataString(ip)}";
            using var resp = await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return default;
            }

            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("location", out var loc) || loc.ValueKind != JsonValueKind.Object)
            {
                return default;
            }

            string city = loc.TryGetProperty("city", out var cProp) ? cProp.GetString() ?? string.Empty : string.Empty;
            string region = loc.TryGetProperty("region", out var rProp) ? rProp.GetString() ?? string.Empty : string.Empty;
            string countryName = loc.TryGetProperty("country_name", out var cnProp) ? cnProp.GetString() ?? string.Empty : string.Empty;
            string countryCode = NormalizeCountryToIso(countryName);

            bool hasFields = !string.IsNullOrWhiteSpace(city) ||
                             !string.IsNullOrWhiteSpace(region) ||
                             !string.IsNullOrWhiteSpace(countryCode);

            return (hasFields, city, region, countryCode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Debug("RoValraService", $"RoValra geolocation query failed for '{ip}': {ex.Message}");
            return default;
        }
    }

    public async Task<string?> GetBetterMatchmakingServerIdAsync(
        long placeId,
        bool randomize = false,
        CancellationToken ct = default)
    {
        try
        {
            double clientLat = _clientLatitude ?? 0;
            double clientLon = _clientLongitude ?? 0;
            bool hasClientCoords = _clientLatitude.HasValue && _clientLongitude.HasValue;

            var selfInfo = await GetCachedSelfIpInfoAsync(ct).ConfigureAwait(false);
            if (!hasClientCoords)
            {
                if (!selfInfo.Success || string.IsNullOrWhiteSpace(selfInfo.Country))
                {
                    return null;
                }

                clientLat = selfInfo.Latitude;
                clientLon = selfInfo.Longitude;
            }

            var dataCenters = await GetCachedDataCentersAsync(ct).ConfigureAwait(false);
            if (dataCenters.Count == 0) return null;

            var candidates = new List<(string Country, double DistanceKm)>(dataCenters.Count);
            foreach (var dc in dataCenters)
            {
                double dist = CalculateHaversineKm(clientLat, clientLon, dc.Latitude, dc.Longitude);
                candidates.Add((dc.Country, dist));
            }

            var orderedRegions = candidates
                .OrderByDescending(c => !string.IsNullOrWhiteSpace(selfInfo.Country) &&
                                        string.Equals(c.Country, selfInfo.Country, StringComparison.OrdinalIgnoreCase))
                .ThenBy(c => c.DistanceKm)
                .Select(c => c.Country)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxBestRegionsToQuery)
                .ToList();

            if (orderedRegions.Count == 0) return null;

            string?[] results = new string?[orderedRegions.Count];
            using var concurrencyGate = new SemaphoreSlim(MaxConcurrentRegionQueries, MaxConcurrentRegionQueries);

            var tasks = orderedRegions.Select(async (region, index) =>
            {
                await concurrencyGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    results[index] = await QueryRegionServerIdAsync(placeId, region, randomize, ct).ConfigureAwait(false);
                }
                finally
                {
                    concurrencyGate.Release();
                }
            }).ToArray();

            await Task.WhenAll(tasks).ConfigureAwait(false);

            foreach (string? serverId in results)
            {
                if (!string.IsNullOrWhiteSpace(serverId))
                {
                    return serverId;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Debug("RoValraService", $"Better matchmaking failed for place {placeId}: {ex.Message}");
        }

        return null;
    }

    private async Task<IpInfoLookup> GetCachedSelfIpInfoAsync(CancellationToken ct)
    {
        const string key = "ipinfo:self";
        DateTime now = DateTime.UtcNow;
        if (_ipInfoCache.TryGetValue(key, out var cached) && cached.ExpiresUtc > now)
        {
            return cached.Value;
        }

        var stripe = GetStripedLock(key);
        await stripe.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            now = DateTime.UtcNow;
            if (_ipInfoCache.TryGetValue(key, out cached) && cached.ExpiresUtc > now)
            {
                return cached.Value;
            }

            var raw = await QueryIpInfoAsync("https://ipinfo.io/json", ct).ConfigureAwait(false);
            var lookup = new IpInfoLookup(raw.Success, raw.IsBogon, raw.City, raw.Region, raw.Country, raw.Latitude, raw.Longitude);
            TimeSpan ttl = lookup.Success ? PositiveCacheTtl : NegativeCacheTtl;
            _ipInfoCache[key] = new CacheEntry<IpInfoLookup>(lookup, DateTime.UtcNow.Add(ttl));
            return lookup;
        }
        finally
        {
            stripe.Release();
        }
    }

    private async Task<IReadOnlyList<DataCenterEndpoint>> GetCachedDataCentersAsync(CancellationToken ct)
    {
        const string key = "rovalra:datacenters";
        DateTime now = DateTime.UtcNow;
        if (_dataCenterCache.TryGetValue(key, out var cached) && cached.ExpiresUtc > now)
        {
            return cached.Value;
        }

        var stripe = GetStripedLock(key);
        await stripe.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            now = DateTime.UtcNow;
            if (_dataCenterCache.TryGetValue(key, out cached) && cached.ExpiresUtc > now)
            {
                return cached.Value;
            }

            var list = new List<DataCenterEndpoint>();
            try
            {
                using var dcResp = await _httpClient.GetAsync("https://apis.rovalra.com/v1/datacenters/list", ct).ConfigureAwait(false);
                if (dcResp.IsSuccessStatusCode)
                {
                    string dcJson = await dcResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using var dcDoc = JsonDocument.Parse(dcJson);
                    if (dcDoc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var dc in dcDoc.RootElement.EnumerateArray())
                        {
                            if (!dc.TryGetProperty("location", out var loc) || loc.ValueKind != JsonValueKind.Object) continue;
                            string dcCountry = loc.TryGetProperty("country", out var cProp) ? cProp.GetString() ?? string.Empty : string.Empty;
                            if (string.IsNullOrWhiteSpace(dcCountry)) continue;

                            double dcLat = 0;
                            double dcLon = 0;
                            if (loc.TryGetProperty("latLong", out var llProp) && llProp.ValueKind == JsonValueKind.Array && llProp.GetArrayLength() >= 2)
                            {
                                TryReadJsonDouble(llProp[0], out dcLat);
                                TryReadJsonDouble(llProp[1], out dcLon);
                            }

                            list.Add(new DataCenterEndpoint(dcCountry, dcLat, dcLon));
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Debug("RoValraService", $"Datacenters list query failed: {ex.Message}");
            }

            TimeSpan ttl = list.Count > 0 ? PositiveCacheTtl : NegativeCacheTtl;
            IReadOnlyList<DataCenterEndpoint> result = list;
            _dataCenterCache[key] = new CacheEntry<IReadOnlyList<DataCenterEndpoint>>(result, DateTime.UtcNow.Add(ttl));
            return result;
        }
        finally
        {
            stripe.Release();
        }
    }

    private async Task<string?> QueryRegionServerIdAsync(long placeId, string region, bool randomize, CancellationToken ct)
    {
        try
        {
            string serversUrl = $"https://apis.rovalra.com/v1/servers/region?place_id={placeId}&region={Uri.EscapeDataString(region)}";
            using var srvResp = await _httpClient.GetAsync(serversUrl, ct).ConfigureAwait(false);
            if (!srvResp.IsSuccessStatusCode) return null;

            string srvJson = await srvResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var srvDoc = JsonDocument.Parse(srvJson);
            if (!srvDoc.RootElement.TryGetProperty("servers", out var servers) ||
                servers.ValueKind != JsonValueKind.Array ||
                servers.GetArrayLength() == 0)
            {
                return null;
            }

            int index = randomize ? Random.Shared.Next(0, servers.GetArrayLength()) : 0;
            var chosen = servers[index];
            if (chosen.TryGetProperty("server_id", out var idProp))
            {
                string? serverId = idProp.GetString();
                if (!string.IsNullOrWhiteSpace(serverId)) return serverId;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Debug("RoValraService", $"Region query failed for {region}: {ex.Message}");
        }

        return null;
    }

    public async Task<Dictionary<string, int>> GetRegionLatenciesAsync(string endpoint, CancellationToken ct = default)
    {
        var latencies = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(endpoint)) return latencies;

        try
        {
            using var resp = await _httpClient.GetAsync(endpoint, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return latencies;

            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return latencies;

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.TryGetInt32(out int ms) && ms >= 0 && ms < 5000)
                {
                    latencies[prop.Name] = ms;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Debug("RoValraService", $"Failed to fetch region latencies: {ex.Message}");
        }

        return latencies;
    }

    public static bool IsPublicRoutableIp(string? ip, out IPAddress? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(ip)) return false;
        if (!IPAddress.TryParse(ip.Trim(), out parsed)) return false;

        if (parsed.IsIPv4MappedToIPv6)
        {
            parsed = parsed.MapToIPv4();
        }

        if (parsed.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = parsed.GetAddressBytes();
            return IsPublicRoutableIpv4Bytes(b[0], b[1]);
        }

        if (parsed.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (IPAddress.IsLoopback(parsed) ||
                parsed.Equals(IPAddress.IPv6Any) ||
                parsed.Equals(IPAddress.IPv6None) ||
                parsed.IsIPv6LinkLocal ||
                parsed.IsIPv6SiteLocal ||
                parsed.IsIPv6Multicast)
            {
                return false;
            }

            byte[] b = parsed.GetAddressBytes();
            // Unique Local Address fc00::/7
            if ((b[0] & 0xFE) == 0xFC) return false;

            // Deprecated RFC 4291 IPv4-compatible IPv6 addresses ::/96 (e.g. ::127.0.0.1, ::10.0.0.1)
            bool isIpv4Compatible96 = true;
            for (int i = 0; i < 12; i++)
            {
                if (b[i] != 0)
                {
                    isIpv4Compatible96 = false;
                    break;
                }
            }
            if (isIpv4Compatible96)
            {
                return IsPublicRoutableIpv4Bytes(b[12], b[13]);
            }

            return true;
        }

        return false;
    }

    private static bool IsPublicRoutableIpv4Bytes(byte b0, byte b1)
    {
        // 0.0.0.0/8 (unspecified)
        if (b0 == 0) return false;
        // 10.0.0.0/8 (RFC1918)
        if (b0 == 10) return false;
        // 100.64.0.0/10 (CGNAT)
        if (b0 == 100 && b1 >= 64 && b1 <= 127) return false;
        // 127.0.0.0/8 (loopback)
        if (b0 == 127) return false;
        // 169.254.0.0/16 (link-local / APIPA)
        if (b0 == 169 && b1 == 254) return false;
        // 172.16.0.0/12 (RFC1918)
        if (b0 == 172 && b1 >= 16 && b1 <= 31) return false;
        // 192.168.0.0/16 (RFC1918)
        if (b0 == 192 && b1 == 168) return false;
        // 224.0.0.0/4+ (multicast / reserved)
        if (b0 >= 224) return false;

        return true;
    }

    private static bool TryReadJsonDouble(JsonElement element, out double value)
    {
        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetDouble(out value);
        }
        if (element.ValueKind == JsonValueKind.String)
        {
            return double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
        value = 0;
        return false;
    }

    public static string ResolveRegionCode(string? country, string? region, double latitude = 0, double longitude = 0)
    {
        if (string.IsNullOrWhiteSpace(country) || string.Equals(country, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return "Unknown";
        }

        string iso = NormalizeCountryToIso(country);
        string reg = region?.Trim() ?? string.Empty;

        if (string.Equals(iso, "US", StringComparison.OrdinalIgnoreCase))
        {
            if (UsEastRegions.Contains(reg)) return "US-East";
            if (UsWestRegions.Contains(reg)) return "US-West";
            if (UsCentralRegions.Contains(reg)) return "US-Central";

            if (longitude != 0)
            {
                if (longitude > -85.0) return "US-East";
                if (longitude < -105.0) return "US-West";
                return "US-Central";
            }
            return "US-East";
        }

        if (string.Equals(iso, "CA", StringComparison.OrdinalIgnoreCase))
        {
            if (longitude != 0 && longitude < -100.0) return "US-West";
            return "US-East";
        }

        return iso.ToUpperInvariant() switch
        {
            "GB" or "IE" or "FR" or "ES" or "PT" or "NL" or "BE" => "EU-West",
            "DE" or "PL" or "CH" or "AT" or "CZ" or "IT" or "SE" or "FI" or "NO" or "DK" => "EU-Central",
            "JP" or "KR" or "HK" or "TW" => "AP-East",
            "SG" or "ID" or "MY" or "TH" or "VN" or "PH" or "AU" or "NZ" or "IN" => "AP-Southeast",
            "BR" or "AR" or "CL" or "CO" or "PE" => "SA-East",
            _ => iso.ToUpperInvariant()
        };
    }

    public static string FormatFlagEmoji(string? countryCode)
    {
        if (string.IsNullOrWhiteSpace(countryCode) || countryCode.Length != 2)
        {
            return "🌐";
        }

        char c0 = char.ToUpperInvariant(countryCode[0]);
        char c1 = char.ToUpperInvariant(countryCode[1]);
        if (c0 < 'A' || c0 > 'Z' || c1 < 'A' || c1 > 'Z')
        {
            return "🌐";
        }

        return string.Concat(
            char.ConvertFromUtf32(0x1F1E6 + (c0 - 'A')),
            char.ConvertFromUtf32(0x1F1E6 + (c1 - 'A')));
    }

    public static string FormatLocationBadge(ServerLocationInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        string flag = FormatFlagEmoji(info.Country);
        return $"{flag} {info.City}";
    }

    public static double CalculateHaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        double dLat = DegreesToRadians(lat2 - lat1);
        double dLon = DegreesToRadians(lon2 - lon1);
        double rLat1 = DegreesToRadians(lat1);
        double rLat2 = DegreesToRadians(lat2);

        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(rLat1) * Math.Cos(rLat2) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return EarthRadiusKm * c;
    }

    public static int EstimatePingFromDistanceKm(double distanceKm)
    {
        if (distanceKm < 0) return 0;
        return Math.Clamp((int)Math.Round(distanceKm / 100.0 + 12.0), 1, 999);
    }

    private async Task<int> EstimateServerPingAsync(string ip, double serverLat, double serverLon, CancellationToken ct)
    {
        if (_pingProbe != null)
        {
            int? icmpMs = await _pingProbe.PingAsync(ip, 1000, ct).ConfigureAwait(false);
            if (icmpMs.HasValue && icmpMs.Value >= 0)
            {
                return icmpMs.Value;
            }
        }

        if (_clientLatitude.HasValue && _clientLongitude.HasValue && (serverLat != 0 || serverLon != 0))
        {
            double km = CalculateHaversineKm(_clientLatitude.Value, _clientLongitude.Value, serverLat, serverLon);
            return EstimatePingFromDistanceKm(km);
        }

        return 0;
    }

    private static double DegreesToRadians(double deg) => deg * (Math.PI / 180.0);

    private static void ParseLatLong(string? loc, out double lat, out double lon)
    {
        lat = 0;
        lon = 0;
        if (string.IsNullOrWhiteSpace(loc)) return;

        string[] parts = loc.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length >= 2)
        {
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out lat);
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out lon);
        }
    }

    public static string NormalizeCountryToIso(string? country)
    {
        if (string.IsNullOrWhiteSpace(country)) return string.Empty;
        string trimmed = country.Trim();
        if (trimmed.Length == 2) return trimmed.ToUpperInvariant();
        return CountryNameToIso.TryGetValue(trimmed, out string? iso) ? iso : string.Empty;
    }
}
