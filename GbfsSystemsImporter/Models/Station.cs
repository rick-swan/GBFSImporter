namespace GbfsSystemsImporter.Models;

public class Station
{
    public string StationId { get; set; } = "";
    public string? Name { get; set; }
    public string? ShortName { get; set; }
    public double Lat { get; set; }
    public double Lon { get; set; }
    public string? Address { get; set; }
    public string? CrossStreet { get; set; }
    public string? RegionId { get; set; }
    public string? PostCode { get; set; }
    public string? StationType { get; set; }
    public int? Capacity { get; set; }
    public bool? IsVirtualStation { get; set; }
    public bool? IsValetStation { get; set; }
    public bool? IsChargingStation { get; set; }
    public string? ParkingType { get; set; }
    public bool? ParkingHoop { get; set; }
    public string? ContactPhone { get; set; }
    public string? RentalMethods { get; set; }
    public string? RentalUrisJson { get; set; }
    public string? RawJson { get; set; }
}
