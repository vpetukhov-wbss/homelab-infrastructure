using System.Text.Json;
using System.Threading.Channels;
using HomeLab.Pulse.Api.Dashboard.Realtime;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace HomeLab.Pulse.Api.IntegrationTests;

internal sealed class PulseEvents : IAsyncDisposable
{
    private readonly HubConnection _connection;
    private readonly IDisposable _handler;
    private readonly Channel<JsonElement> _updates = Channel.CreateUnbounded<JsonElement>();

    public PulseEvents(Uri baseAddress)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl(new Uri(baseAddress, "/hubs/pulse"), HttpTransportType.WebSockets)
            .Build();
        _handler = _connection.On<JsonElement>(nameof(IPulseClient.TelemetryUpdated), snapshot =>
        {
            _updates.Writer.TryWrite(snapshot);
        });
    }

    public Task ConnectAsync(CancellationToken cancellationToken) => _connection.StartAsync(cancellationToken);

    public Task DisconnectAsync(CancellationToken cancellationToken) => _connection.StopAsync(cancellationToken);

    public async Task<JsonElement> ReadAfterAsync(long revision, CancellationToken cancellationToken)
    {
        while (true)
        {
            var snapshot = await _updates.Reader.ReadAsync(cancellationToken);
            if (snapshot.GetProperty("revision").GetInt64() > revision)
            {
                return snapshot;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _handler.Dispose();
        await _connection.DisposeAsync();
    }
}
