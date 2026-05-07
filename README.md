# GBFS Systems Importer

A small .NET 10 console app that imports the GBFS [`systems.csv`](https://github.com/MobilityData/gbfs/blob/master/systems.csv) into a SQL Server 2025 instance running in Docker, then lets you query and filter it from the command line.

## Stack

- .NET 10 console app (`GbfsSystemsImporter`)
- SQL Server 2025 (`mcr.microsoft.com/mssql/server:2025-latest`) in Docker
- `Microsoft.Data.SqlClient` for connectivity + `SqlBulkCopy` for fast inserts
- `CsvHelper` for parsing
- `Spectre.Console` for nice tabular output

## Connection

The container is published on a randomly chosen host port:

| Setting   | Value                  |
|-----------|------------------------|
| Host      | `localhost`            |
| Port      | **`51919`**            |
| User      | `sa`                   |
| Password  | `Yukon2025!Gbfs`       |
| Database  | `GbfsSystems`          |
| Table     | `dbo.Systems`          |

Override any of these with `--host`, `--port`, `--password`, or the env var `GBFS_SQL_PASSWORD`. The host port is also taken from `GBFS_SQL_PORT` when running `docker compose up`.

## First-time setup

```bash
# 1. Start SQL Server 2025 in Docker
GBFS_SQL_PORT=51919 docker compose up -d

# 2. Build the app
dotnet build

# 3. Create the database/table and import the CSV
dotnet run --project GbfsSystemsImporter -- import --truncate
```

`import` will create the `GbfsSystems` database and `dbo.Systems` table if they don't already exist, so you don't need a separate `init` step. Use `--truncate` to wipe the table before re-importing.

## Commands

```text
db-up                     start the SQL Server container
db-down                   stop the container
init                      create the database and table only
import [--file <path>]    import the CSV (defaults to /Users/ricky/Downloads/systems (1).csv)
       [--truncate]       wipe the table first
count                     show row count
query [filters]           query rows
group <column>            count rows grouped by a column
fetch <system-id>         fetch the system's GBFS data over HTTP and display it
import-stations <id>      import the station_information feed for one system into dbo.Stations
stations <id>             list stored stations for a system
```

### `fetch` flags

| Flag             | Effect                                                                 |
|------------------|------------------------------------------------------------------------|
| (positional)     | the `system-id` to look up in the DB                                   |
| `--feed <name>`  | follow a feed listed in the gbfs.json (e.g. `station_information`)     |
| `--list-feeds`   | print the feed names available in the discovery doc and exit          |
| `--homepage`     | use the operator's homepage URL instead of the auto-discovery URL      |
| `--raw`          | skip JSON pretty-printing                                              |
| `--save <path>`  | write the response body to a file instead of stdout                    |

### Query filters

| Flag                 | Match    | Example                         |
|----------------------|----------|---------------------------------|
| `--country <CC>`     | exact    | `--country GB`                  |
| `--name <s>`         | contains | `--name dott`                   |
| `--location <s>`     | contains | `--location berlin`             |
| `--system-id <id>`   | exact    | `--system-id careem_bike`       |
| `--auth-type <t>`    | exact    | `--auth-type api_key`           |
| `--version <v>`      | contains | `--version 3.0`                 |
| `--top <n>`          | limit    | `--top 25` (default 100)        |

Filters are AND-combined. `group` accepts: `CountryCode`, `AuthenticationType`, `Location`, `SupportedVersions`.

## Examples

```bash
dotnet run --project GbfsSystemsImporter -- count
dotnet run --project GbfsSystemsImporter -- query --country GB --top 20
dotnet run --project GbfsSystemsImporter -- query --name dott
dotnet run --project GbfsSystemsImporter -- query --version 3.0 --top 50
dotnet run --project GbfsSystemsImporter -- group CountryCode
dotnet run --project GbfsSystemsImporter -- group --by AuthenticationType

dotnet run --project GbfsSystemsImporter -- fetch careem_bike
dotnet run --project GbfsSystemsImporter -- fetch careem_bike --list-feeds
dotnet run --project GbfsSystemsImporter -- fetch careem_bike --feed station_information
dotnet run --project GbfsSystemsImporter -- fetch careem_bike --feed system_information --save out.json

dotnet run --project GbfsSystemsImporter -- import-stations careem_bike
dotnet run --project GbfsSystemsImporter -- stations careem_bike --top 20
```

## Schema

```sql
CREATE TABLE dbo.Systems (
    Id                          INT IDENTITY(1,1) PRIMARY KEY,
    CountryCode                 NVARCHAR(8)     NULL,
    Name                        NVARCHAR(256)   NULL,
    Location                    NVARCHAR(256)   NULL,
    SystemId                    NVARCHAR(128)   NULL,
    Url                         NVARCHAR(1024)  NULL,
    AutoDiscoveryUrl            NVARCHAR(1024)  NULL,
    SupportedVersions           NVARCHAR(256)   NULL,
    AuthenticationInfoUrl       NVARCHAR(1024)  NULL,
    AuthenticationType          NVARCHAR(128)   NULL,
    AuthenticationParameterName NVARCHAR(256)   NULL,
    ImportedAtUtc               DATETIME2(0)    NOT NULL DEFAULT (SYSUTCDATETIME())
);
```

Indexes on `CountryCode`, `AuthenticationType`, and a `UNIQUE` constraint on `SystemId` (so it can act as a FK target).

```sql
CREATE TABLE dbo.Stations (
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    SystemId            NVARCHAR(128)   NOT NULL,
    StationId           NVARCHAR(128)   NOT NULL,
    Name                NVARCHAR(512)   NULL,
    ShortName           NVARCHAR(256)   NULL,
    Lat                 FLOAT           NOT NULL,
    Lon                 FLOAT           NOT NULL,
    Address             NVARCHAR(512)   NULL,
    CrossStreet         NVARCHAR(256)   NULL,
    RegionId            NVARCHAR(128)   NULL,
    PostCode            NVARCHAR(32)    NULL,
    StationType         NVARCHAR(64)    NULL,
    Capacity            INT             NULL,
    IsVirtualStation    BIT             NULL,
    IsValetStation      BIT             NULL,
    IsChargingStation   BIT             NULL,
    ParkingType         NVARCHAR(64)    NULL,
    ParkingHoop         BIT             NULL,
    ContactPhone        NVARCHAR(64)    NULL,
    RentalMethods       NVARCHAR(512)   NULL,
    RentalUris          JSON            NULL,   -- SQL Server 2025 native JSON type
    RawJson             JSON            NULL,   -- raw station object (any extra GBFS fields)
    FetchedAtUtc        DATETIME2(0)    NOT NULL DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT FK_Stations_Systems FOREIGN KEY (SystemId)
        REFERENCES dbo.Systems(SystemId) ON DELETE CASCADE,
    CONSTRAINT UQ_Stations_SystemId_StationId UNIQUE (SystemId, StationId)
);
```

Query JSON sub-fields with `JSON_VALUE`:

```sql
SELECT TOP 3 StationId, Name,
       JSON_VALUE(RentalUris, '$.android') AS AndroidUri,
       JSON_VALUE(RentalUris, '$.ios')     AS IosUri
FROM dbo.Stations
WHERE SystemId = 'careem_bike';
```

`import-stations` is idempotent per system: it deletes existing rows for that `SystemId` and re-inserts inside a transaction.

## Web app (Blazor SSR map)

The `GbfsSystemsImporter.Web` project is a Blazor Web App scaffolded with **`--interactivity None`** (pure server-side rendering — no SignalR, no WebAssembly, no API endpoints). The `/map` page renders a dropdown of every system that has stations imported and shows their locations on a Google Map.

The dropdown is a regular HTML `<select>` inside a GET form (`onchange="this.form.submit()"`), so the selected system arrives back as a query-string parameter on a full page navigation. The server queries the DB, embeds the stations as JSON inside `<script type="application/json">`, and the map JS reads that and drops markers.

### Where to put your Google Maps API key

Pick one of these (in order of preference for local dev):

**1. User secrets (recommended for local dev — never committed):**

```bash
cd GbfsSystemsImporter.Web
dotnet user-secrets init
dotnet user-secrets set "GoogleMaps:ApiKey" "YOUR_KEY_HERE"
```

**2. Environment variable (the `__` is required as the section separator):**

```bash
export GoogleMaps__ApiKey="YOUR_KEY_HERE"
dotnet run --project GbfsSystemsImporter.Web
```

**3. `appsettings.Development.json` (committed if you forget — be careful):**

```json
{
  "GoogleMaps": { "ApiKey": "YOUR_KEY_HERE" }
}
```

The key needs the **Maps JavaScript API** enabled in Google Cloud Console. Restrict it to your `localhost` origin while developing.

### Run it

```bash
GBFS_SQL_PORT=51919 docker compose up -d                            # if not already up
dotnet run --project GbfsSystemsImporter -- import-stations careem_bike
dotnet user-secrets --project GbfsSystemsImporter.Web set "GoogleMaps:ApiKey" "YOUR_KEY"
dotnet run --project GbfsSystemsImporter.Web
```

Then open the URL printed by Kestrel (defaults to `http://localhost:5037`) and visit **`/map`**. Pick a system from the dropdown — the map re-renders with all that system's station markers, fitted to the bounds.

If you haven't imported stations for any system yet, the page shows a hint pointing at the `import-stations` command. If you forgot the API key, it shows a banner with the user-secrets command.

## API + Hangfire (recurring station_status capture)

The `GbfsSystemsImporter.Api` project is a minimal-API ASP.NET project with **Hangfire** (`Hangfire.AspNetCore` + `Hangfire.SqlServer`) that schedules recurring **station_status** captures into a time-series table.

### Schema — `dbo.StationStatuses`

```sql
CREATE TABLE dbo.StationStatuses (
    Id                       BIGINT IDENTITY(1,1) PRIMARY KEY,
    SystemId                 NVARCHAR(128)   NOT NULL,
    StationId                NVARCHAR(128)   NOT NULL,
    NumVehiclesAvailable     INT             NULL,   -- maps GBFS v2 num_bikes_available too
    NumVehiclesDisabled      INT             NULL,
    NumDocksAvailable        INT             NULL,
    NumDocksDisabled         INT             NULL,
    IsInstalled              BIT             NULL,
    IsRenting                BIT             NULL,
    IsReturning              BIT             NULL,
    LastReportedUtc          DATETIME2(0)    NULL,
    VehicleTypesAvailable    JSON            NULL,
    VehicleDocksAvailable    JSON            NULL,
    RawJson                  JSON            NULL,
    FetchedAtUtc             DATETIME2(0)    NOT NULL DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT FK_StationStatuses_Systems
        FOREIGN KEY (SystemId) REFERENCES dbo.Systems(SystemId) ON DELETE CASCADE
);

CREATE INDEX IX_StationStatuses_System_Time
    ON dbo.StationStatuses(SystemId, FetchedAtUtc DESC);
CREATE INDEX IX_StationStatuses_Sys_Station_Time
    ON dbo.StationStatuses(SystemId, StationId, FetchedAtUtc DESC);
```

This is **append-only**. Every capture inserts one row per station, so you build a history of availability over time. Indexes are tuned for `WHERE SystemId=? ORDER BY FetchedAtUtc DESC` access.

### Hangfire setup

- Hangfire stores its own state in the `HangFire` schema of the same `GbfsSystems` database (auto-created on first start)
- Dashboard at **`http://localhost:5198/hangfire`** (in Development the dashboard is open; in any other environment it's restricted to local requests)
- Worker count: 2

### Endpoints

| Method | Route | Body | What it does |
|--------|-------|------|--------------|
| POST   | `/api/jobs/station-status` | `{ "systemId": "...", "cron": "*/5 * * * *" }` | Add or update the recurring job. Cron defaults to every 5 minutes if omitted. |
| DELETE | `/api/jobs/station-status/{systemId}` | — | Remove the recurring job. |
| POST   | `/api/jobs/station-status/{systemId}/trigger` | — | Fire-and-forget one-off capture (returns the Hangfire job id). |
| GET    | `/api/jobs/station-status` | — | List all recurring `station-status-*` jobs with last/next execution. |
| GET    | `/api/jobs/station-status/{systemId}/count` | — | Count of snapshots stored for the system. |

The recurring job id is `station-status-{systemId}` (one job per system, idempotent re-add).

### Run it

```bash
GBFS_SQL_PORT=51919 docker compose up -d   # if not already up
dotnet run --project GbfsSystemsImporter.Api
```

```bash
# trigger once
curl -X POST http://localhost:5198/api/jobs/station-status/careem_bike/trigger

# schedule every 2 minutes
curl -X POST http://localhost:5198/api/jobs/station-status \
  -H 'Content-Type: application/json' \
  -d '{"systemId":"careem_bike","cron":"*/2 * * * *"}'

# how many snapshots stored so far
curl http://localhost:5198/api/jobs/station-status/careem_bike/count

# list scheduled jobs
curl http://localhost:5198/api/jobs/station-status

# stop the recurring job
curl -X DELETE http://localhost:5198/api/jobs/station-status/careem_bike
```

The system must already exist in `dbo.Systems` (i.e. you imported the systems CSV) — the FK enforces that. The system does **not** need to have its `dbo.Stations` populated; the `station_status` job is independent of `import-stations`.

### Querying availability over time

```sql
-- snapshots for one station, newest first
SELECT FetchedAtUtc, NumVehiclesAvailable, NumDocksAvailable, IsRenting
FROM dbo.StationStatuses
WHERE SystemId = 'careem_bike' AND StationId = '10'
ORDER BY FetchedAtUtc DESC;

-- average availability per hour, per station
SELECT StationId,
       DATEPART(hour, FetchedAtUtc) AS HourOfDay,
       AVG(CAST(NumVehiclesAvailable AS FLOAT)) AS AvgAvailable
FROM dbo.StationStatuses
WHERE SystemId = 'careem_bike'
GROUP BY StationId, DATEPART(hour, FetchedAtUtc)
ORDER BY StationId, HourOfDay;
```

## Enabling systems and the fan-out import pipeline

`dbo.Systems` has an `IsEnabled BIT NOT NULL DEFAULT 0` column. Three recurring **fan-out** jobs in the API enqueue a per-system "leaf" job for **each currently enabled system** every time they fire. To start collecting data for a system, just enable it.

### `/systems` page

The Blazor SSR app's **`/systems`** page lists every system in the catalogue with:

- Per-row `Enable` / `Disable` toggle button (single outer `<form @formname="toggle-system">` with antiforgery; each button submits its own `name="ToggleSystemId"` value, which the page handler routes to `DatabaseService.ToggleSystemEnabledAsync`)
- Counts of `Stations`, `GeofencingZones`, and `StationStatuses` for each system
- Filters: `Name` contains, `Location` contains, `Country` (dropdown of distinct codes), `AuthType` (exact), `Status` (`all`/`enabled`/`disabled`)
- 50/page pagination
- "X enabled overall" running counter

Filters propagate through the toggle (via the form's `action="/systems?..."`), so toggling on a filtered view stays filtered.

### Fan-out jobs

Three recurring jobs are auto-registered at API startup with sensible defaults (overridable in `appsettings.json` under `FanOutJobs:*`):

| Job ID                        | Default cron     | What it does |
|-------------------------------|------------------|--------------|
| `fanout-stations`             | `0 */6 * * *`    | Enqueues one `StationsImportJob` leaf per enabled system (re-imports `station_information`) |
| `fanout-station-statuses`     | `*/5 * * * *`    | Enqueues one `StationStatusJob` leaf per enabled system (appends to the time-series) |
| `fanout-geofencing-zones`     | `0 */6 * * *`    | Enqueues one `GeofencingZonesImportJob` leaf per enabled system |

Each leaf does discovery → relevant feed → write. Leaves run on Hangfire workers in parallel up to `WorkerCount`. Per-system trigger endpoints are still available for ad-hoc runs:

```bash
# trigger all 3 fan-outs for all enabled systems immediately
curl -X POST http://localhost:5198/api/jobs/fanout/stations
curl -X POST http://localhost:5198/api/jobs/fanout/station-statuses
curl -X POST http://localhost:5198/api/jobs/fanout/geofencing-zones
```

### Verified flow

1. New systems start with `IsEnabled = 0`. None of the fan-out jobs do anything.
2. Toggling a system on `/systems` flips `IsEnabled` to 1.
3. Next fan-out firing (or manual trigger) sees that system in `ListEnabledSystemIdsAsync()` and enqueues all three leaves for it.
4. Disabling stops future fan-outs but leaves existing data in place.

End-to-end tested: enabling `careem_bike` + `beryl_norwich`, triggering all three fan-outs immediately, observed +205 / +225 rows in `dbo.StationStatuses` (matching their station counts) and `dbo.Stations` / `dbo.GeofencingZones` repopulated.

## Cleanup

```bash
docker compose down          # stop the container
docker compose down -v       # …and delete the persisted volume
```
