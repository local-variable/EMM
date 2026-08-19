using System;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EorzeanMarketMaster.Core.Ingest;

namespace EorzeanMarketMaster.Ingest;

/// <summary>
/// The wire. Everything about which address to ask, how often, and what the answer means lives in
/// Core; what lives here is the HTTP client and the two things only this side can set.
///
/// <b>The connection cap is set on the handler, not only obeyed by the caller.</b> The ingest
/// issues its requests one at a time, so this limit is never reached in the ordinary run of
/// things - which is exactly why it is worth setting. A later ticket that decides to parallelise a
/// sweep will find the ceiling already in the socket layer rather than discovering it was only
/// ever a property of one loop.
///
/// <b>The User-Agent is descriptive on purpose.</b> The aggregator records the agent on every
/// request; it does not enforce anything on it, but an identifiable agent is how its operators
/// diagnose one misbehaving client instead of blocking an address range that a lot of Players
/// share.
/// </summary>
internal sealed class UniversalisTransport : IAggregatorTransport, IDisposable
{
    private readonly HttpClient client;

    internal UniversalisTransport()
    {
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = Citizenship.MaxConnections,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,

            // Well under the fifteen-minute sweep floor, so a stalled connection can never hold a
            // sweep open across the window that was meant to bound it.
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };

        client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    /// <summary>
    /// What EMM tells the aggregator it is. UNAPPROVED COPY - it is a public statement of
    /// identity, sent on every request.
    /// </summary>
    internal static string UserAgent { get; } =
        $"EorzeanMarketMaster/{Assembly.GetExecutingAssembly().GetName().Version} " +
        "(+https://github.com/local-variable/EMM)";

    /// <inheritdoc/>
    public async Task<TransportResult> Get(Uri address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);

        try
        {
            using var response = await client
                .GetAsync(address, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return TransportResult.Failed($"{(int)response.StatusCode} {response.ReasonPhrase}");
            }

            // Read the bytes and decode them here rather than asking for a string, so that the
            // figure reported as the cost of a refresh is what crossed the wire and not the
            // character count of what came out the other side.
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            return TransportResult.Ok(Encoding.UTF8.GetString(bytes), bytes.Length);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // No network, no DNS, a timeout, a proxy in the way. All of them are the same thing to
            // EMM - the refresh did not happen and the store still holds what it held.
            return TransportResult.Failed(ex.Message);
        }
    }

    /// <inheritdoc/>
    public void Dispose() => client.Dispose();
}

/// <summary>
/// Pacing, in the only way a running plugin can do it. Core decides how long to wait; this waits.
/// </summary>
internal sealed class RealPacing : IPacing
{
    /// <inheritdoc/>
    public Task Wait(TimeSpan duration, CancellationToken cancellationToken) =>
        Task.Delay(duration, cancellationToken);
}
