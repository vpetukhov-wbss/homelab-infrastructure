using System.Threading.Channels;
using HomeLab.Pulse.Api.Dashboard.Overview;
using Microsoft.AspNetCore.SignalR;

namespace HomeLab.Pulse.Api.Dashboard.Realtime;

internal sealed partial class PulseBroadcastWorker(
    IHubContext<PulseHub, IPulseClient> hub, ILogger<PulseBroadcastWorker> logger) : BackgroundService
{
    private readonly Channel<OverviewSnapshot> _updates = Channel.CreateBounded<OverviewSnapshot>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = true,
    });

    public void Publish(OverviewSnapshot snapshot) => _updates.Writer.TryWrite(snapshot);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var snapshot in _updates.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await hub.Clients.All.TelemetryUpdated(snapshot, stoppingToken);
            }
            catch (Exception error) when (error is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                LogBroadcastFailure(logger, snapshot.Revision, error.GetType().Name);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Overview broadcast for revision {Revision} failed ({FailureType}).")]
    private static partial void LogBroadcastFailure(ILogger logger, long revision, string failureType);
}
