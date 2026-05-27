namespace DTSAG.HRSuite.SQLImport.Models;

public class AppSettings
{
    public List<string> Servers { get; set; } = [];
    public string SelectedServer { get; set; } = string.Empty;
    public bool UseNtAuth { get; set; } = true;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Database { get; set; } = string.Empty;
}
