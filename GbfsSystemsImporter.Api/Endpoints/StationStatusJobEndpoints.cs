using GbfsSystemsImporter.Api.Jobs;
using GbfsSystemsImporter.Services;
using Hangfire;
using Hangfire.Storage;

namespace GbfsSystemsImporter.Api.Endpoints;

public static class StationStatusJobEndpoints
{
    public static void MapStationStatusJobEndpoints(this WebApplication app)
    {
        var grp = app.MapGroup("/api/jobs/station-status").WithTags("station-status jobs");

        grp.MapPost("/", (ScheduleRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.SystemId))
                return Results.BadRequest(new { error = "systemId is required" });

            var jobId = JobIdFor(req.SystemId);
            var cron = string.IsNullOrWhiteSpace(req.Cron) ? "*/5 * * * *" : req.Cron;
            RecurringJob.AddOrUpdate<StationStatusJob>(
                jobId,
                j => j.CaptureAsync(req.SystemId, CancellationToken.None),
                cron);
            return Results.Ok(new { jobId, req.SystemId, cron });
        }).WithSummary("Schedule a recurring station_status capture job for a system");

        grp.MapDelete("/{systemId}", (string systemId) =>
        {
            RecurringJob.RemoveIfExists(JobIdFor(systemId));
            return Results.NoContent();
        }).WithSummary("Remove the recurring job for a system");

        grp.MapPost("/{systemId}/trigger", (string systemId) =>
        {
            var hangfireJobId = BackgroundJob.Enqueue<StationStatusJob>(
                j => j.CaptureAsync(systemId, CancellationToken.None));
            return Results.Ok(new { hangfireJobId, systemId });
        }).WithSummary("Run a one-off station_status capture immediately");

        grp.MapGet("/", () =>
        {
            using var conn = JobStorage.Current.GetConnection();
            var jobs = conn.GetRecurringJobs();
            return jobs.Where(j => j.Id.StartsWith("station-status-"))
                       .Select(j => new
                       {
                           j.Id,
                           j.Cron,
                           lastExecution = j.LastExecution,
                           nextExecution = j.NextExecution,
                           j.Queue,
                       });
        }).WithSummary("List configured recurring station_status jobs");

        grp.MapGet("/{systemId}/count", async (string systemId, DatabaseService db) =>
        {
            var count = await db.StationStatusCountAsync(systemId);
            return Results.Ok(new { systemId, count });
        }).WithSummary("Count of station_status snapshots stored for a system");
    }

    private static string JobIdFor(string systemId) => $"station-status-{systemId}";
}

public record ScheduleRequest(string SystemId, string? Cron);
