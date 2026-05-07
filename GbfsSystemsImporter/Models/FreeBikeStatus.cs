namespace GbfsSystemsImporter.Models;

public class FreeBikeStatus
{
    public string VehicleId { get; set; } = "";
    public double? Lat { get; set; }
    public double? Lon { get; set; }
    public bool? IsReserved { get; set; }
    public bool? IsDisabled { get; set; }
    public string? VehicleTypeId { get; set; }
    public double? CurrentRangeMeters { get; set; }
    public double? CurrentFuelPercent { get; set; }
    public string? StationId { get; set; }
    public string? PricingPlanId { get; set; }
    public DateTime? LastReportedUtc { get; set; }
    public string? RentalUrisJson { get; set; }
    public string? RawJson { get; set; }
}
