using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using GbfsSystemsImporter.Models;

namespace GbfsSystemsImporter.Services;

public class CsvImportService
{
    public IEnumerable<GbfsSystem> ReadSystems(string path)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            MissingFieldFound = null,
            BadDataFound = null,
            TrimOptions = TrimOptions.Trim,
        };

        using var reader = new StreamReader(path);
        using var csv = new CsvReader(reader, config);

        foreach (var record in csv.GetRecords<GbfsSystem>())
        {
            yield return Normalize(record);
        }
    }

    private static GbfsSystem Normalize(GbfsSystem record) => new()
    {
        CountryCode = NullIfEmpty(record.CountryCode),
        Name = NullIfEmpty(record.Name),
        Location = NullIfEmpty(record.Location),
        SystemId = NullIfEmpty(record.SystemId),
        Url = NullIfEmpty(record.Url),
        AutoDiscoveryUrl = NullIfEmpty(record.AutoDiscoveryUrl),
        SupportedVersions = NullIfEmpty(record.SupportedVersions),
        AuthenticationInfoUrl = NullIfEmpty(record.AuthenticationInfoUrl),
        AuthenticationType = NullIfEmpty(record.AuthenticationType),
        AuthenticationParameterName = NullIfEmpty(record.AuthenticationParameterName),
    };

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
