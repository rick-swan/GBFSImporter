using GbfsSystemsImporter.Services;

namespace GbfsSystemsImporter.Api.Jobs;

public class FreeBikeStatusJob
{
    private readonly DatabaseService _db;
    private readonly GbfsFeedService _feed;
    private readonly ILogger<FreeBikeStatusJob> _log;

    public FreeBikeStatusJob(DatabaseService db, GbfsFeedService feed, ILogger<FreeBikeStatusJob> log)
    {
        _db = db;
        _feed = feed;
        _log = log;
    }

    public async Task CaptureAsync(string systemId, CancellationToken ct)
    {
        var system = await _db.GetBySystemIdAsync(systemId);
        if (system?.AutoDiscoveryUrl is null)
        {
            _log.LogWarning("System {SystemId} not found or has no auto-discovery URL", systemId);
            return;
        }

        var disc = await _feed.FetchAsync(system.AutoDiscoveryUrl, ct);
        if (!disc.Success)
        {
            _log.LogWarning("Discovery fetch for {SystemId} failed with {Status}", systemId, disc.StatusCode);
            return;
        }

        var feedUrl = GbfsFeedService.TryFindFreeBikeStatusUrl(disc.Body);
        if (feedUrl is null)
        {
            _log.LogInformation("System {SystemId} has no free_bike_status / vehicle_status feed", systemId);
            return;
        }

        var resp = await _feed.FetchAsync(feedUrl, ct);
        if (!resp.Success)
        {
            _log.LogWarning("free_bike_status fetch for {SystemId} failed: {Status}", systemId, resp.StatusCode);
            return;
        }

        var statuses = GbfsFeedService.ParseFreeBikeStatuses(resp.Body);
        var n = await _db.RecordFreeBikeStatusesAsync(systemId, statuses);
        _log.LogInformation("Captured {Count} free bike statuses for {SystemId}", n, systemId);
    }
}
