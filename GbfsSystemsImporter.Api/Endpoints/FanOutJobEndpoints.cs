using GbfsSystemsImporter.Api.Jobs;
using Hangfire;

namespace GbfsSystemsImporter.Api.Endpoints;

public static class FanOutJobEndpoints
{
    public static void MapFanOutJobEndpoints(this WebApplication app)
    {
        var grp = app.MapGroup("/api/jobs/fanout").WithTags("fan-out jobs");

        grp.MapPost("/stations", () =>
        {
            var id = BackgroundJob.Enqueue<FanOutJobs>(j => j.FanOutStationsAsync(CancellationToken.None));
            return Results.Ok(new { hangfireJobId = id });
        }).WithSummary("Trigger immediate fan-out of station_information imports for all enabled systems");

        grp.MapPost("/station-statuses", () =>
        {
            var id = BackgroundJob.Enqueue<FanOutJobs>(j => j.FanOutStationStatusesAsync(CancellationToken.None));
            return Results.Ok(new { hangfireJobId = id });
        }).WithSummary("Trigger immediate fan-out of station_status capture for all enabled systems");

        grp.MapPost("/geofencing-zones", () =>
        {
            var id = BackgroundJob.Enqueue<FanOutJobs>(j => j.FanOutGeofencingZonesAsync(CancellationToken.None));
            return Results.Ok(new { hangfireJobId = id });
        }).WithSummary("Trigger immediate fan-out of geofencing_zones imports for all enabled systems");

        grp.MapPost("/free-bike-statuses", () =>
        {
            var id = BackgroundJob.Enqueue<FanOutJobs>(j => j.FanOutFreeBikeStatusesAsync(CancellationToken.None));
            return Results.Ok(new { hangfireJobId = id });
        }).WithSummary("Trigger immediate fan-out of free_bike_status capture for all enabled systems");
    }
}
