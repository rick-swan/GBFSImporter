using GbfsSystemsImporter.Services;
using Hangfire;

namespace GbfsSystemsImporter.Api.Jobs;

/// <summary>
/// Master jobs that fan out per-system imports to all enabled systems
/// by enqueueing a leaf job per system. Leaf jobs run on Hangfire workers
/// in parallel up to the configured worker count.
/// </summary>
public class FanOutJobs
{
    private readonly DatabaseService _db;
    private readonly ILogger<FanOutJobs> _log;
    private readonly IBackgroundJobClient _jobs;

    public FanOutJobs(DatabaseService db, IBackgroundJobClient jobs, ILogger<FanOutJobs> log)
    {
        _db = db;
        _jobs = jobs;
        _log = log;
    }

    public async Task FanOutStationsAsync(CancellationToken ct)
    {
        var enabled = await _db.ListEnabledSystemIdsAsync();
        _log.LogInformation("Fan-out stations import for {Count} enabled systems", enabled.Count);
        foreach (var sid in enabled)
            _jobs.Enqueue<StationsImportJob>(j => j.ImportAsync(sid, CancellationToken.None));
    }

    public async Task FanOutStationStatusesAsync(CancellationToken ct)
    {
        var enabled = await _db.ListEnabledSystemIdsAsync();
        _log.LogInformation("Fan-out station_status capture for {Count} enabled systems", enabled.Count);
        foreach (var sid in enabled)
            _jobs.Enqueue<StationStatusJob>(j => j.CaptureAsync(sid, CancellationToken.None));
    }

    public async Task FanOutGeofencingZonesAsync(CancellationToken ct)
    {
        var enabled = await _db.ListEnabledSystemIdsAsync();
        _log.LogInformation("Fan-out geofencing_zones import for {Count} enabled systems", enabled.Count);
        foreach (var sid in enabled)
            _jobs.Enqueue<GeofencingZonesImportJob>(j => j.ImportAsync(sid, CancellationToken.None));
    }

    public async Task FanOutFreeBikeStatusesAsync(CancellationToken ct)
    {
        var enabled = await _db.ListEnabledSystemIdsAsync();
        _log.LogInformation("Fan-out free_bike_status capture for {Count} enabled systems", enabled.Count);
        foreach (var sid in enabled)
            _jobs.Enqueue<FreeBikeStatusJob>(j => j.CaptureAsync(sid, CancellationToken.None));
    }
}
