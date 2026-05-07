using GbfsSystemsImporter.Services;

namespace GbfsSystemsImporter.Api.Jobs;

public class StationStatusJob
{
    private readonly DatabaseService _db;
    private readonly GbfsFeedService _feed;
    private readonly ILogger<StationStatusJob> _log;

    public StationStatusJob(DatabaseService db, GbfsFeedService feed, ILogger<StationStatusJob> log)
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
            _log.LogWarning("Discovery fetch for {SystemId} failed with status {Status}", systemId, disc.StatusCode);
            return;
        }

        var feedUrl = GbfsFeedService.TryFindFeedUrl(disc.Body, "station_status");
        if (feedUrl is null)
        {
            _log.LogInformation("System {SystemId} has no station_status feed", systemId);
            return;
        }

        var resp = await _feed.FetchAsync(feedUrl, ct);
        if (!resp.Success)
        {
            _log.LogWarning("station_status fetch for {SystemId} failed: {Status}", systemId, resp.StatusCode);
            return;
        }

        var statuses = GbfsFeedService.ParseStationStatuses(resp.Body);
        var n = await _db.RecordStationStatusesAsync(systemId, statuses);
        _log.LogInformation("Captured {Count} station statuses for {SystemId}", n, systemId);
    }
}
