using HomeLab.Pulse.Api.Dashboard.Overview;

namespace HomeLab.Pulse.Api.Dashboard.Realtime;

public interface IPulseClient
{
    Task TelemetryUpdated(OverviewSnapshot snapshot, CancellationToken cancellationToken);
}
