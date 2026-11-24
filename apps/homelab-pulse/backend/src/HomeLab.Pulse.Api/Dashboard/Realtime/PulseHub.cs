using Microsoft.AspNetCore.SignalR;

namespace HomeLab.Pulse.Api.Dashboard.Realtime;

internal sealed class PulseHub : Hub<IPulseClient>
{
}
