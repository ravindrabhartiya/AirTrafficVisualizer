namespace ATC.DashboardApi;

using ATC.Shared;
using Microsoft.AspNetCore.SignalR;

public sealed class FlightHub : Hub
{
    private readonly ILogger<FlightHub> _logger;

    public FlightHub(ILogger<FlightHub> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Called by the Tracking Engine to broadcast a flight position update to all dashboard clients.
    /// </summary>
    public async Task BroadcastFlightUpdate(FlightPosition position)
    {
        await Clients.All.SendAsync("FlightUpdated", position);
    }

    public override Task OnConnectedAsync()
    {
        _logger.LogInformation("Dashboard client connected: {ConnectionId}", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Dashboard client disconnected: {ConnectionId}", Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
