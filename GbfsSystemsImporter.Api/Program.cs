using GbfsSystemsImporter.Api.Endpoints;
using GbfsSystemsImporter.Api.Jobs;
using GbfsSystemsImporter.Services;
using Hangfire;
using Hangfire.Dashboard;
using Hangfire.SqlServer;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

var sql = builder.Configuration.GetSection("Sql");
var host = sql["Host"] ?? "localhost";
var port = int.Parse(sql["Port"] ?? "51919");
var password = sql["Password"]
    ?? Environment.GetEnvironmentVariable("GBFS_SQL_PASSWORD")
    ?? "Yukon2025!Gbfs";

var serverConn = DatabaseService.BuildConnectionString(host, port, password);
var dbConn = DatabaseService.BuildConnectionString(host, port, password, "GbfsSystems");

var bootstrap = new DatabaseService(serverConn);
bootstrap.WaitUntilReadyAsync(TimeSpan.FromMinutes(2)).GetAwaiter().GetResult();
bootstrap.EnsureDatabaseAndTableAsync().GetAwaiter().GetResult();

builder.Services.AddSingleton<DatabaseService>(_ => new DatabaseService(serverConn));
builder.Services.AddSingleton<GbfsFeedService>();
builder.Services.AddScoped<StationStatusJob>();
builder.Services.AddScoped<StationsImportJob>();
builder.Services.AddScoped<GeofencingZonesImportJob>();
builder.Services.AddScoped<FreeBikeStatusJob>();
builder.Services.AddScoped<FanOutJobs>();

builder.Services.AddHangfire(cfg => cfg
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(dbConn, new SqlServerStorageOptions
    {
        SchemaName = "HangFire",
        PrepareSchemaIfNecessary = true,
        QueuePollInterval = TimeSpan.FromSeconds(5),
        SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
        UseRecommendedIsolationLevel = true,
        DisableGlobalLocks = true,
    }));

builder.Services.AddHangfireServer(opt => opt.WorkerCount = 2);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
app.UseHttpsRedirection();

app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = app.Environment.IsDevelopment()
        ? new IDashboardAuthorizationFilter[] { new AllowAllDashboardFilter() }
        : new IDashboardAuthorizationFilter[] { new LocalRequestsOnlyAuthorizationFilter() },
});

app.MapGet("/", () => Results.Redirect("/hangfire"));
app.MapStationStatusJobEndpoints();
app.MapFanOutJobEndpoints();

RegisterFanOutRecurringJobs(builder.Configuration);

app.Run();

static void RegisterFanOutRecurringJobs(IConfiguration cfg)
{
    var section = cfg.GetSection("FanOutJobs");
    var stationsCron     = section["StationsCron"]     ?? "0 */6 * * *";
    var statusesCron     = section["StatusesCron"]     ?? "*/5 * * * *";
    var zonesCron        = section["ZonesCron"]        ?? "0 */6 * * *";
    var freeBikesCron    = section["FreeBikesCron"]    ?? "*/5 * * * *";

    RecurringJob.AddOrUpdate<FanOutJobs>("fanout-stations",
        j => j.FanOutStationsAsync(CancellationToken.None), stationsCron);
    RecurringJob.AddOrUpdate<FanOutJobs>("fanout-station-statuses",
        j => j.FanOutStationStatusesAsync(CancellationToken.None), statusesCron);
    RecurringJob.AddOrUpdate<FanOutJobs>("fanout-geofencing-zones",
        j => j.FanOutGeofencingZonesAsync(CancellationToken.None), zonesCron);
    RecurringJob.AddOrUpdate<FanOutJobs>("fanout-free-bike-statuses",
        j => j.FanOutFreeBikeStatusesAsync(CancellationToken.None), freeBikesCron);
}

internal sealed class AllowAllDashboardFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context) => true;
}
