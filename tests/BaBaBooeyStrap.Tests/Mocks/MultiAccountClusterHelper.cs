using System.Net;
using System.Text;
using BaBaBooeyStrap.MultiInstance.Abstractions;

namespace BaBaBooeyStrap.Tests.Mocks;

public static class MultiAccountClusterHelper
{
    public static void SetupDynamicMultiAccountAuth(
        MockHttpMessageHandler handler,
        IReadOnlyDictionary<string, string> cookieToTicketMap)
    {
        handler.WhenPost("auth.roblox.com/v1/authentication-ticket")
            .RespondWith((req, ct) =>
            {
                req.Headers.TryGetValues("Cookie", out var cookies);
                string cookieHeader = cookies != null ? string.Join("; ", cookies) : string.Empty;

                req.Headers.TryGetValues("X-CSRF-TOKEN", out var csrf);
                string? token = csrf?.FirstOrDefault();

                if (string.IsNullOrEmpty(token))
                {
                    var forbidden = new HttpResponseMessage(HttpStatusCode.Forbidden)
                    {
                        Content = new StringContent("{\"errors\":[{\"code\":0,\"message\":\"Token Validation Failed\"}]}", Encoding.UTF8, "application/json")
                    };
                    forbidden.Headers.Add("x-csrf-token", "dynamic_csrf_valid");
                    return Task.FromResult(forbidden);
                }

                // Match specific account cookie to unique auth ticket
                string matchingTicket = "default_ticket";
                foreach (var (cookieSub, ticket) in cookieToTicketMap)
                {
                    if (cookieHeader.Contains(cookieSub))
                    {
                        matchingTicket = ticket;
                        break;
                    }
                }

                var ok = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                };
                ok.Headers.Add("rbx-authentication-ticket", matchingTicket);
                return Task.FromResult(ok);
            });
    }

    public static Func<IReadOnlyList<ClientWindowInfo>> CreateSimulatedClusterWindowProbe(
        params (int Pid, nint Hwnd, bool Visible)[] windows)
    {
        return () => windows.Select(w => new ClientWindowInfo(w.Pid, w.Hwnd, w.Visible)).ToList();
    }
}
