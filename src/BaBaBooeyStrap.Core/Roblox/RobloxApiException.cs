using System.Net;

namespace BaBaBooeyStrap.Core.Roblox;

public class RobloxApiException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public string? ResponseBody { get; }

    public RobloxApiException(HttpStatusCode statusCode, string message, string? responseBody = null, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }
}
