using System.IO;
using System.Text.Json;
using DTSAG.HRSuite.SQLImport.Models;

namespace DTSAG.HRSuite.SQLImport.Services;

public static class SettingsService
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DTSAG", "HRSuite", "SQLImport", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new();
        }
        catch { return new(); }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, opts));
    }
}
