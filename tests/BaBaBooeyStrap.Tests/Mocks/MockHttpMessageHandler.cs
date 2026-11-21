using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BaBaBooeyStrap.Tests.Mocks;

/// <summary>
/// A deterministic, programmable HTTP message handler for mocking Roblox Web APIs,
/// CDN package downloads, CSRF handshakes, and external microservices.
/// </summary>
public sealed class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly List<HttpRequestMessage> _recordedRequests = new();
    private readonly List<HttpRouteRule> _rules = new();
    private readonly object _lock = new();

    public IReadOnlyList<HttpRequestMessage> RecordedRequests
    {
        get { lock (_lock) return _recordedRequests.ToList(); }
    }

    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? FallbackHandler { get; set; }

    public MockHttpMessageHandler()
    {
        FallbackHandler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            RequestMessage = req,
            Content = new StringContent($"No mock rule matched for {req.Method} {req.RequestUri}")
        });
    }

    public HttpClient ToHttpClient(bool disposeHandler = false) => new(this, disposeHandler);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _recordedRequests.Add(request);
        }

        HttpRouteRule? matchedRule;
        lock (_lock)
        {
            matchedRule = _rules.LastOrDefault(r => r.Matches(request));
        }

        if (matchedRule != null)
        {
            return await matchedRule.Handler(request, cancellationToken);
        }

        if (FallbackHandler != null)
        {
            return await FallbackHandler(request, cancellationToken);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            RequestMessage = request,
            Content = new StringContent($"Unhandled mock request: {request.Method} {request.RequestUri}")
        };
    }

    // --- Fluent Routing API ---

    public HttpRouteRule When(HttpMethod method, string urlSubstring)
    {
        var rule = new HttpRouteRule(method, urlSubstring);
        lock (_lock)
        {
            _rules.Add(rule);
        }
        return rule;
    }

    public HttpRouteRule WhenAny(string urlSubstring)
    {
        var rule = new HttpRouteRule(null, urlSubstring);
        lock (_lock)
        {
            _rules.Add(rule);
        }
        return rule;
    }

    public HttpRouteRule WhenGet(string urlSubstring) => When(HttpMethod.Get, urlSubstring);
    public HttpRouteRule WhenPost(string urlSubstring) => When(HttpMethod.Post, urlSubstring);

    public void SetupRoute(string urlSubstring, Func<HttpResponseMessage> responseFactory)
    {
        WhenAny(urlSubstring).RespondWith((req, ct) => Task.FromResult(responseFactory()));
    }

    // --- Pre-Packaged Roblox API Workflows ---

    /// <summary>
    /// Configures the Roblox two-step CSRF challenge-response handshake for auth ticket acquisition:
    /// 1. First request without matching X-CSRF-TOKEN -> HTTP 403 with x-csrf-token header.
    /// 2. Second request with valid X-CSRF-TOKEN -> HTTP 200 with rbx-authentication-ticket header.
    /// </summary>
    public MockHttpMessageHandler SetupAuthTicketWorkflow(
        string validCookie = ".ROBLOSECURITY=_test_cookie_value_",
        string csrfToken = "mock_csrf_token_abc123",
        string authTicket = "mock_auth_ticket_xyz789")
    {
        WhenPost("auth.roblox.com/v1/authentication-ticket")
            .RespondWith((req, ct) =>
            {
                // Verify cookie
                req.Headers.TryGetValues("Cookie", out var cookieHeaders);
                string cookies = cookieHeaders != null ? string.Join("; ", cookieHeaders) : string.Empty;
                if (!cookies.Contains(".ROBLOSECURITY="))
                {
                    var unauthorizedResp = new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    {
                        Content = new StringContent("{\"errors\":[{\"code\":0,\"message\":\"Unauthorized\"}]}", Encoding.UTF8, "application/json")
                    };
                    return Task.FromResult(unauthorizedResp);
                }

                // Check CSRF token
                req.Headers.TryGetValues("X-CSRF-TOKEN", out var csrfHeaders);
                string? clientCsrf = csrfHeaders?.FirstOrDefault();

                if (string.IsNullOrEmpty(clientCsrf) || clientCsrf != csrfToken)
                {
                    // Challenge: 403 Forbidden with token header
                    var forbiddenResp = new HttpResponseMessage(HttpStatusCode.Forbidden)
                    {
                        Content = new StringContent("{\"errors\":[{\"code\":0,\"message\":\"Token Validation Failed\"}]}", Encoding.UTF8, "application/json")
                    };
                    forbiddenResp.Headers.Add("x-csrf-token", csrfToken);
                    return Task.FromResult(forbiddenResp);
                }

                // Success: 200 OK with ticket header
                var okResp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                };
                okResp.Headers.Add("rbx-authentication-ticket", authTicket);
                return Task.FromResult(okResp);
            });

        return this;
    }

    /// <summary>
    /// Configures private server share link resolution endpoint (apis.roblox.com/sharelinks/v1/resolve-link).
    /// </summary>
    public MockHttpMessageHandler SetupShareLinkWorkflow(string linkId, long placeId, string linkCode)
    {
        WhenPost("apis.roblox.com/sharelinks/v1/resolve-link")
            .RespondWith((req, ct) =>
            {
                var body = new
                {
                    statusCode = 200,
                    placeId = placeId,
                    linkCode = linkCode
                };
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
                };
                return Task.FromResult(resp);
            });

        return this;
    }

    /// <summary>
    /// Configures Roblox deployment version resolution endpoint for a channel.
    /// </summary>
    public MockHttpMessageHandler SetupChannelVersionWorkflow(
        string channel = "live",
        string versionGuid = "version-4ea8e85481e34f46",
        string versionNumber = "0.640.0.6400599")
    {
        WhenGet($"clientsettingscdn.roblox.com/v2/client-version/WindowsPlayer/channel/{channel}")
            .RespondWithJson(new
            {
                version = versionNumber,
                clientVersionUpload = versionGuid,
                bootstrapperVersion = "1, 6, 0, 599"
            });

        return this;
    }

    /// <summary>
    /// Configures CDN manifest and package payload downloads.
    /// Automatically calculates valid MD5 hashes and generates manifest triplets.
    /// </summary>
    public MockHttpMessageHandler SetupCdnDeployment(
        string cdnHost,
        string versionGuid,
        IReadOnlyDictionary<string, byte[]> packages)
    {
        // 1. Ping / versionStudio
        WhenGet($"{cdnHost}/versionStudio")
            .RespondWithString(versionGuid);

        // 2. Manifest: <cdn>/<versionGuid>-rbxPkgManifest.txt
        var manifestBuilder = new StringBuilder();
        manifestBuilder.AppendLine("v0");

        foreach (var (pkgName, payload) in packages)
        {
            string md5 = Convert.ToHexString(MD5.HashData(payload)).ToLowerInvariant();
            manifestBuilder.AppendLine(pkgName);
            manifestBuilder.AppendLine(md5);
            manifestBuilder.AppendLine(payload.Length.ToString());

            // 3. Package download: <cdn>/<versionGuid>-<packageName>
            WhenGet($"{cdnHost}/{versionGuid}-{pkgName}")
                .RespondWithBytes(payload);
        }

        WhenGet($"{cdnHost}/{versionGuid}-rbxPkgManifest.txt")
            .RespondWithString(manifestBuilder.ToString());

        return this;
    }

    // --- Verification Assertions ---

    public void AssertCalled(HttpMethod method, string urlSubstring, int expectedTimes = 1)
    {
        lock (_lock)
        {
            int count = _recordedRequests.Count(r => r.Method == method && r.RequestUri != null && r.RequestUri.ToString().Contains(urlSubstring, StringComparison.OrdinalIgnoreCase));
            if (count != expectedTimes)
            {
                throw new InvalidOperationException($"Expected {expectedTimes} call(s) to {method} *{urlSubstring}*, but received {count}.");
            }
        }
    }

    public void AssertHeader(string urlSubstring, string headerName, string expectedValue)
    {
        lock (_lock)
        {
            var req = _recordedRequests.FirstOrDefault(r => r.RequestUri != null && r.RequestUri.ToString().Contains(urlSubstring, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"No request recorded matching *{urlSubstring}*.");

            if (!req.Headers.TryGetValues(headerName, out var values) || !values.Contains(expectedValue))
            {
                throw new InvalidOperationException($"Request to *{urlSubstring}* missing header {headerName}={expectedValue}.");
            }
        }
    }

    public void ClearHistory()
    {
        lock (_lock)
        {
            _recordedRequests.Clear();
        }
    }
}

public sealed class HttpRouteRule
{
    private readonly HttpMethod? _method;
    private readonly string _urlSubstring;
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Handler { get; private set; }

    public HttpRouteRule(HttpMethod? method, string urlSubstring)
    {
        _method = method;
        _urlSubstring = urlSubstring;
        Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    public bool Matches(HttpRequestMessage request)
    {
        if (_method != null && request.Method != _method) return false;
        string? url = request.RequestUri?.ToString();
        return url != null && url.Contains(_urlSubstring, StringComparison.OrdinalIgnoreCase);
    }

    public void RespondWith(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) => Handler = handler;

    public void RespondWithString(string text, string mediaType = "text/plain", HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(text, Encoding.UTF8, mediaType)
        });
    }

    public void RespondWithJson<T>(T obj, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        string json = JsonSerializer.Serialize(obj);
        Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
    }

    public void RespondWithBytes(byte[] data, string mediaType = "application/octet-stream", HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new ByteArrayContent(data)
            {
                Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType) }
            }
        });
    }

    public void RespondWithStatus(HttpStatusCode statusCode)
    {
        Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(statusCode));
    }
}
