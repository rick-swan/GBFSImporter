using GbfsSystemsImporter.Services;

namespace GbfsSystemsImporter.Api.Jobs;

public class GeofencingZonesImportJob
{
    private readonly DatabaseService _db;
    private readonly GbfsFeedService _feed;
    private readonly ILogger<GeofencingZonesImportJob> _log;

    public GeofencingZonesImportJob(DatabaseService db, GbfsFeedService feed, ILogger<GeofencingZonesImportJob> log)
    {
        _db = db;
        _feed = feed;
        _log = log;
    }

    public async Task ImportAsync(string systemId, CancellationToken ct)
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

        var feedUrl = GbfsFeedService.TryFindFeedUrl(disc.Body, "geofencing_zones");
        if (feedUrl is null)
        {
            _log.LogInformation("System {SystemId} has no geofencing_zones feed", systemId);
            return;
        }

        var resp = await _feed.FetchAsync(feedUrl, ct);
        if (!resp.Success)
        {
            _log.LogWarning("geofencing_zones fetch for {SystemId} failed: {Status}", systemId, resp.StatusCode);
            return;
        }

        var zones = GbfsFeedService.ParseGeofencingZones(resp.Body);
        var inserted = await _db.ReplaceGeofencingZonesAsync(systemId, zones);
        _log.LogInformation("Imported {Count} geofencing zones for {SystemId}", inserted, systemId);
    }
}
