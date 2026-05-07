namespace GbfsSystemsImporter.Models;

public class GeofencingZone
{
    public int ZoneIndex { get; set; }
    public string? Name { get; set; }
    public DateTime? StartUtc { get; set; }
    public DateTime? EndUtc { get; set; }
    public string? RulesJson { get; set; }
    public string GeometryGeoJson { get; set; } = "";
    public string? RawJson { get; set; }
}
