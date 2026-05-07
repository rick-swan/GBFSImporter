using GbfsSystemsImporter.Services;

namespace GbfsSystemsImporter.Api.Jobs;

public class StationsImportJob
{
    private readonly DatabaseService _db;
    private readonly GbfsFeedService _feed;
    private readonly ILogger<StationsImportJob> _log;

    public StationsImportJob(DatabaseService db, GbfsFeedService feed, ILogger<StationsImportJob> log)
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

        var feedUrl = GbfsFeedService.TryFindFeedUrl(disc.Body, "station_information");
        if (feedUrl is null)
        {
            _log.LogInformation("System {SystemId} has no station_information feed", systemId);
            return;
        }

        var resp = await _feed.FetchAsync(feedUrl, ct);
        if (!resp.Success)
        {
            _log.LogWarning("station_information fetch for {SystemId} failed: {Status}", systemId, resp.StatusCode);
            return;
        }

        var stations = GbfsFeedService.ParseStationInformation(resp.Body);
        var inserted = await _db.ReplaceStationsAsync(systemId, stations);
        _log.LogInformation("Imported {Count} stations for {SystemId}", inserted, systemId);
    }
}
