using System.Data;
using GbfsSystemsImporter.Models;
using Microsoft.Data.SqlClient;

namespace GbfsSystemsImporter.Services;

public class DatabaseService
{
    private readonly string _connectionString;
    private const string DatabaseName = "GbfsSystems";
    private const string TableName = "Systems";

    public DatabaseService(string connectionString) =>
        _connectionString = connectionString;

    public static string BuildConnectionString(string host, int port, string password, string? database = null) =>
        new SqlConnectionStringBuilder
        {
            DataSource = $"{host},{port}",
            UserID = "sa",
            Password = password,
            InitialCatalog = database ?? "master",
            TrustServerCertificate = true,
            Encrypt = true,
            ConnectTimeout = 30,
        }.ConnectionString;

    public async Task WaitUntilReadyAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync(ct);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
        throw new TimeoutException($"SQL Server not ready after {timeout.TotalSeconds:N0}s.", last);
    }

    public async Task EnsureDatabaseAndTableAsync()
    {
        await using (var conn = new SqlConnection(_connectionString))
        {
            await conn.OpenAsync();
            var createDb = $@"
IF DB_ID(N'{DatabaseName}') IS NULL
    CREATE DATABASE [{DatabaseName}];";
            await using var cmd = new SqlCommand(createDb, conn);
            await cmd.ExecuteNonQueryAsync();
        }

        var dbConnString = ReplaceCatalog(_connectionString, DatabaseName);
        await using (var conn = new SqlConnection(dbConnString))
        {
            await conn.OpenAsync();
            var createSystems = $@"
IF OBJECT_ID(N'dbo.{TableName}', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.{TableName} (
        Id                          INT             IDENTITY(1,1) PRIMARY KEY,
        CountryCode                 NVARCHAR(8)     NULL,
        Name                        NVARCHAR(256)   NULL,
        Location                    NVARCHAR(256)   NULL,
        SystemId                    NVARCHAR(128)   NOT NULL,
        Url                         NVARCHAR(1024)  NULL,
        AutoDiscoveryUrl            NVARCHAR(1024)  NULL,
        SupportedVersions           NVARCHAR(256)   NULL,
        AuthenticationInfoUrl       NVARCHAR(1024)  NULL,
        AuthenticationType          NVARCHAR(128)   NULL,
        AuthenticationParameterName NVARCHAR(256)   NULL,
        ImportedAtUtc               DATETIME2(0)    NOT NULL CONSTRAINT DF_Systems_ImportedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT UQ_Systems_SystemId UNIQUE (SystemId)
    );

    CREATE INDEX IX_Systems_CountryCode  ON dbo.{TableName}(CountryCode);
    CREATE INDEX IX_Systems_AuthType     ON dbo.{TableName}(AuthenticationType);
END";
            await using (var cmd = new SqlCommand(createSystems, conn))
                await cmd.ExecuteNonQueryAsync();

            await UpgradeSystemsForFkAsync(conn);
            await AddIsEnabledColumnAsync(conn);
            await CreateStationsTableAsync(conn);
            await CreateStationStatusesTableAsync(conn);
            await CreateGeofencingZonesTableAsync(conn);
            await CreateFreeBikeStatusesTableAsync(conn);
        }
    }

    private async Task CreateFreeBikeStatusesTableAsync(SqlConnection conn)
    {
        const string sql = @"
IF OBJECT_ID(N'dbo.FreeBikeStatuses', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.FreeBikeStatuses (
        Id                    BIGINT          IDENTITY(1,1) PRIMARY KEY,
        SystemId              NVARCHAR(128)   NOT NULL,
        VehicleId             NVARCHAR(128)   NOT NULL,
        Lat                   FLOAT           NULL,
        Lon                   FLOAT           NULL,
        IsReserved            BIT             NULL,
        IsDisabled            BIT             NULL,
        VehicleTypeId         NVARCHAR(128)   NULL,
        CurrentRangeMeters    FLOAT           NULL,
        CurrentFuelPercent    FLOAT           NULL,
        StationId             NVARCHAR(128)   NULL,
        PricingPlanId         NVARCHAR(128)   NULL,
        LastReportedUtc       DATETIME2(0)    NULL,
        RentalUris            JSON            NULL,
        RawJson               JSON            NULL,
        FetchedAtUtc          DATETIME2(0)    NOT NULL CONSTRAINT DF_FreeBikeStatuses_FetchedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_FreeBikeStatuses_Systems
            FOREIGN KEY (SystemId) REFERENCES dbo.Systems(SystemId) ON DELETE CASCADE
    );

    CREATE INDEX IX_FreeBikeStatuses_System_Time
        ON dbo.FreeBikeStatuses(SystemId, FetchedAtUtc DESC);
    CREATE INDEX IX_FreeBikeStatuses_Sys_Vehicle_Time
        ON dbo.FreeBikeStatuses(SystemId, VehicleId, FetchedAtUtc DESC);
END";
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task AddIsEnabledColumnAsync(SqlConnection conn)
    {
        const string sql = @"
IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE name = 'IsEnabled' AND object_id = OBJECT_ID('dbo.Systems'))
BEGIN
    ALTER TABLE dbo.Systems
        ADD IsEnabled BIT NOT NULL CONSTRAINT DF_Systems_IsEnabled DEFAULT (0);
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_Systems_IsEnabled' AND object_id = OBJECT_ID('dbo.Systems'))
BEGIN
    CREATE INDEX IX_Systems_IsEnabled ON dbo.Systems(IsEnabled) INCLUDE (SystemId);
END";
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task CreateGeofencingZonesTableAsync(SqlConnection conn)
    {
        const string sql = @"
IF OBJECT_ID(N'dbo.GeofencingZones', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.GeofencingZones (
        Id              INT             IDENTITY(1,1) PRIMARY KEY,
        SystemId        NVARCHAR(128)   NOT NULL,
        ZoneIndex       INT             NOT NULL,
        Name            NVARCHAR(512)   NULL,
        StartUtc        DATETIME2(0)    NULL,
        EndUtc          DATETIME2(0)    NULL,
        Rules           JSON            NULL,
        Geometry        JSON            NOT NULL,
        RawJson         JSON            NULL,
        FetchedAtUtc    DATETIME2(0)    NOT NULL CONSTRAINT DF_GeofencingZones_FetchedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_GeofencingZones_Systems
            FOREIGN KEY (SystemId) REFERENCES dbo.Systems(SystemId) ON DELETE CASCADE,
        CONSTRAINT UQ_GeofencingZones_SystemId_ZoneIndex UNIQUE (SystemId, ZoneIndex)
    );

    CREATE INDEX IX_GeofencingZones_SystemId ON dbo.GeofencingZones(SystemId);
END";
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task CreateStationStatusesTableAsync(SqlConnection conn)
    {
        const string sql = @"
IF OBJECT_ID(N'dbo.StationStatuses', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.StationStatuses (
        Id                       BIGINT          IDENTITY(1,1) PRIMARY KEY,
        SystemId                 NVARCHAR(128)   NOT NULL,
        StationId                NVARCHAR(128)   NOT NULL,
        NumVehiclesAvailable     INT             NULL,
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
        FetchedAtUtc             DATETIME2(0)    NOT NULL CONSTRAINT DF_StationStatuses_FetchedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_StationStatuses_Systems
            FOREIGN KEY (SystemId) REFERENCES dbo.Systems(SystemId) ON DELETE CASCADE
    );

    CREATE INDEX IX_StationStatuses_System_Time
        ON dbo.StationStatuses(SystemId, FetchedAtUtc DESC);
    CREATE INDEX IX_StationStatuses_Sys_Station_Time
        ON dbo.StationStatuses(SystemId, StationId, FetchedAtUtc DESC);
END";
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task UpgradeSystemsForFkAsync(SqlConnection conn)
    {
        const string sql = @"
IF EXISTS (SELECT 1 FROM sys.indexes
           WHERE name = 'IX_Systems_SystemId' AND object_id = OBJECT_ID('dbo.Systems'))
    DROP INDEX IX_Systems_SystemId ON dbo.Systems;

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE name = 'SystemId' AND object_id = OBJECT_ID('dbo.Systems') AND is_nullable = 1)
BEGIN
    DELETE FROM dbo.Systems WHERE SystemId IS NULL OR LTRIM(RTRIM(SystemId)) = '';
    ALTER TABLE dbo.Systems ALTER COLUMN SystemId NVARCHAR(128) NOT NULL;
END

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints
               WHERE parent_object_id = OBJECT_ID('dbo.Systems') AND name = 'UQ_Systems_SystemId')
BEGIN
    ;WITH d AS (
        SELECT Id, ROW_NUMBER() OVER (PARTITION BY SystemId ORDER BY Id) AS rn
        FROM dbo.Systems
    )
    DELETE FROM d WHERE rn > 1;

    ALTER TABLE dbo.Systems ADD CONSTRAINT UQ_Systems_SystemId UNIQUE (SystemId);
END";
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task CreateStationsTableAsync(SqlConnection conn)
    {
        const string sql = @"
IF OBJECT_ID(N'dbo.Stations', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Stations (
        Id                  INT             IDENTITY(1,1) PRIMARY KEY,
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
        RentalUris          JSON            NULL,
        RawJson             JSON            NULL,
        FetchedAtUtc        DATETIME2(0)    NOT NULL CONSTRAINT DF_Stations_FetchedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_Stations_Systems
            FOREIGN KEY (SystemId) REFERENCES dbo.Systems(SystemId) ON DELETE CASCADE,
        CONSTRAINT UQ_Stations_SystemId_StationId UNIQUE (SystemId, StationId)
    );

    CREATE INDEX IX_Stations_SystemId ON dbo.Stations(SystemId);
END";
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<int> TruncateAsync()
    {
        await using var conn = new SqlConnection(ReplaceCatalog(_connectionString, DatabaseName));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand($"DELETE FROM dbo.{TableName}; DBCC CHECKIDENT('dbo.{TableName}', RESEED, 0) WITH NO_INFOMSGS;", conn);
        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task<int> BulkInsertAsync(IEnumerable<GbfsSystem> systems)
    {
        var table = BuildDataTable();
        foreach (var s in systems)
        {
            table.Rows.Add(
                (object?)s.CountryCode ?? DBNull.Value,
                (object?)s.Name ?? DBNull.Value,
                (object?)s.Location ?? DBNull.Value,
                (object?)s.SystemId ?? DBNull.Value,
                (object?)s.Url ?? DBNull.Value,
                (object?)s.AutoDiscoveryUrl ?? DBNull.Value,
                (object?)s.SupportedVersions ?? DBNull.Value,
                (object?)s.AuthenticationInfoUrl ?? DBNull.Value,
                (object?)s.AuthenticationType ?? DBNull.Value,
                (object?)s.AuthenticationParameterName ?? DBNull.Value);
        }

        await using var conn = new SqlConnection(ReplaceCatalog(_connectionString, DatabaseName));
        await conn.OpenAsync();
        using var bulk = new SqlBulkCopy(conn) { DestinationTableName = $"dbo.{TableName}", BatchSize = 500 };
        foreach (DataColumn col in table.Columns)
            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        await bulk.WriteToServerAsync(table);
        return table.Rows.Count;
    }

    public async Task<long> CountAsync()
    {
        await using var conn = new SqlConnection(ReplaceCatalog(_connectionString, DatabaseName));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand($"SELECT COUNT_BIG(*) FROM dbo.{TableName}", conn);
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    public async Task<List<GbfsSystem>> QueryAsync(QueryFilter filter)
    {
        var sql = $@"
SELECT TOP (@top)
    CountryCode, Name, Location, SystemId, Url, AutoDiscoveryUrl,
    SupportedVersions, AuthenticationInfoUrl, AuthenticationType, AuthenticationParameterName
FROM dbo.{TableName}
WHERE (@country     IS NULL OR CountryCode = @country)
  AND (@nameLike    IS NULL OR Name        LIKE '%' + @nameLike + '%')
  AND (@locationLike IS NULL OR Location   LIKE '%' + @locationLike + '%')
  AND (@systemId    IS NULL OR SystemId    = @systemId)
  AND (@authType    IS NULL OR AuthenticationType = @authType)
  AND (@version     IS NULL OR SupportedVersions LIKE '%' + @version + '%')
ORDER BY CountryCode, Name;";

        await using var conn = new SqlConnection(ReplaceCatalog(_connectionString, DatabaseName));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@top", filter.Top);
        cmd.Parameters.AddWithValue("@country", (object?)filter.Country ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@nameLike", (object?)filter.NameLike ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@locationLike", (object?)filter.LocationLike ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@systemId", (object?)filter.SystemId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@authType", (object?)filter.AuthType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@version", (object?)filter.Version ?? DBNull.Value);

        var results = new List<GbfsSystem>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new GbfsSystem
            {
                CountryCode = ReadString(reader, 0),
                Name = ReadString(reader, 1),
                Location = ReadString(reader, 2),
                SystemId = ReadString(reader, 3),
                Url = ReadString(reader, 4),
                AutoDiscoveryUrl = ReadString(reader, 5),
                SupportedVersions = ReadString(reader, 6),
                AuthenticationInfoUrl = ReadString(reader, 7),
                AuthenticationType = ReadString(reader, 8),
                AuthenticationParameterName = ReadString(reader, 9),
            });
        }
        return results;
    }

    public async Task<GbfsSystem?> GetBySystemIdAsync(string systemId)
    {
        const string sql = @"
SELECT TOP (1)
    CountryCode, Name, Location, SystemId, Url, AutoDiscoveryUrl,
    SupportedVersions, AuthenticationInfoUrl, AuthenticationType, AuthenticationParameterName,
    IsEnabled
FROM dbo.Systems
WHERE SystemId = @systemId;";

        await using var conn = new SqlConnection(ReplaceCatalog(_connectionString, DatabaseName));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@systemId", systemId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new GbfsSystem
        {
            CountryCode = ReadString(reader, 0),
            Name = ReadString(reader, 1),
            Location = ReadString(reader, 2),
            SystemId = ReadString(reader, 3),
            Url = ReadString(reader, 4),
            AutoDiscoveryUrl = ReadString(reader, 5),
            SupportedVersions = ReadString(reader, 6),
            AuthenticationInfoUrl = ReadString(reader, 7),
            AuthenticationType = ReadString(reader, 8),
            AuthenticationParameterName = ReadString(reader, 9),
            IsEnabled = reader.IsDBNull(10) ? null : reader.GetBoolean(10),
        };
    }

    public async Task<List<(string Group, int Count)>> GroupCountAsync(string column)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "CountryCode", "AuthenticationType", "Location", "SupportedVersions" };
        if (!allowed.Contains(column))
            throw new ArgumentException($"Group column must be one of: {string.Join(", ", allowed)}");

        var sql = $@"
SELECT ISNULL([{column}], '(null)') AS GroupKey, COUNT(*) AS Cnt
FROM dbo.{TableName}
GROUP BY [{column}]
ORDER BY Cnt DESC, GroupKey;";

        await using var conn = new SqlConnection(ReplaceCatalog(_connectionString, DatabaseName));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        var results = new List<(string, int)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add((reader.GetString(0), reader.GetInt32(1)));
        return results;
    }

    public async Task<int> ReplaceStationsAsync(string systemId, IEnumerable<Station> stations)
    {
        var dbConn = ReplaceCatalog(_connectionString, DatabaseName);
        await using var conn = new SqlConnection(dbConn);
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            await using (var del = new SqlCommand("DELETE FROM dbo.Stations WHERE SystemId = @sid", conn, tx))
            {
                del.Parameters.AddWithValue("@sid", systemId);
                await del.ExecuteNonQueryAsync();
            }

            const string insert = @"
INSERT INTO dbo.Stations
    (SystemId, StationId, Name, ShortName, Lat, Lon, Address, CrossStreet, RegionId, PostCode,
     StationType, Capacity, IsVirtualStation, IsValetStation, IsChargingStation,
     ParkingType, ParkingHoop, ContactPhone, RentalMethods, RentalUris, RawJson)
VALUES
    (@SystemId, @StationId, @Name, @ShortName, @Lat, @Lon, @Address, @CrossStreet, @RegionId, @PostCode,
     @StationType, @Capacity, @IsVirtualStation, @IsValetStation, @IsChargingStation,
     @ParkingType, @ParkingHoop, @ContactPhone, @RentalMethods, @RentalUris, @RawJson);";

            var inserted = 0;
            foreach (var s in stations)
            {
                await using var cmd = new SqlCommand(insert, conn, tx);
                cmd.Parameters.AddWithValue("@SystemId", systemId);
                cmd.Parameters.AddWithValue("@StationId", s.StationId);
                AddNullable(cmd, "@Name", s.Name);
                AddNullable(cmd, "@ShortName", s.ShortName);
                cmd.Parameters.AddWithValue("@Lat", s.Lat);
                cmd.Parameters.AddWithValue("@Lon", s.Lon);
                AddNullable(cmd, "@Address", s.Address);
                AddNullable(cmd, "@CrossStreet", s.CrossStreet);
                AddNullable(cmd, "@RegionId", s.RegionId);
                AddNullable(cmd, "@PostCode", s.PostCode);
                AddNullable(cmd, "@StationType", s.StationType);
                AddNullableInt(cmd, "@Capacity", s.Capacity);
                AddNullableBool(cmd, "@IsVirtualStation", s.IsVirtualStation);
                AddNullableBool(cmd, "@IsValetStation", s.IsValetStation);
                AddNullableBool(cmd, "@IsChargingStation", s.IsChargingStation);
                AddNullable(cmd, "@ParkingType", s.ParkingType);
                AddNullableBool(cmd, "@ParkingHoop", s.ParkingHoop);
                AddNullable(cmd, "@ContactPhone", s.ContactPhone);
                AddNullable(cmd, "@RentalMethods", s.RentalMethods);
                AddNullable(cmd, "@RentalUris", s.RentalUrisJson);
                AddNullable(cmd, "@RawJson", s.RawJson);
                inserted += await cmd.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            return inserted;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    public async Task<List<StationRow>> ListStationsAsync(string systemId, int top)
    {
        const string sql = @"
SELECT TOP (@top)
    StationId, Name, Lat, Lon, Address, Capacity, IsVirtualStation,
    CAST(RentalUris AS NVARCHAR(MAX)) AS RentalUris
FROM dbo.Stations
WHERE SystemId = @sid
ORDER BY StationId;";

        await using var conn = new SqlConnection(ReplaceCatalog(_connectionString, DatabaseName));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@sid", systemId);
        cmd.Parameters.AddWithValue("@top", top);

        var results = new List<StationRow>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new StationRow(
                StationId: reader.GetString(0),
                Name: reader.IsDBNull(1) ? null : reader.GetString(1),
                Lat: reader.GetDouble(2),
                Lon: reader.GetDouble(3),
                Address: reader.IsDBNull(4) ? null : reader.GetString(4),
                Capacity: reader.IsDBNull(5) ? null : reader.GetInt32(5),
                IsVirtualStation: reader.IsDBNull(6) ? null : reader.GetBoolean(6),
                RentalUris: reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        return results;
    }

    public async Task<int> RecordStationStatusesAsync(string systemId, IEnumerable<StationStatus> statuses)
    {
        var dbConn = ReplaceCatalog(_connectionString, DatabaseName);
        await using var conn = new SqlConnection(dbConn);
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            const string insert = @"
INSERT INTO dbo.StationStatuses
    (SystemId, StationId, NumVehiclesAvailable, NumVehiclesDisabled,
     NumDocksAvailable, NumDocksDisabled, IsInstalled, IsRenting, IsReturning,
     LastReportedUtc, VehicleTypesAvailable, VehicleDocksAvailable, RawJson)
VALUES
    (@SystemId, @StationId, @NumVehiclesAvailable, @NumVehiclesDisabled,
     @NumDocksAvailable, @NumDocksDisabled, @IsInstalled, @IsRenting, @IsReturning,
     @LastReportedUtc, @VehicleTypesAvailable, @VehicleDocksAvailable, @RawJson);";

            var inserted = 0;
            foreach (var s in statuses)
            {
                await using var cmd = new SqlCommand(insert, conn, tx);
                cmd.Parameters.AddWithValue("@SystemId", systemId);
                cmd.Parameters.AddWithValue("@StationId", s.StationId);
                AddNullableInt(cmd, "@NumVehiclesAvailable", s.NumVehiclesAvailable);
                AddNullableInt(cmd, "@NumVehiclesDisabled", s.NumVehiclesDisabled);
                AddNullableInt(cmd, "@NumDocksAvailable", s.NumDocksAvailable);
                AddNullableInt(cmd, "@NumDocksDisabled", s.NumDocksDisabled);
                AddNullableBool(cmd, "@IsInstalled", s.IsInstalled);
                AddNullableBool(cmd, "@IsRenting", s.IsRenting);
                AddNullableBool(cmd, "@IsReturning", s.IsReturning);
                cmd.Parameters.AddWithValue("@LastReportedUtc", (object?)s.LastReportedUtc ?? DBNull.Value);
                AddNullable(cmd, "@VehicleTypesAvailable", s.VehicleTypesAvailableJson);
                AddNullable(cmd, "@VehicleDocksAvailable", s.VehicleDocksAvailableJson);
                AddNullable(cmd, "@RawJson", s.RawJson);
                inserted += await cmd.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            return inserted;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    public async Task<long> StationStatusCountAsync(string? systemId = null)
    {
        var sql = systemId is null
            ? "SELECT COUNT_BIG(*) FROM dbo.StationStatuses"
            : "SELECT COUNT_BIG(*) FROM dbo.StationStatuses WHERE SystemId = @sid";
        await using var conn = new SqlConnection(ReplaceCatalog(_connectionString, DatabaseName));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        if (systemId is not null) cmd.Parameters.AddWithValue("@sid", systemId);
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    public async Task<List<SystemOption>> ListSystemsWithStationsAsync()
    {
        const string sql = @"
SELECT s.SystemId, s.Name, s.CountryCode, s.Location, COUNT(st.Id) AS StationCount
FROM dbo.Systems s
JOIN dbo.Stations st ON st.SystemId = s.SystemId
GROUP BY s.SystemId, s.Name, s.CountryCode, s.Location
ORDER BY s.Name;";

        await using var conn = new SqlConnection(ReplaceCatalog(_connectionString, DatabaseName));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        var results = new List<SystemOption>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new SystemOption(
                SystemId: reader.GetString(0),
                Name: reader.IsDBNull(1) ? "" : reader.GetString(1),
                CountryCode: reader.IsDBNull(2) ? null : reader.GetString(2),
                Location: reader.IsDBNull(3) ? null : reader.GetString(3),
                StationCount: reader.GetInt32(4)));
        }
        return results;
    }

    public async Task<List<SystemOption>> ListEnabledSystemsForMapAsync()
    {
        const string sql = @"
SELECT s.SystemId, s.Name, s.CountryCode, s.Location,
       ISNULL((SELECT COUNT(*) FROM dbo.Stations st WHERE st.SystemId = s.SystemId), 0) AS StationCount
FROM dbo.Systems s
WHERE s.IsEnabled = 1
ORDER BY s.Name;";

        await using var conn = new SqlConnection(ReplaceCatalog(_connectionString, DatabaseName));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        var results = new List<SystemOption>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new SystemOption(
                SystemId: reader.GetString(0),
                Name: reader.IsDBNull(1) ? "" : reader.GetString(1),
                CountryCode: reader.IsDBNull(2) ? null : reader.GetString(2),
                Location: reader.IsDBNull(3) ? null : reader.GetString(3),
                StationCount: reader.GetInt32(4)));
        }
        return results;
    }

    public async Task<List<MapStation>> GetStationsForMapAsync(string systemId)
    {
        const string sql = @"
;WITH latest AS (
    SELECT StationId,
           NumVehiclesAvailable, NumVehiclesDisabled,
           NumDocksAvailable, NumDocksDisabled,
           IsInstalled, IsRenting, IsReturning,
           LastReportedUtc, FetchedAtUtc, VehicleTypesAvailable,
           ROW_NUMBER() OVER (PARTITION BY StationId ORDER BY FetchedAtUtc DESC) AS rn
    FROM dbo.StationStatuses
    WHERE SystemId = @sid
),
today AS (
    SELECT StationId,
           AVG(CAST(NumVehiclesAvailable AS FLOAT)) AS AvgVehiclesToday,
           AVG(CAST(NumDocksAvailable    AS FLOAT)) AS AvgDocksToday,
           COUNT(*) AS SamplesToday
    FROM dbo.StationStatuses
    WHERE SystemId = @sid
      AND CAST(FetchedAtUtc AS DATE) = CAST(SYSUTCDATETIME() AS DATE)
    GROUP BY StationId
)
SELECT s.StationId, s.Name, s.Lat, s.Lon, s.Capacity,
       l.NumVehiclesAvailable, l.NumVehiclesDisabled,
       l.NumDocksAvailable, l.NumDocksDisabled,
       l.IsInstalled, l.IsRenting, l.IsReturning,
       l.LastReportedUtc, l.FetchedAtUtc,
       CAST(l.VehicleTypesAvailable AS NVARCHAR(MAX)) AS VehicleTypesJson,
       t.AvgVehiclesToday, t.AvgDocksToday, t.SamplesToday
FROM dbo.Stations s
LEFT JOIN latest l ON l.StationId = s.StationId AND l.rn = 1
LEFT JOIN today  t ON t.StationId = s.StationId
WHERE s.SystemId = @sid
ORDER BY s.StationId;";

        await using var conn = new SqlConnection(ReplaceCatalog(_connectionString, DatabaseName));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@sid", systemId);

        var results = new List<MapStation>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new MapStation(
                StationId: reader.GetString(0),
                Name: reader.IsDBNull(1) ? null : reader.GetString(1),
                Lat: reader.GetDouble(2),
                Lon: reader.GetDouble(3),
                Capacity: reader.IsDBNull(4) ? null : reader.GetInt32(4),
                NumVehiclesAvailable: reader.IsDBNull(5) ? null : reader.GetInt32(5),
                NumVehiclesDisabled: reader.IsDBNull(6) ? null : reader.GetInt32(6),
                NumDocksAvailable: reader.IsDBNull(7) ? null : reader.GetInt32(7),
                NumDocksDisabled: reader.IsDBNull(8) ? null : reader.GetInt32(8),
                IsInstalled: reader.IsDBNull(9) ? null : reader.GetBoolean(9),
                IsRenting: reader.IsDBNull(10) ? null : reader.GetBoolean(10),
                IsReturning: reader.IsDBNull(11) ? null : reader.GetBoolean(11),
                LastReportedUtc: reader.IsDBNull(12) ? null : reader.GetDateTime(12),
                LatestFetchedUtc: reader.IsDBNull(13) ? null : reader.GetDateTime(13),
                VehicleTypesAvailableJson: reader.IsDBNull(14) ? null : reader.GetString(14),
                AvgVehiclesAvailableToday: reader.IsDBNull(15) ? null : reader.GetDouble(15),
                AvgDocksAvailableToday: reader.IsDBNull(16) ? null : reader.GetDouble(16),
                SamplesToday: reader.IsDBNull(17) ? null : reader.GetInt32(17)));
        }
        return results;
    }

    public async Task<List<DailyAverage>> GetStationDailyAveragesAsync(string systemId, string stationId)
    {
        const string sql = @"
SELECT CAST(FetchedAtUtc AS DATE) AS Day,
       AVG(CAST(NumVehiclesAvailable AS FLOAT)) AS AvgVehicles,
       AVG(CAST(NumDocksAvailable    AS FLOAT)) AS AvgDocks,
       COUNT(*) AS Samples
FROM dbo.StationStatuses
WHERE SystemId = @sid AND StationId = @sta
GROUP BY CAST(FetchedAtUtc AS DATE)
ORDER BY Day DESC;";

        await using var conn = new SqlConnection(ReplaceCatalog(_connectionString, DatabaseName));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@sid", systemId);
        cmd.Parameters.AddWithValue("@sta", stationId);

        var results = new List<DailyAverage>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new DailyAverage(
                Day: DateOnly.FromDateTime(reader.GetDateTime(0)),
                AvgVehiclesAvailable: reader.IsDBNull(1) ? null : reader.GetDouble(1),
                AvgDocksAvailable: reader.IsDBNull(2) ? null : reader.GetDouble(2),
                Samples: reader.GetInt32(3)));
        }
        return results;
    }

    public async Task<int> ReplaceGeofencingZonesAsync(string systemId, IEnumerable<GeofencingZone> zones)
    {
        var dbConn = ReplaceCatalog(_connectionString, DatabaseName);
        await using var conn = new SqlConnection(dbConn);
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            await using (var del = new SqlCommand("DELETE FROM dbo.GeofencingZones WHERE SystemId = @sid", conn, tx))
            {
                del.Parameters.AddWithValue("@sid", systemId);
                await del.ExecuteNonQueryAsync();
            }

            const string insert = @"
INSERT INTO dbo.GeofencingZones
    (SystemId, ZoneIndex, Name, StartUtc, EndUtc, Rules, Geometry, RawJson)
VALUES
    (@SystemId, @ZoneIndex, @Name, @StartUtc, @EndUtc, @Rules, @Geometry, @RawJson);";

            var inserted = 0;
            foreach (var z in zones)
            {
                await using var cmd = new SqlCommand(insert, conn, tx);
                cmd.Parameters.AddWithValue("@SystemId", systemId);
                cmd.Parameters.AddWithValue("@ZoneIndex", z.ZoneIndex);
                AddNullable(cmd, "@Name", z.Name);
                cmd.Parameters.AddWithValue("@StartUtc", (object?)z.StartUtc ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@EndUtc", (object?)z.EndUtc ?? DBNull.Value);
                AddNullable(cmd, "@Rules", z.RulesJson);
                cmd.Parameters.AddWithValue("@Geometry", z.GeometryGeoJson);
                AddNullable(cmd, "@RawJson", z.RawJson);
                inserted += await cmd.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            return inserted;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    public async Task<List<MapGeofencingZone>> GetGeofencingZonesForMapAsync(string systemId)
    {
        const string sql = @"
SELECT ZoneIndex, Name,
       CAST(Geometry AS NVARCHAR(MAX)) AS Geometry,
       CAST(Rules AS NVARCHAR(MAX)) AS Rules
FROM dbo.GeofencingZones
WHERE SystemId = @sid
ORDER BY ZoneIndex;";

        await using var conn = new SqlConnection(ReplaceCatalog(_connectionString, DatabaseName));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@sid", systemId);
        var results = new List<MapGeofencingZone>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new MapGeofencingZone(
                ZoneIndex: reader.GetInt32(0),
                Name: reader.IsDBNull(1) ? null : reader.GetString(1),
                GeometryGeoJson: reader.GetString(2),
                RulesJson: reader.IsDBNull(3) ? null : reader.GetString(3)));
        }
        return results;
    }

    public async Task<long> GeofencingZoneCountAsync(string? systemId = null)
    {
        var sql = systemId is null
            ? "SELECT COUNT_BIG(*) FROM dbo.GeofencingZones"
            : "SELECT COUNT_BIG(*) FROM dbo.GeofencingZones WHERE SystemId = @sid";
        await using var conn = new SqlConnection(ReplaceCatalog(_connectionString, DatabaseName));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        if (systemId is not null) cmd.Parameters.AddWithValue("@sid", systemId);
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    public async Task<long> StationCountAsync(string? systemId = null)
    {
        var sql = systemId is null
            ? "SELECT COUNT_BIG(*) FROM dbo.Stations"
            : "SELECT COUNT_BIG(*) FROM dbo.Stations WHERE SystemId = @sid";
        await using var conn = new SqlConnection(ReplaceCatalog(_connectionString, DatabaseName));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        if (systemId is not null) cmd.Parameters.AddWithValue("@sid", systemId);
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    public async Task<int> RecordFreeBikeStatusesAsync(string systemId, IEnumerable<FreeBikeStatus> statuses)
    {
        var dbConn = ReplaceCatalog(_connectionString, DatabaseName);
        await using var conn = new SqlConnection(dbConn);
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            const string insert = @"
INSERT INTO dbo.FreeBikeStatuses
    (SystemId, VehicleId, Lat, Lon, IsReserved, IsDisabled, VehicleTypeId,
     CurrentRangeMeters, CurrentFuelPercent, StationId, PricingPlanId,
     LastReportedUtc, RentalUris, RawJson)
VALUES
    (@SystemId, @VehicleId, @Lat, @Lon, @IsReserved, @IsDisabled, @VehicleTypeId,
     @CurrentRangeMeters, @CurrentFuelPercent, @StationId, @PricingPlanId,
     @LastReportedUtc, @RentalUris, @RawJson);";

            var inserted = 0;
            foreach (var s in statuses)
            {
                await using var cmd = new SqlCommand(insert, conn, tx);
                cmd.Parameters.AddWithValue("@SystemId", systemId);
                cmd.Parameters.AddWithValue("@VehicleId", s.VehicleId);
                cmd.Parameters.AddWithValue("@Lat", (object?)s.Lat ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Lon", (object?)s.Lon ?? DBNull.Value);
                AddNullableBool(cmd, "@IsReserved", s.IsReserved);
                AddNullableBool(cmd, "@IsDisabled", s.IsDisabled);
                AddNullable(cmd, "@VehicleTypeId", s.VehicleTypeId);
                cmd.Parameters.AddWithValue("@CurrentRangeMeters", (object?)s.CurrentRangeMeters ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@CurrentFuelPercent", (object?)s.CurrentFuelPercent ?? DBNull.Value);
                AddNullable(cmd, "@StationId", s.StationId);
                AddNullable(cmd, "@PricingPlanId", s.PricingPlanId);
                cmd.Parameters.AddWithValue("@LastReportedUtc", (object?)s.LastReportedUtc ?? DBNull.Value);
                AddNullable(cmd, "@RentalUris", s.RentalUrisJson);
                AddNullable(cmd, "@RawJson", s.RawJson);
                inserted += await cmd.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            return inserted;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    public async Task<List<MapBike>> GetLatestFreeBikesForMapAsync(string systemId)
    {
        const string sql = @"
;WITH latest AS (
    SELECT
        VehicleId, Lat, Lon, IsReserved, IsDisabled, VehicleTypeId,
        CurrentRangeMeters, CurrentFuelPercent, StationId, FetchedAtUtc,
        ROW_NUMBER() OVER (PARTITION BY VehicleId ORDER BY FetchedAtUtc DESC) AS rn
    FROM dbo.FreeBikeStatuses
    WHERE SystemId = @sid
)
SELECT VehicleId, Lat, Lon, IsReserved, IsDisabled, VehicleTypeId,
       CurrentRangeMeters, CurrentFuelPercent, StationId, FetchedAtUtc
FROM latest
WHERE rn = 1
  AND Lat IS NOT NULL AND Lon IS NOT NULL
  AND ISNULL(StationId, '') = ''  -- only show truly free-floating (not docked)
ORDER BY VehicleId;";

        await using var conn = new SqlConnection(ReplaceCatalog(_connectionString, DatabaseName));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@sid", systemId);

        var results = new List<MapBike>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new MapBike(
                VehicleId: reader.GetString(0),
                Lat: reader.GetDouble(1),
                Lon: reader.GetDouble(2),
                IsReserved: reader.IsDBNull(3) ? null : reader.GetBoolean(3),
                IsDisabled: reader.IsDBNull(4) ? null : reader.GetBoolean(4),
                VehicleTypeId: reader.IsDBNull(5) ? null : reader.GetString(5),
                CurrentRangeMeters: reader.IsDBNull(6) ? null : reader.GetDouble(6),
                CurrentFuelPercent: reader.IsDBNull(7) ? null : reader.GetDouble(7),
                StationId: reader.IsDBNull(8) ? null : reader.GetString(8),
                LatestFetchedUtc: reader.GetDateTime(9)));
        }
        return results;
    }

    public async Task<long> FreeBikeStatusCountAsync(string? systemId = null)
    {
        var sql = systemId is null
            ? "SELECT COUNT_BIG(*) FROM dbo.FreeBikeStatuses"
            : "SELECT COUNT_BIG(*) FROM dbo.FreeBikeStatuses WHERE SystemId = @sid";
        await using var conn = new SqlConnection(ReplaceCatalog(_connectionString, DatabaseName));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        if (systemId is not null) cmd.Parameters.AddWithValue("@sid", systemId);
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    public async Task<bool?> ToggleSystemEnabledAsync(string systemId)
    {
        const string sql = @"
UPDATE dbo.Systems
SET IsEnabled = CASE WHEN IsEnabled = 1 THEN 0 ELSE 1 END
OUTPUT inserted.IsEnabled
WHERE SystemId = @sid;";
        await using var conn = new SqlConnection(ReplaceCatalog(_connectionString, DatabaseName));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@sid", systemId);
        var result = await cmd.ExecuteScalarAsync();
        return result is bool b ? b : null;
    }

    public async Task<int> SetSystemEnabledAsync(string systemId, bool isEnabled)
    {
        const string sql = "UPDATE dbo.Systems SET IsEnabled = @e WHERE SystemId = @sid;";
        await using var conn = new SqlConnection(ReplaceCatalog(_connectionString, DatabaseName));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@e", isEnabled);
        cmd.Parameters.AddWithValue("@sid", systemId);
        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<string>> ListEnabledSystemIdsAsync()
    {
        const string sql = "SELECT SystemId FROM dbo.Systems WHERE IsEnabled = 1 ORDER BY SystemId;";
        await using var conn = new SqlConnection(ReplaceCatalog(_connectionString, DatabaseName));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        var results = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(reader.GetString(0));
        return results;
    }

    public async Task<List<string>> ListDistinctCountryCodesAsync()
    {
        const string sql = @"
SELECT DISTINCT CountryCode FROM dbo.Systems
WHERE CountryCode IS NOT NULL AND LTRIM(RTRIM(CountryCode)) <> ''
ORDER BY CountryCode;";
        await using var conn = new SqlConnection(ReplaceCatalog(_connectionString, DatabaseName));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        var results = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(reader.GetString(0));
        return results;
    }

    public async Task<(List<SystemListRow> Rows, int TotalCount)> ListSystemsAsync(SystemListFilter filter, int page, int pageSize)
    {
        const string whereClause = @"
WHERE (@country IS NULL OR s.CountryCode = @country)
  AND (@nameLike IS NULL OR s.Name LIKE '%' + @nameLike + '%')
  AND (@locationLike IS NULL OR s.Location LIKE '%' + @locationLike + '%')
  AND (@authType IS NULL OR s.AuthenticationType = @authType)
  AND (@enabled IS NULL OR s.IsEnabled = @enabled)";

        var sql = $@"
SELECT COUNT_BIG(*) FROM dbo.Systems s {whereClause};

SELECT
    s.SystemId, s.Name, s.CountryCode, s.Location,
    s.SupportedVersions, s.AuthenticationType, s.IsEnabled,
    ISNULL(sc.cnt, 0) AS StationCount,
    ISNULL(zc.cnt, 0) AS ZoneCount,
    ISNULL(ss.cnt, 0) AS StatusCount,
    ss.LastFetched
FROM dbo.Systems s
LEFT JOIN (SELECT SystemId, COUNT(*) AS cnt FROM dbo.Stations         GROUP BY SystemId) sc ON sc.SystemId = s.SystemId
LEFT JOIN (SELECT SystemId, COUNT(*) AS cnt FROM dbo.GeofencingZones  GROUP BY SystemId) zc ON zc.SystemId = s.SystemId
LEFT JOIN (SELECT SystemId, COUNT(*) AS cnt, MAX(FetchedAtUtc) AS LastFetched
           FROM dbo.StationStatuses GROUP BY SystemId) ss ON ss.SystemId = s.SystemId
{whereClause}
ORDER BY s.IsEnabled DESC, s.Name
OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY;";

        await using var conn = new SqlConnection(ReplaceCatalog(_connectionString, DatabaseName));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@country",      (object?)NullIfEmpty(filter.CountryCode) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@nameLike",     (object?)NullIfEmpty(filter.NameLike)    ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@locationLike", (object?)NullIfEmpty(filter.LocationLike)?? DBNull.Value);
        cmd.Parameters.AddWithValue("@authType",     (object?)NullIfEmpty(filter.AuthType)    ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@enabled",      (object?)filter.Enabled                  ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@offset",       Math.Max(0, (page - 1) * pageSize));
        cmd.Parameters.AddWithValue("@pageSize",     Math.Clamp(pageSize, 1, 500));

        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        var total = checked((int)reader.GetInt64(0));
        await reader.NextResultAsync();

        var rows = new List<SystemListRow>();
        while (await reader.ReadAsync())
        {
            rows.Add(new SystemListRow(
                SystemId: reader.GetString(0),
                Name: reader.IsDBNull(1) ? "" : reader.GetString(1),
                CountryCode: reader.IsDBNull(2) ? null : reader.GetString(2),
                Location: reader.IsDBNull(3) ? null : reader.GetString(3),
                SupportedVersions: reader.IsDBNull(4) ? null : reader.GetString(4),
                AuthenticationType: reader.IsDBNull(5) ? null : reader.GetString(5),
                IsEnabled: reader.GetBoolean(6),
                StationCount: reader.GetInt32(7),
                ZoneCount: reader.GetInt32(8),
                StatusCount: reader.GetInt32(9),
                LastStatusFetchedUtc: reader.IsDBNull(10) ? null : reader.GetDateTime(10)));
        }
        return (rows, total);
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public async Task<List<SystemAvailabilityOption>> ListSystemsWithStationsAndStatusesAsync()
    {
        const string sql = @"
SELECT s.SystemId, s.Name, s.CountryCode, s.Location,
       sc.StationCount, ss.StatusCount, ss.FirstFetched, ss.LastFetched
FROM dbo.Systems s
INNER JOIN (SELECT SystemId, COUNT(*) AS StationCount FROM dbo.Stations GROUP BY SystemId) sc
        ON sc.SystemId = s.SystemId
INNER JOIN (SELECT SystemId, COUNT(*) AS StatusCount,
                   MIN(FetchedAtUtc) AS FirstFetched, MAX(FetchedAtUtc) AS LastFetched
            FROM dbo.StationStatuses GROUP BY SystemId) ss
        ON ss.SystemId = s.SystemId
ORDER BY s.Name;";

        await using var conn = new SqlConnection(ReplaceCatalog(_connectionString, DatabaseName));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        var results = new List<SystemAvailabilityOption>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new SystemAvailabilityOption(
                SystemId: reader.GetString(0),
                Name: reader.IsDBNull(1) ? "" : reader.GetString(1),
                CountryCode: reader.IsDBNull(2) ? null : reader.GetString(2),
                Location: reader.IsDBNull(3) ? null : reader.GetString(3),
                StationCount: reader.GetInt32(4),
                StatusCount: reader.GetInt32(5),
                FirstFetchedUtc: reader.IsDBNull(6) ? null : reader.GetDateTime(6),
                LastFetchedUtc: reader.IsDBNull(7) ? null : reader.GetDateTime(7)));
        }
        return results;
    }

    public async Task<List<StationWithLatestStatus>> GetStationsWithLatestStatusAsync(string systemId, string? search)
    {
        const string sql = @"
;WITH latest AS (
    SELECT
        StationId, NumVehiclesAvailable, NumVehiclesDisabled,
        NumDocksAvailable, NumDocksDisabled,
        IsInstalled, IsRenting, IsReturning,
        LastReportedUtc, FetchedAtUtc,
        ROW_NUMBER() OVER (PARTITION BY StationId ORDER BY FetchedAtUtc DESC) AS rn
    FROM dbo.StationStatuses
    WHERE SystemId = @sid
)
SELECT
    s.StationId, s.Name, s.Lat, s.Lon, s.Capacity,
    l.NumVehiclesAvailable, l.NumVehiclesDisabled,
    l.NumDocksAvailable, l.NumDocksDisabled,
    l.IsInstalled, l.IsRenting, l.IsReturning,
    l.LastReportedUtc, l.FetchedAtUtc
FROM dbo.Stations s
LEFT JOIN latest l
       ON l.StationId = s.StationId AND l.rn = 1
WHERE s.SystemId = @sid
  AND (@search IS NULL
       OR s.Name LIKE '%' + @search + '%'
       OR s.StationId LIKE '%' + @search + '%')
ORDER BY s.Name, s.StationId;";

        await using var conn = new SqlConnection(ReplaceCatalog(_connectionString, DatabaseName));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@sid", systemId);
        cmd.Parameters.AddWithValue("@search", string.IsNullOrWhiteSpace(search) ? DBNull.Value : (object)search);

        var results = new List<StationWithLatestStatus>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new StationWithLatestStatus(
                StationId: reader.GetString(0),
                Name: reader.IsDBNull(1) ? null : reader.GetString(1),
                Lat: reader.GetDouble(2),
                Lon: reader.GetDouble(3),
                Capacity: reader.IsDBNull(4) ? null : reader.GetInt32(4),
                NumVehiclesAvailable: reader.IsDBNull(5) ? null : reader.GetInt32(5),
                NumVehiclesDisabled: reader.IsDBNull(6) ? null : reader.GetInt32(6),
                NumDocksAvailable: reader.IsDBNull(7) ? null : reader.GetInt32(7),
                NumDocksDisabled: reader.IsDBNull(8) ? null : reader.GetInt32(8),
                IsInstalled: reader.IsDBNull(9) ? null : reader.GetBoolean(9),
                IsRenting: reader.IsDBNull(10) ? null : reader.GetBoolean(10),
                IsReturning: reader.IsDBNull(11) ? null : reader.GetBoolean(11),
                LastReportedUtc: reader.IsDBNull(12) ? null : reader.GetDateTime(12),
                LatestFetchedUtc: reader.IsDBNull(13) ? null : reader.GetDateTime(13)));
        }
        return results;
    }

    private static void AddNullable(SqlCommand cmd, string name, string? value) =>
        cmd.Parameters.AddWithValue(name, (object?)value ?? DBNull.Value);

    private static void AddNullableInt(SqlCommand cmd, string name, int? value) =>
        cmd.Parameters.AddWithValue(name, (object?)value ?? DBNull.Value);

    private static void AddNullableBool(SqlCommand cmd, string name, bool? value) =>
        cmd.Parameters.AddWithValue(name, (object?)value ?? DBNull.Value);

    private static DataTable BuildDataTable()
    {
        var table = new DataTable();
        table.Columns.Add("CountryCode", typeof(string));
        table.Columns.Add("Name", typeof(string));
        table.Columns.Add("Location", typeof(string));
        table.Columns.Add("SystemId", typeof(string));
        table.Columns.Add("Url", typeof(string));
        table.Columns.Add("AutoDiscoveryUrl", typeof(string));
        table.Columns.Add("SupportedVersions", typeof(string));
        table.Columns.Add("AuthenticationInfoUrl", typeof(string));
        table.Columns.Add("AuthenticationType", typeof(string));
        table.Columns.Add("AuthenticationParameterName", typeof(string));
        return table;
    }

    private static string ReplaceCatalog(string connString, string catalog) =>
        new SqlConnectionStringBuilder(connString) { InitialCatalog = catalog }.ConnectionString;

    private static string? ReadString(SqlDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : reader.GetString(index);
}

public record SystemOption(string SystemId, string Name, string? CountryCode, string? Location, int StationCount);

public record MapStation(
    string StationId, string? Name, double Lat, double Lon, int? Capacity,
    int? NumVehiclesAvailable, int? NumVehiclesDisabled,
    int? NumDocksAvailable, int? NumDocksDisabled,
    bool? IsInstalled, bool? IsRenting, bool? IsReturning,
    DateTime? LastReportedUtc, DateTime? LatestFetchedUtc,
    string? VehicleTypesAvailableJson,
    double? AvgVehiclesAvailableToday, double? AvgDocksAvailableToday, int? SamplesToday);

public record DailyAverage(DateOnly Day, double? AvgVehiclesAvailable, double? AvgDocksAvailable, int Samples);

public record MapBike(
    string VehicleId, double Lat, double Lon,
    bool? IsReserved, bool? IsDisabled,
    string? VehicleTypeId,
    double? CurrentRangeMeters, double? CurrentFuelPercent,
    string? StationId, DateTime LatestFetchedUtc);

public record MapGeofencingZone(int ZoneIndex, string? Name, string GeometryGeoJson, string? RulesJson);

public record SystemListFilter(
    string? NameLike = null,
    string? LocationLike = null,
    string? CountryCode = null,
    string? AuthType = null,
    bool? Enabled = null);

public record SystemListRow(
    string SystemId, string Name, string? CountryCode, string? Location,
    string? SupportedVersions, string? AuthenticationType, bool IsEnabled,
    int StationCount, int ZoneCount, int StatusCount,
    DateTime? LastStatusFetchedUtc);

public record SystemAvailabilityOption(
    string SystemId, string Name, string? CountryCode, string? Location,
    int StationCount, int StatusCount,
    DateTime? FirstFetchedUtc, DateTime? LastFetchedUtc);

public record StationWithLatestStatus(
    string StationId, string? Name, double Lat, double Lon, int? Capacity,
    int? NumVehiclesAvailable, int? NumVehiclesDisabled,
    int? NumDocksAvailable, int? NumDocksDisabled,
    bool? IsInstalled, bool? IsRenting, bool? IsReturning,
    DateTime? LastReportedUtc, DateTime? LatestFetchedUtc);

public record StationRow(
    string StationId,
    string? Name,
    double Lat,
    double Lon,
    string? Address,
    int? Capacity,
    bool? IsVirtualStation,
    string? RentalUris);

public record QueryFilter(
    string? Country = null,
    string? NameLike = null,
    string? LocationLike = null,
    string? SystemId = null,
    string? AuthType = null,
    string? Version = null,
    int Top = 100);
