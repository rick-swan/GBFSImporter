using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using GbfsSystemsImporter.Models;

namespace GbfsSystemsImporter.Services;

public class GbfsFeedService
{
    private readonly HttpClient _http;

    public GbfsFeedService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GbfsSystemsImporter", "1.0"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.5));
    }

    public async Task<FetchResult> FetchAsync(string url, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(url, ct);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        var body = await response.Content.ReadAsStringAsync(ct);
        return new FetchResult(
            Url: url,
            StatusCode: (int)response.StatusCode,
            ContentType: contentType,
            Body: body,
            Success: response.IsSuccessStatusCode);
    }

    /// <summary>
    /// Given the body of a gbfs.json discovery document, find the URL for a named feed.
    /// Returns null if the document isn't a gbfs.json or the feed isn't listed.
    /// </summary>
    public static string? TryFindFeedUrl(string discoveryJson, string feedName)
    {
        try
        {
            using var doc = JsonDocument.Parse(discoveryJson);
            if (!doc.RootElement.TryGetProperty("data", out var data))
                return null;

            // v1.x/2.x: data is keyed by language -> { feeds: [...] }
            // v3.x: data.feeds: [...] (no language key)
            if (data.TryGetProperty("feeds", out var feedsV3) && feedsV3.ValueKind == JsonValueKind.Array)
                return ExtractFeedUrl(feedsV3, feedName);

            foreach (var lang in data.EnumerateObject())
            {
                if (lang.Value.TryGetProperty("feeds", out var feeds) && feeds.ValueKind == JsonValueKind.Array)
                {
                    var url = ExtractFeedUrl(feeds, feedName);
                    if (url is not null) return url;
                }
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parse a gbfs.json discovery doc to extract last_updated/ttl/version/feeds in one pass.
    /// Returns null if the body is not valid JSON.
    /// </summary>
    public static DiscoveryInfo? ParseDiscovery(string discoveryJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(discoveryJson);
            var root = doc.RootElement;

            DateTime? lastUpdated = null;
            if (root.TryGetProperty("last_updated", out var lu))
            {
                lastUpdated = lu.ValueKind switch
                {
                    JsonValueKind.Number when lu.TryGetInt64(out var unix) => DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime,
                    JsonValueKind.String when DateTime.TryParse(lu.GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt) => dt,
                    _ => null,
                };
            }

            int? ttl = null;
            if (root.TryGetProperty("ttl", out var t)
                && t.ValueKind == JsonValueKind.Number
                && t.TryGetInt32(out var ttlSec))
            {
                ttl = ttlSec;
            }

            string? version = null;
            if (root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
                version = v.GetString();

            var feeds = new List<FeedListing>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (root.TryGetProperty("data", out var data))
            {
                if (data.TryGetProperty("feeds", out var feedsV3) && feedsV3.ValueKind == JsonValueKind.Array)
                {
                    ExtractFeedListings(feedsV3, feeds, seen);
                }
                else
                {
                    foreach (var lang in data.EnumerateObject())
                        if (lang.Value.TryGetProperty("feeds", out var f) && f.ValueKind == JsonValueKind.Array)
                            ExtractFeedListings(f, feeds, seen);
                }
            }

            return new DiscoveryInfo(lastUpdated, ttl, version, feeds);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void ExtractFeedListings(JsonElement feedsArr, List<FeedListing> result, HashSet<string> seen)
    {
        foreach (var feed in feedsArr.EnumerateArray())
        {
            if (feed.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                && feed.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
            {
                var name = n.GetString()!;
                if (seen.Add(name))
                    result.Add(new FeedListing(name, u.GetString()!));
            }
        }
    }

    public static IReadOnlyList<string> ListFeedNames(string discoveryJson)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(discoveryJson);
            if (!doc.RootElement.TryGetProperty("data", out var data))
                return Array.Empty<string>();

            if (data.TryGetProperty("feeds", out var feedsV3) && feedsV3.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in feedsV3.EnumerateArray())
                    if (f.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                        names.Add(n.GetString()!);
                return names.ToList();
            }

            foreach (var lang in data.EnumerateObject())
            {
                if (!lang.Value.TryGetProperty("feeds", out var feeds) || feeds.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var f in feeds.EnumerateArray())
                    if (f.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                        names.Add(n.GetString()!);
            }
            return names.ToList();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Parses a station_information feed body into Station records.
    /// Handles GBFS v1/v2 (name as string) and v3 (name as array of localised strings).
    /// </summary>
    public static List<Station> ParseStationInformation(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data)) return new();
        if (!data.TryGetProperty("stations", out var stationsArr) || stationsArr.ValueKind != JsonValueKind.Array)
            return new();

        var list = new List<Station>(stationsArr.GetArrayLength());
        foreach (var s in stationsArr.EnumerateArray())
        {
            if (!s.TryGetProperty("station_id", out var sidEl) || sidEl.ValueKind != JsonValueKind.String)
                continue;
            if (!TryGetDouble(s, "lat", out var lat) || !TryGetDouble(s, "lon", out var lon))
                continue;

            list.Add(new Station
            {
                StationId         = sidEl.GetString()!,
                Name              = ExtractLocalised(s, "name"),
                ShortName         = ExtractLocalised(s, "short_name"),
                Lat               = lat,
                Lon               = lon,
                Address           = TryGetString(s, "address"),
                CrossStreet       = TryGetString(s, "cross_street"),
                RegionId          = TryGetString(s, "region_id"),
                PostCode          = TryGetString(s, "post_code"),
                StationType       = TryGetString(s, "station_type"),
                Capacity          = TryGetInt(s, "capacity"),
                IsVirtualStation  = TryGetBool(s, "is_virtual_station"),
                IsValetStation    = TryGetBool(s, "is_valet_station"),
                IsChargingStation = TryGetBool(s, "is_charging_station"),
                ParkingType       = TryGetString(s, "parking_type"),
                ParkingHoop       = TryGetBool(s, "parking_hoop"),
                ContactPhone      = TryGetString(s, "contact_phone"),
                RentalMethods     = ExtractStringArray(s, "rental_methods"),
                RentalUrisJson    = ExtractRawJson(s, "rental_uris"),
                RawJson           = s.GetRawText(),
            });
        }
        return list;
    }

    private static string? ExtractLocalised(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Array  => FirstLocalisedText(el),
            _                    => null,
        };
    }

    private static string? FirstLocalisedText(JsonElement array)
    {
        foreach (var item in array.EnumerateArray())
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("text", out var t)
                && t.ValueKind == JsonValueKind.String)
                return t.GetString();
        return null;
    }

    private static string? ExtractStringArray(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var el) || el.ValueKind != JsonValueKind.Array)
            return null;
        var values = new List<string>();
        foreach (var item in el.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String) values.Add(item.GetString()!);
        return values.Count == 0 ? null : string.Join(",", values);
    }

    private static string? ExtractRawJson(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var el) && el.ValueKind != JsonValueKind.Null
            ? el.GetRawText()
            : null;

    private static string? TryGetString(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static int? TryGetInt(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var el)) return null;
        return el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n) ? n : null;
    }

    private static bool? TryGetBool(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.True  => true,
            JsonValueKind.False => false,
            _                   => null,
        };
    }

    private static bool TryGetDouble(JsonElement parent, string property, out double value)
    {
        value = 0;
        if (!parent.TryGetProperty(property, out var el)) return false;
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.TryGetDouble(out value),
            JsonValueKind.String => double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value),
            _                    => false,
        };
    }

    /// <summary>
    /// Parses a station_status feed body into StationStatus records.
    /// Handles the v2 (num_bikes_*) and v3 (num_vehicles_*) field naming.
    /// </summary>
    public static List<StationStatus> ParseStationStatuses(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data)) return new();
        if (!data.TryGetProperty("stations", out var stationsArr) || stationsArr.ValueKind != JsonValueKind.Array)
            return new();

        var list = new List<StationStatus>(stationsArr.GetArrayLength());
        foreach (var s in stationsArr.EnumerateArray())
        {
            if (!s.TryGetProperty("station_id", out var sidEl) || sidEl.ValueKind != JsonValueKind.String)
                continue;

            list.Add(new StationStatus
            {
                StationId                 = sidEl.GetString()!,
                NumVehiclesAvailable      = TryGetInt(s, "num_vehicles_available") ?? TryGetInt(s, "num_bikes_available"),
                NumVehiclesDisabled       = TryGetInt(s, "num_vehicles_disabled")  ?? TryGetInt(s, "num_bikes_disabled"),
                NumDocksAvailable         = TryGetInt(s, "num_docks_available"),
                NumDocksDisabled          = TryGetInt(s, "num_docks_disabled"),
                IsInstalled               = TryGetBool(s, "is_installed"),
                IsRenting                 = TryGetBool(s, "is_renting"),
                IsReturning               = TryGetBool(s, "is_returning"),
                LastReportedUtc           = TryGetTimestamp(s, "last_reported"),
                VehicleTypesAvailableJson = ExtractRawJson(s, "vehicle_types_available"),
                VehicleDocksAvailableJson = ExtractRawJson(s, "vehicle_docks_available"),
                RawJson                   = s.GetRawText(),
            });
        }
        return list;
    }

    /// <summary>
    /// last_reported is a unix timestamp (number) in v1/v2 and an ISO 8601 string in v3.
    /// </summary>
    private static DateTime? TryGetTimestamp(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetInt64(out var unix) => DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime,
            JsonValueKind.String when DateTime.TryParse(el.GetString(), CultureInfo.InvariantCulture,
                                                       DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                                                       out var dt) => dt,
            _ => null,
        };
    }

    /// <summary>
    /// Parses a geofencing_zones feed body into GeofencingZone records.
    /// Spec: data.geofencing_zones is a GeoJSON FeatureCollection (v2.1+/v3) or
    /// historically data.{lang}.geofencing_zones.geofencing_zones (v2.1 with lang key).
    /// </summary>
    public static List<GeofencingZone> ParseGeofencingZones(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data))
            return new();

        var fc = FindFeatureCollection(data);
        if (fc is null || !fc.Value.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
            return new();

        var list = new List<GeofencingZone>(features.GetArrayLength());
        var i = 0;
        foreach (var feature in features.EnumerateArray())
        {
            if (!feature.TryGetProperty("geometry", out var geometry) || geometry.ValueKind != JsonValueKind.Object)
                continue;

            string? name = null;
            string? rulesJson = null;
            DateTime? startUtc = null;
            DateTime? endUtc = null;
            if (feature.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
            {
                name = ExtractLocalised(props, "name");
                rulesJson = ExtractRawJson(props, "rules");
                startUtc = TryGetTimestamp(props, "start");
                endUtc = TryGetTimestamp(props, "end");
            }

            list.Add(new GeofencingZone
            {
                ZoneIndex       = i++,
                Name            = name,
                StartUtc        = startUtc,
                EndUtc          = endUtc,
                RulesJson       = rulesJson,
                GeometryGeoJson = geometry.GetRawText(),
                RawJson         = feature.GetRawText(),
            });
        }
        return list;
    }

    private static JsonElement? FindFeatureCollection(JsonElement data)
    {
        if (data.TryGetProperty("geofencing_zones", out var gz))
        {
            if (gz.ValueKind == JsonValueKind.Object && gz.TryGetProperty("type", out var t)
                && t.ValueKind == JsonValueKind.String && t.GetString() == "FeatureCollection")
                return gz;

            if (gz.ValueKind == JsonValueKind.Object && gz.TryGetProperty("geofencing_zones", out var inner)
                && inner.ValueKind == JsonValueKind.Object)
                return inner;
        }

        foreach (var lang in data.EnumerateObject())
        {
            if (lang.Value.ValueKind != JsonValueKind.Object) continue;
            if (lang.Value.TryGetProperty("geofencing_zones", out var langGz) && langGz.ValueKind == JsonValueKind.Object)
            {
                if (langGz.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "FeatureCollection")
                    return langGz;
                if (langGz.TryGetProperty("geofencing_zones", out var inner2) && inner2.ValueKind == JsonValueKind.Object)
                    return inner2;
            }
        }
        return null;
    }

    /// <summary>
    /// Resolve the feed URL for free-floating vehicles. Checks both the v1/v2 name
    /// (free_bike_status) and the v3 name (vehicle_status). Returns null if neither
    /// is listed in the discovery doc.
    /// </summary>
    public static string? TryFindFreeBikeStatusUrl(string discoveryJson) =>
        TryFindFeedUrl(discoveryJson, "free_bike_status")
        ?? TryFindFeedUrl(discoveryJson, "vehicle_status");

    /// <summary>
    /// Parse a free_bike_status (v1/v2) or vehicle_status (v3) feed body.
    /// Records under either "bikes" or "vehicles", IDed by either "bike_id" or "vehicle_id".
    /// </summary>
    public static List<FreeBikeStatus> ParseFreeBikeStatuses(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data)) return new();

        JsonElement arr;
        if (data.TryGetProperty("bikes", out var bikes) && bikes.ValueKind == JsonValueKind.Array)
            arr = bikes;
        else if (data.TryGetProperty("vehicles", out var vehicles) && vehicles.ValueKind == JsonValueKind.Array)
            arr = vehicles;
        else
            return new();

        var list = new List<FreeBikeStatus>(arr.GetArrayLength());
        foreach (var v in arr.EnumerateArray())
        {
            string? vehicleId = TryGetString(v, "vehicle_id") ?? TryGetString(v, "bike_id");
            if (string.IsNullOrEmpty(vehicleId)) continue;

            list.Add(new FreeBikeStatus
            {
                VehicleId          = vehicleId,
                Lat                = TryGetDoubleNullable(v, "lat"),
                Lon                = TryGetDoubleNullable(v, "lon"),
                IsReserved         = TryGetBool(v, "is_reserved"),
                IsDisabled         = TryGetBool(v, "is_disabled"),
                VehicleTypeId      = TryGetString(v, "vehicle_type_id"),
                CurrentRangeMeters = TryGetDoubleNullable(v, "current_range_meters"),
                CurrentFuelPercent = TryGetDoubleNullable(v, "current_fuel_percent"),
                StationId          = TryGetString(v, "station_id"),
                PricingPlanId      = TryGetString(v, "pricing_plan_id"),
                LastReportedUtc    = TryGetTimestamp(v, "last_reported"),
                RentalUrisJson     = ExtractRawJson(v, "rental_uris"),
                RawJson            = v.GetRawText(),
            });
        }
        return list;
    }

    private static double? TryGetDoubleNullable(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetDouble(out var d) => d,
            JsonValueKind.String when double.TryParse(el.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }

    public static string PrettyPrintJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static string? ExtractFeedUrl(JsonElement feedsArray, string feedName)
    {
        foreach (var feed in feedsArray.EnumerateArray())
        {
            if (feed.TryGetProperty("name", out var name)
                && name.ValueKind == JsonValueKind.String
                && string.Equals(name.GetString(), feedName, StringComparison.OrdinalIgnoreCase)
                && feed.TryGetProperty("url", out var url)
                && url.ValueKind == JsonValueKind.String)
            {
                return url.GetString();
            }
        }
        return null;
    }
}

public record FetchResult(string Url, int StatusCode, string ContentType, string Body, bool Success);

public record FeedListing(string Name, string Url);

public record DiscoveryInfo(DateTime? LastUpdatedUtc, int? TtlSeconds, string? Version, List<FeedListing> Feeds);
