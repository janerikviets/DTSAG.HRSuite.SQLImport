using System.Globalization;
using System.IO;
using CsvHelper;
using CsvHelper.Configuration;

namespace DTSAG.HRSuite.SQLImport.Services;

public static class CsvService
{
    public static (List<string> Headers, List<Dictionary<string, string>> Rows) Read(string path, char delimiter)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = delimiter.ToString(),
            BadDataFound = null,
            MissingFieldFound = null,
            TrimOptions = TrimOptions.Trim
        };
        using var sr = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
        using var csv = new CsvReader(sr, config);
        csv.Read();
        csv.ReadHeader();
        var headers = csv.HeaderRecord?.ToList() ?? [];
        var rows = new List<Dictionary<string, string>>();
        while (csv.Read())
        {
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in headers) row[h] = csv.GetField(h) ?? string.Empty;
            rows.Add(row);
        }
        return (headers, rows);
    }
}
