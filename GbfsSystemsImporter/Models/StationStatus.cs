namespace GbfsSystemsImporter.Models;

public class StationStatus
{
    public string StationId { get; set; } = "";
    public int? NumVehiclesAvailable { get; set; }
    public int? NumVehiclesDisabled { get; set; }
    public int? NumDocksAvailable { get; set; }
    public int? NumDocksDisabled { get; set; }
    public bool? IsInstalled { get; set; }
    public bool? IsRenting { get; set; }
    public bool? IsReturning { get; set; }
    public DateTime? LastReportedUtc { get; set; }
    public string? VehicleTypesAvailableJson { get; set; }
    public string? VehicleDocksAvailableJson { get; set; }
    public string? RawJson { get; set; }
}
