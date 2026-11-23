using System.Net;
using System.Net.NetworkInformation;

namespace BaBaBooeyStrap.Integrations.Matchmaking;

public interface IPingProbe
{
    Task<int?> PingAsync(string ip, int timeoutMs, CancellationToken ct = default);
}

public sealed class SystemPingProbe : IPingProbe
{
    private static readonly SemaphoreSlim ConcurrencyGate = new(8, 8);

    public async Task<int?> PingAsync(string ip, int timeoutMs, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ip) || !IPAddress.TryParse(ip.Trim(), out IPAddress? address))
        {
            return null;
        }

        await ConcurrencyGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var ping = new Ping();
            using var cancelRegistration = ct.Register(static state =>
            {
                try { ((Ping)state!).SendAsyncCancel(); } catch { }
            }, ping);

            ct.ThrowIfCancellationRequested();
            PingReply reply = await ping.SendPingAsync(address, timeoutMs).WaitAsync(ct).ConfigureAwait(false);
            if (reply.Status == IPStatus.Success && reply.RoundtripTime >= 0)
            {
                return (int)Math.Min(reply.RoundtripTime, int.MaxValue);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // ICMP blocked or unreachable -> caller falls back to Haversine estimation
        }
        finally
        {
            ConcurrencyGate.Release();
        }

        return null;
    }
}
