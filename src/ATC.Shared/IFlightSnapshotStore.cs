namespace ATC.Shared;

/// <summary>
/// Persistence abstraction for flight snapshot data.
/// Allows the TrackingEngine to persist flights and the DashboardApi to load them on cold start.
/// </summary>
public interface IFlightSnapshotStore
{
    Task UpsertAsync(FlightPosition position);
    Task<IReadOnlyList<FlightPosition>> LoadAllAsync(TimeSpan maxAge);
    Task PurgeStaleAsync(TimeSpan maxAge);
}
