using System.Diagnostics;
using GbfsSystemsImporter.Services;
using Spectre.Console;

namespace GbfsSystemsImporter;

internal static class Program
{
    private const int DefaultPort = 51919;
    private const string DefaultHost = "localhost";
    private const string DefaultPassword = "Yukon2025!Gbfs";

    private static int Main(string[] args)
    {
        try
        {
            return RunAsync(args).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {ex.Message}");
            if (Environment.GetEnvironmentVariable("GBFS_DEBUG") == "1")
                AnsiConsole.WriteException(ex);
            return 1;
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return 0;
        }

        var command = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();
        var opts = ParseOptions(rest);

        var port = opts.GetInt("port") ?? DefaultPort;
        var host = opts.Get("host") ?? DefaultHost;
        var password = opts.Get("password") ?? Environment.GetEnvironmentVariable("GBFS_SQL_PASSWORD") ?? DefaultPassword;
        var connString = DatabaseService.BuildConnectionString(host, port, password);
        var db = new DatabaseService(connString);

        return command switch
        {
            "db-up" => await DbUpAsync(opts),
            "db-down" => await DbDownAsync(),
            "init" => await InitAsync(db),
            "import" => await ImportAsync(db, opts),
            "count" => await CountAsync(db),
            "query" => await QueryAsync(db, opts),
            "group" => await GroupAsync(db, opts),
            "fetch" => await FetchAsync(db, opts),
            "import-stations" => await ImportStationsAsync(db, opts),
            "stations" => await ListStationsAsync(db, opts),
            "import-geofencing-zones" => await ImportGeofencingZonesAsync(db, opts),
            _ => UnknownCommand(command),
        };
    }

    private static int UnknownCommand(string cmd)
    {
        AnsiConsole.MarkupLineInterpolated($"[red]unknown command:[/] {cmd}");
        PrintUsage();
        return 2;
    }

    private static async Task<int> DbUpAsync(Options opts)
    {
        var port = opts.GetInt("port") ?? DefaultPort;
        var compose = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "docker-compose.yml"));
        AnsiConsole.MarkupLineInterpolated($"[grey]compose file:[/] {compose}");
        AnsiConsole.MarkupLineInterpolated($"[grey]host port:[/] {port}");

        var env = new Dictionary<string, string?> { ["GBFS_SQL_PORT"] = port.ToString() };
        var rc = await RunProcessAsync("docker", $"compose -f \"{compose}\" up -d", env);
        if (rc != 0) return rc;

        AnsiConsole.MarkupLine("[grey]waiting for SQL Server to accept connections…[/]");
        var host = opts.Get("host") ?? DefaultHost;
        var password = opts.Get("password") ?? Environment.GetEnvironmentVariable("GBFS_SQL_PASSWORD") ?? DefaultPassword;
        var conn = DatabaseService.BuildConnectionString(host, port, password);
        var db = new DatabaseService(conn);
        await db.WaitUntilReadyAsync(TimeSpan.FromMinutes(2));
        AnsiConsole.MarkupLine("[green]✓ SQL Server is ready[/]");
        return 0;
    }

    private static async Task<int> DbDownAsync()
    {
        var compose = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "docker-compose.yml"));
        return await RunProcessAsync("docker", $"compose -f \"{compose}\" down");
    }

    private static async Task<int> InitAsync(DatabaseService db)
    {
        await db.WaitUntilReadyAsync(TimeSpan.FromSeconds(30));
        await db.EnsureDatabaseAndTableAsync();
        AnsiConsole.MarkupLine("[green]✓ database and table ready[/]");
        return 0;
    }

    private static async Task<int> ImportAsync(DatabaseService db, Options opts)
    {
        var file = opts.Get("file") ?? "/Users/ricky/Downloads/systems (1).csv";
        if (!File.Exists(file))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]file not found:[/] {file}");
            return 1;
        }

        await db.WaitUntilReadyAsync(TimeSpan.FromSeconds(30));
        await db.EnsureDatabaseAndTableAsync();

        if (opts.Has("truncate"))
        {
            await db.TruncateAsync();
            AnsiConsole.MarkupLine("[yellow]table truncated[/]");
        }

        var csv = new CsvImportService();
        var rows = csv.ReadSystems(file).ToList();
        AnsiConsole.MarkupLineInterpolated($"[grey]parsed[/] {rows.Count} [grey]rows from[/] {file}");

        var inserted = await db.BulkInsertAsync(rows);
        AnsiConsole.MarkupLineInterpolated($"[green]✓ inserted[/] {inserted} [green]rows[/]");

        var total = await db.CountAsync();
        AnsiConsole.MarkupLineInterpolated($"[grey]total rows in table:[/] {total}");
        return 0;
    }

    private static async Task<int> CountAsync(DatabaseService db)
    {
        var total = await db.CountAsync();
        AnsiConsole.MarkupLineInterpolated($"[bold]{total}[/] rows in dbo.Systems");
        return 0;
    }

    private static async Task<int> QueryAsync(DatabaseService db, Options opts)
    {
        var filter = new QueryFilter(
            Country: opts.Get("country"),
            NameLike: opts.Get("name"),
            LocationLike: opts.Get("location"),
            SystemId: opts.Get("system-id"),
            AuthType: opts.Get("auth-type"),
            Version: opts.Get("version"),
            Top: opts.GetInt("top") ?? 100);

        var rows = await db.QueryAsync(filter);
        if (rows.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]no rows matched[/]");
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumns("CC", "Name", "Location", "System ID", "Versions", "Auth");
        foreach (var r in rows)
        {
            table.AddRow(
                Markup.Escape(r.CountryCode ?? ""),
                Markup.Escape(Truncate(r.Name, 40)),
                Markup.Escape(Truncate(r.Location, 30)),
                Markup.Escape(Truncate(r.SystemId, 30)),
                Markup.Escape(Truncate(r.SupportedVersions, 18)),
                Markup.Escape(r.AuthenticationType ?? ""));
        }
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLineInterpolated($"[grey]({rows.Count} rows, top={filter.Top})[/]");
        return 0;
    }

    private static async Task<int> GroupAsync(DatabaseService db, Options opts)
    {
        var column = opts.Get("by") ?? opts.Positional.FirstOrDefault() ?? "CountryCode";
        var rows = await db.GroupCountAsync(column);
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumns(column, "Count");
        foreach (var (g, c) in rows)
            table.AddRow(Markup.Escape(g), c.ToString());
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLineInterpolated($"[grey]({rows.Count} groups)[/]");
        return 0;
    }

    private static async Task<int> FetchAsync(DatabaseService db, Options opts)
    {
        var systemId = opts.Get("system-id") ?? opts.Positional.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(systemId))
        {
            AnsiConsole.MarkupLine("[red]usage:[/] gbfs fetch <system-id> [--feed <name>] [--homepage] [--list-feeds] [--raw] [--save <path>]");
            return 2;
        }

        var system = await db.GetBySystemIdAsync(systemId);
        if (system is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]system not found:[/] {systemId}");
            AnsiConsole.MarkupLine("[grey]tip: use [bold]query --name <substring>[/] to find a system id[/]");
            return 1;
        }

        var useHomepage = opts.Has("homepage");
        var url = useHomepage ? system.Url : (system.AutoDiscoveryUrl ?? system.Url);
        if (string.IsNullOrWhiteSpace(url))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]system has no {(useHomepage ? "homepage" : "auto-discovery")} URL[/]");
            return 1;
        }

        AnsiConsole.MarkupLineInterpolated($"[grey]system:[/] {Markup.Escape(system.Name ?? "")} ([grey]{Markup.Escape(system.CountryCode ?? "?")} / {Markup.Escape(system.Location ?? "?")}[/])");
        AnsiConsole.MarkupLineInterpolated($"[grey]GET[/] {Markup.Escape(url)}");

        var feed = new GbfsFeedService();
        var result = await feed.FetchAsync(url);
        AnsiConsole.MarkupLineInterpolated($"[grey]→ {result.StatusCode} {Markup.Escape(result.ContentType)} ({result.Body.Length:N0} bytes)[/]");

        if (!result.Success)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]request failed[/]");
            AnsiConsole.WriteLine(result.Body);
            return 1;
        }

        var feedName = opts.Get("feed");
        if (opts.Has("list-feeds"))
        {
            var names = GbfsFeedService.ListFeedNames(result.Body);
            if (names.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]no feeds listed (response is not a gbfs.json discovery doc)[/]");
                return 0;
            }
            var table = new Table().Border(TableBorder.Rounded).AddColumn("Feed");
            foreach (var n in names.OrderBy(x => x))
                table.AddRow(Markup.Escape(n));
            AnsiConsole.Write(table);
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(feedName))
        {
            var feedUrl = GbfsFeedService.TryFindFeedUrl(result.Body, feedName);
            if (feedUrl is null)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]feed not found:[/] {Markup.Escape(feedName)}");
                var available = GbfsFeedService.ListFeedNames(result.Body);
                if (available.Count > 0)
                    AnsiConsole.MarkupLineInterpolated($"[grey]available:[/] {Markup.Escape(string.Join(", ", available))}");
                return 1;
            }
            AnsiConsole.MarkupLineInterpolated($"[grey]GET[/] {Markup.Escape(feedUrl)}");
            result = await feed.FetchAsync(feedUrl);
            AnsiConsole.MarkupLineInterpolated($"[grey]→ {result.StatusCode} {Markup.Escape(result.ContentType)} ({result.Body.Length:N0} bytes)[/]");
            if (!result.Success)
            {
                AnsiConsole.WriteLine(result.Body);
                return 1;
            }
        }

        var output = result.ContentType.Contains("json", StringComparison.OrdinalIgnoreCase) && !opts.Has("raw")
            ? GbfsFeedService.PrettyPrintJson(result.Body)
            : result.Body;

        if (opts.Get("save") is { } savePath)
        {
            await File.WriteAllTextAsync(savePath, output);
            AnsiConsole.MarkupLineInterpolated($"[green]✓ saved to[/] {Markup.Escape(savePath)} [grey]({output.Length:N0} chars)[/]");
            return 0;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine(output);
        return 0;
    }

    private static async Task<int> ImportStationsAsync(DatabaseService db, Options opts)
    {
        var systemId = opts.Get("system-id") ?? opts.Positional.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(systemId))
        {
            AnsiConsole.MarkupLine("[red]usage:[/] gbfs import-stations <system-id>");
            return 2;
        }

        await db.WaitUntilReadyAsync(TimeSpan.FromSeconds(30));
        await db.EnsureDatabaseAndTableAsync();

        var system = await db.GetBySystemIdAsync(systemId);
        if (system is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]system not found:[/] {systemId}");
            return 1;
        }

        var discoveryUrl = system.AutoDiscoveryUrl;
        if (string.IsNullOrWhiteSpace(discoveryUrl))
        {
            AnsiConsole.MarkupLine("[red]system has no auto-discovery URL[/]");
            return 1;
        }

        AnsiConsole.MarkupLineInterpolated($"[grey]system:[/] {Markup.Escape(system.Name ?? "")} ({Markup.Escape(system.SystemId ?? "")})");
        var feed = new GbfsFeedService();

        AnsiConsole.MarkupLineInterpolated($"[grey]GET[/] {Markup.Escape(discoveryUrl)}");
        var discovery = await feed.FetchAsync(discoveryUrl);
        if (!discovery.Success)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]discovery fetch failed:[/] {discovery.StatusCode}");
            return 1;
        }

        var stationFeedUrl = GbfsFeedService.TryFindFeedUrl(discovery.Body, "station_information");
        if (stationFeedUrl is null)
        {
            AnsiConsole.MarkupLine("[yellow]this system has no station_information feed (likely free-floating only)[/]");
            return 0;
        }

        AnsiConsole.MarkupLineInterpolated($"[grey]GET[/] {Markup.Escape(stationFeedUrl)}");
        var stationsResp = await feed.FetchAsync(stationFeedUrl);
        if (!stationsResp.Success)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]station_information fetch failed:[/] {stationsResp.StatusCode}");
            return 1;
        }

        var stations = GbfsFeedService.ParseStationInformation(stationsResp.Body);
        AnsiConsole.MarkupLineInterpolated($"[grey]parsed[/] {stations.Count} [grey]stations[/]");

        var inserted = await db.ReplaceStationsAsync(systemId, stations);
        AnsiConsole.MarkupLineInterpolated($"[green]✓ stored[/] {inserted} [green]stations for[/] {Markup.Escape(systemId)}");
        return 0;
    }

    private static async Task<int> ImportGeofencingZonesAsync(DatabaseService db, Options opts)
    {
        var systemId = opts.Get("system-id") ?? opts.Positional.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(systemId))
        {
            AnsiConsole.MarkupLine("[red]usage:[/] gbfs import-geofencing-zones <system-id>");
            return 2;
        }

        await db.WaitUntilReadyAsync(TimeSpan.FromSeconds(30));
        await db.EnsureDatabaseAndTableAsync();

        var system = await db.GetBySystemIdAsync(systemId);
        if (system is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]system not found:[/] {systemId}");
            return 1;
        }

        var discoveryUrl = system.AutoDiscoveryUrl;
        if (string.IsNullOrWhiteSpace(discoveryUrl))
        {
            AnsiConsole.MarkupLine("[red]system has no auto-discovery URL[/]");
            return 1;
        }

        AnsiConsole.MarkupLineInterpolated($"[grey]system:[/] {Markup.Escape(system.Name ?? "")} ({Markup.Escape(system.SystemId ?? "")})");
        var feed = new GbfsFeedService();

        AnsiConsole.MarkupLineInterpolated($"[grey]GET[/] {Markup.Escape(discoveryUrl)}");
        var discovery = await feed.FetchAsync(discoveryUrl);
        if (!discovery.Success)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]discovery fetch failed:[/] {discovery.StatusCode}");
            return 1;
        }

        var zonesUrl = GbfsFeedService.TryFindFeedUrl(discovery.Body, "geofencing_zones");
        if (zonesUrl is null)
        {
            AnsiConsole.MarkupLine("[yellow]this system has no geofencing_zones feed[/]");
            return 0;
        }

        AnsiConsole.MarkupLineInterpolated($"[grey]GET[/] {Markup.Escape(zonesUrl)}");
        var zonesResp = await feed.FetchAsync(zonesUrl);
        if (!zonesResp.Success)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]geofencing_zones fetch failed:[/] {zonesResp.StatusCode}");
            return 1;
        }

        var zones = GbfsFeedService.ParseGeofencingZones(zonesResp.Body);
        AnsiConsole.MarkupLineInterpolated($"[grey]parsed[/] {zones.Count} [grey]zones[/]");

        var inserted = await db.ReplaceGeofencingZonesAsync(systemId, zones);
        AnsiConsole.MarkupLineInterpolated($"[green]✓ stored[/] {inserted} [green]geofencing zones for[/] {Markup.Escape(systemId)}");
        return 0;
    }

    private static async Task<int> ListStationsAsync(DatabaseService db, Options opts)
    {
        var systemId = opts.Get("system-id") ?? opts.Positional.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(systemId))
        {
            AnsiConsole.MarkupLine("[red]usage:[/] gbfs stations <system-id> [--top N]");
            return 2;
        }
        var top = opts.GetInt("top") ?? 50;
        var rows = await db.ListStationsAsync(systemId, top);
        if (rows.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]no stations stored for that system. run [bold]import-stations[/] first[/]");
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumns("Station ID", "Name", "Lat", "Lon", "Capacity", "Virtual", "Rental URIs");
        foreach (var r in rows)
        {
            table.AddRow(
                Markup.Escape(Truncate(r.StationId, 24)),
                Markup.Escape(Truncate(r.Name, 32)),
                r.Lat.ToString("F4"),
                r.Lon.ToString("F4"),
                r.Capacity?.ToString() ?? "",
                r.IsVirtualStation?.ToString() ?? "",
                Markup.Escape(Truncate(r.RentalUris, 40)));
        }
        AnsiConsole.Write(table);

        var total = await db.StationCountAsync(systemId);
        AnsiConsole.MarkupLineInterpolated($"[grey]({rows.Count} of {total} for {Markup.Escape(systemId)})[/]");
        return 0;
    }

    private static async Task<int> RunProcessAsync(string fileName, string arguments, IDictionary<string, string?>? env = null)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (env is not null)
            foreach (var (k, v) in env)
                psi.Environment[k] = v;

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) AnsiConsole.WriteLine(e.Data); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data is not null) AnsiConsole.MarkupLineInterpolated($"[grey]{e.Data}[/]"); };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        await proc.WaitForExitAsync();
        return proc.ExitCode;
    }

    private static string Truncate(string? s, int max) =>
        s is null ? "" : s.Length <= max ? s : s[..(max - 1)] + "…";

    private static Options ParseOptions(string[] args)
    {
        var flags = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var positional = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--"))
            {
                var key = a[2..];
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                {
                    flags[key] = args[++i];
                }
                else
                {
                    flags[key] = null;
                }
            }
            else
            {
                positional.Add(a);
            }
        }
        return new Options(flags, positional);
    }

    private static void PrintUsage()
    {
        var help = """
        [bold]gbfs[/] — import & query GBFS systems CSV in SQL Server

        [bold]Commands[/]
          db-up                     start the SQL Server 2025 docker container
          db-down                   stop the docker container
          init                      create database and table
          import                    import the CSV (default: /Users/ricky/Downloads/systems (1).csv)
          count                     show row count
          query                     query rows with filters
          group [column|--by col]   group counts by column
          fetch <system-id>         fetch the system's GBFS data and display it
          import-stations <id>      import the station_information feed for a system into dbo.Stations
          stations <id>             list stored stations for a system
          import-geofencing-zones <id>  import the geofencing_zones feed for a system into dbo.GeofencingZones

        [bold]Connection options[/] (apply to all data commands)
          --host <h>        default localhost
          --port <p>        default 51919
          --password <pw>   default Yukon2025!Gbfs (env: GBFS_SQL_PASSWORD)

        [bold]import[/]
          --file <path>     CSV file path
          --truncate        wipe table before import

        [bold]query[/] filters
          --country <CC>            ISO country code (exact)
          --name <substring>        name contains
          --location <substring>    location contains
          --system-id <id>          system id (exact)
          --auth-type <type>        auth type (exact, e.g. api_key, oauth_client_credentials_grant)
          --version <v>             supported versions contains (e.g. 3.0)
          --top <n>                 max rows (default 100)

        [bold]fetch[/]
          <system-id>             positional system id (or use --system-id)
          --feed <name>           follow a feed listed in gbfs.json (e.g. station_information)
          --list-feeds            list feeds available in the discovery doc and exit
          --homepage              use the homepage URL instead of the auto-discovery URL
          --raw                   skip JSON pretty-printing
          --save <path>           write the response body to a file instead of stdout

        [bold]Examples[/]
          gbfs db-up
          gbfs init
          gbfs import --truncate
          gbfs count
          gbfs query --country GB --top 20
          gbfs query --name dott --version 3.0
          gbfs query --auth-type api_key --top 50
          gbfs group CountryCode
          gbfs group --by AuthenticationType
          gbfs fetch careem_bike
          gbfs fetch careem_bike --list-feeds
          gbfs fetch careem_bike --feed station_information
          gbfs fetch careem_bike --feed system_information --save out.json
          gbfs import-stations careem_bike
          gbfs stations careem_bike --top 20
          gbfs import-geofencing-zones beryl_norwich
        """;
        AnsiConsole.MarkupLine(help);
    }
}

internal sealed record Options(IDictionary<string, string?> Flags, IReadOnlyList<string> Positional)
{
    public string? Get(string key) =>
        Flags.TryGetValue(key, out var v) ? v : null;

    public int? GetInt(string key) =>
        Get(key) is { } s && int.TryParse(s, out var n) ? n : null;

    public bool Has(string key) =>
        Flags.ContainsKey(key);
}
