using System.Net.NetworkInformation;

namespace BaBaBooeyStrap.Integrations.Matchmaking;

public interface IPingProbe
{
    Task<int?> PingAsync(string ip, int timeoutMs, CancellationToken ct = default);
}

public sealed class SystemPingProbe : IPingProbe
{
    public async Task<int?> PingAsync(string ip, int timeoutMs, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ip)) return null;

        try
        {
            using var ping = new Ping();
            ct.ThrowIfCancellationRequested();
            PingReply reply = await ping.SendPingAsync(ip, timeoutMs).WaitAsync(ct).ConfigureAwait(false);
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

        return null;
    }
}
