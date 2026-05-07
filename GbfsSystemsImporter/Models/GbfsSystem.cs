using CsvHelper.Configuration.Attributes;

namespace GbfsSystemsImporter.Models;

public class GbfsSystem
{
    [Name("Country Code")]
    public string? CountryCode { get; set; }

    [Name("Name")]
    public string? Name { get; set; }

    [Name("Location")]
    public string? Location { get; set; }

    [Name("System ID")]
    public string? SystemId { get; set; }

    [Name("URL")]
    public string? Url { get; set; }

    [Name("Auto-Discovery URL")]
    public string? AutoDiscoveryUrl { get; set; }

    [Name("Supported Versions")]
    public string? SupportedVersions { get; set; }

    [Name("Authentication Info URL")]
    public string? AuthenticationInfoUrl { get; set; }

    [Name("Authentication Type")]
    public string? AuthenticationType { get; set; }

    [Name("Authentication Parameter Name")]
    public string? AuthenticationParameterName { get; set; }

    [Ignore]
    public bool? IsEnabled { get; set; }
}
