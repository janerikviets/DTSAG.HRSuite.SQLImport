using System.Collections.ObjectModel;
using Microsoft.Win32;
using DTSAG.HRSuite.SQLImport.Models;
using DTSAG.HRSuite.SQLImport.Services;

namespace DTSAG.HRSuite.SQLImport.ViewModels;

public class MainViewModel : ViewModelBase
{
    private readonly AppSettings _settings;
    private List<string> _allTables = [];
    private List<Dictionary<string, string>> _csvData = [];

    // ── Settings ────────────────────────────────────────────────────────────

    public ObservableCollection<string> Servers { get; } = [];

    private string _newServer = string.Empty;
    public string NewServer { get => _newServer; set => Set(ref _newServer, value); }

    private string _selectedServer = string.Empty;
    public string SelectedServer
    {
        get => _selectedServer;
        set { if (Set(ref _selectedServer, value)) _settings.SelectedServer = value; }
    }

    private bool _useNtAuth = true;
    public bool UseNtAuth
    {
        get => _useNtAuth;
        set
        {
            if (Set(ref _useNtAuth, value))
            {
                OnPropertyChanged(nameof(UseSqlAuth));
                _settings.UseNtAuth = value;
            }
        }
    }
    public bool UseSqlAuth
    {
        get => !_useNtAuth;
        set => UseNtAuth = !value;
    }

    private string _username = string.Empty;
    public string Username
    {
        get => _username;
        set { Set(ref _username, value); _settings.Username = value; }
    }

    public string Password
    {
        get => _settings.Password;
        set => _settings.Password = value;
    }

    public ObservableCollection<string> Databases { get; } = [];

    private string _selectedDatabase = string.Empty;
    public string SelectedDatabase
    {
        get => _selectedDatabase;
        set { Set(ref _selectedDatabase, value); _settings.Database = value; }
    }

    private string _connectionStatus = string.Empty;
    public string ConnectionStatus { get => _connectionStatus; set => Set(ref _connectionStatus, value); }

    // ── Import ───────────────────────────────────────────────────────────────

    public ObservableCollection<string> FilteredTables { get; } = [];

    private string _tableSearch = string.Empty;
    public string TableSearch
    {
        get => _tableSearch;
        set { Set(ref _tableSearch, value); ApplyTableFilter(); }
    }

    private string _selectedTable = string.Empty;
    public string SelectedTable
    {
        get => _selectedTable;
        set
        {
            if (Set(ref _selectedTable, value) && !string.IsNullOrEmpty(value))
                _ = LoadColumnsAsync(value);
        }
    }

    private string _csvFilePath = string.Empty;
    public string CsvFilePath { get => _csvFilePath; set => Set(ref _csvFilePath, value); }

    private string _delimiter = ";";
    public string Delimiter
    {
        get => _delimiter;
        set
        {
            if (Set(ref _delimiter, value) && !string.IsNullOrEmpty(CsvFilePath))
                LoadCsvHeaders();
        }
    }

    public List<KeyValuePair<string, string>> DelimiterOptions { get; } =
    [
        new("Semikolon  ;", ";"),
        new("Komma  ,",     ","),
        new("Tab",          "\t"),
        new("Pipe  |",      "|"),
    ];

    private List<string> _csvHeadersWithIgnore = ["(ignorieren)"];
    public List<string> CsvHeadersWithIgnore
    {
        get => _csvHeadersWithIgnore;
        set => Set(ref _csvHeadersWithIgnore, value);
    }

    public ObservableCollection<ColumnMapping> ColumnMappings { get; } = [];

    private string _importStatus = string.Empty;
    public string ImportStatus { get => _importStatus; set => Set(ref _importStatus, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set => Set(ref _isBusy, value); }

    // ── Commands ──────────────────────────────────────────────────────────────

    public RelayCommand AddServerCommand { get; }
    public RelayCommand RemoveServerCommand { get; }
    public RelayCommand LoadDatabasesCommand { get; }
    public RelayCommand SaveSettingsCommand { get; }
    public RelayCommand BrowseCsvCommand { get; }
    public RelayCommand ReloadCsvCommand { get; }
    public RelayCommand LoadTablesCommand { get; }
    public RelayCommand AutoMapCommand { get; }
    public RelayCommand ImportCommand { get; }

    public MainViewModel()
    {
        _settings = SettingsService.Load();

        foreach (var s in _settings.Servers) Servers.Add(s);
        _selectedServer = _settings.SelectedServer;
        _useNtAuth = _settings.UseNtAuth;
        _username = _settings.Username;
        _selectedDatabase = _settings.Database;

        AddServerCommand = new RelayCommand(AddServer,
            () => !string.IsNullOrWhiteSpace(NewServer));
        RemoveServerCommand = new RelayCommand(RemoveServer,
            () => !string.IsNullOrEmpty(SelectedServer));
        LoadDatabasesCommand = new RelayCommand(
            async () => await LoadDatabasesAsync(),
            () => !IsBusy && !string.IsNullOrEmpty(SelectedServer));
        SaveSettingsCommand = new RelayCommand(SaveSettings);
        BrowseCsvCommand = new RelayCommand(BrowseCsv);
        ReloadCsvCommand = new RelayCommand(LoadCsvHeaders,
            () => !string.IsNullOrEmpty(CsvFilePath));
        LoadTablesCommand = new RelayCommand(
            async () => await LoadTablesAsync(),
            () => !IsBusy && !string.IsNullOrEmpty(SelectedDatabase));
        AutoMapCommand = new RelayCommand(AutoMap,
            () => ColumnMappings.Count > 0 && CsvHeadersWithIgnore.Count > 1);
        ImportCommand = new RelayCommand(
            async () => await ImportAsync(),
            () => !IsBusy && !string.IsNullOrEmpty(SelectedTable) && !string.IsNullOrEmpty(CsvFilePath));
    }

    // ── Settings actions ──────────────────────────────────────────────────────

    private void AddServer()
    {
        var name = NewServer.Trim();
        if (string.IsNullOrEmpty(name)) return;
        if (!Servers.Contains(name)) Servers.Add(name);
        SelectedServer = name;
        NewServer = string.Empty;
    }

    private void RemoveServer()
    {
        Servers.Remove(SelectedServer);
        SelectedServer = Servers.FirstOrDefault() ?? string.Empty;
    }

    private async Task LoadDatabasesAsync()
    {
        IsBusy = true;
        ConnectionStatus = "Verbinde mit Server...";
        try
        {
            var dbs = await new DatabaseService(_settings).GetDatabasesAsync();
            Databases.Clear();
            foreach (var db in dbs) Databases.Add(db);
            if (!string.IsNullOrEmpty(_settings.Database) && Databases.Contains(_settings.Database))
                SelectedDatabase = _settings.Database;
            else if (Databases.Count > 0)
                SelectedDatabase = Databases[0];
            ConnectionStatus = $"Verbunden  –  {dbs.Count} Datenbank(en) verfügbar.";
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"Fehler: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    private void SaveSettings()
    {
        _settings.Servers = [.. Servers];
        SettingsService.Save(_settings);
        ConnectionStatus = "Einstellungen gespeichert.";
    }

    // ── Import actions ────────────────────────────────────────────────────────

    private void BrowseCsv()
    {
        var dlg = new OpenFileDialog { Filter = "CSV-Dateien (*.csv;*.txt)|*.csv;*.txt|Alle Dateien (*.*)|*.*" };
        if (dlg.ShowDialog() != true) return;
        CsvFilePath = dlg.FileName;
        LoadCsvHeaders();
    }

    private void LoadCsvHeaders()
    {
        if (string.IsNullOrEmpty(CsvFilePath)) return;
        try
        {
            var delim = string.IsNullOrEmpty(Delimiter) ? ';' : Delimiter[0];
            var (headers, rows) = CsvService.Read(CsvFilePath, delim);
            _csvData = rows;
            var list = new List<string>(headers.Count + 1) { "(ignorieren)" };
            list.AddRange(headers);
            CsvHeadersWithIgnore = list;
            AutoMap();
            ImportStatus = $"CSV geladen: {headers.Count} Spalte(n), {rows.Count} Zeile(n).";
        }
        catch (Exception ex)
        {
            ImportStatus = $"CSV-Fehler: {ex.Message}";
        }
    }

    private async Task LoadTablesAsync()
    {
        IsBusy = true;
        ImportStatus = "Lade Tabellen...";
        try
        {
            _allTables = await new DatabaseService(_settings).GetTablesAsync();
            ApplyTableFilter();
            ImportStatus = $"{_allTables.Count} Tabelle(n) geladen.";
        }
        catch (Exception ex)
        {
            ImportStatus = $"Fehler: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    private void ApplyTableFilter()
    {
        var filter = _tableSearch.Trim();
        FilteredTables.Clear();
        foreach (var t in _allTables)
            if (string.IsNullOrEmpty(filter) || t.Contains(filter, StringComparison.OrdinalIgnoreCase))
                FilteredTables.Add(t);
    }

    private async Task LoadColumnsAsync(string table)
    {
        IsBusy = true;
        ImportStatus = $"Lade Spalten für '{table}'...";
        try
        {
            var cols = await new DatabaseService(_settings).GetColumnsAsync(table);
            ColumnMappings.Clear();
            foreach (var c in cols) ColumnMappings.Add(c);
            AutoMap();
            ImportStatus = $"'{table}': {cols.Count} Spalte(n).";
        }
        catch (Exception ex)
        {
            ImportStatus = $"Fehler beim Laden der Spalten: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    private void AutoMap()
    {
        if (CsvHeadersWithIgnore.Count <= 1) return;
        foreach (var m in ColumnMappings)
        {
            var match = CsvHeadersWithIgnore.Skip(1)
                .FirstOrDefault(h => string.Equals(h, m.DbColumn, StringComparison.OrdinalIgnoreCase));
            m.CsvColumn = match ?? "(ignorieren)";
        }
    }

    private async Task ImportAsync()
    {
        if (string.IsNullOrEmpty(CsvFilePath) || string.IsNullOrEmpty(SelectedTable)) return;
        IsBusy = true;
        ImportStatus = "Importiere Daten...";
        try
        {
            var delim = string.IsNullOrEmpty(Delimiter) ? ';' : Delimiter[0];
            var (_, rows) = CsvService.Read(CsvFilePath, delim);
            var count = await new DatabaseService(_settings).ImportAsync(SelectedTable, [.. ColumnMappings], rows);
            ImportStatus = $"Import erfolgreich: {count} Zeile(n) in '{SelectedTable}' importiert.";
        }
        catch (Exception ex)
        {
            ImportStatus = $"Import-Fehler: {ex.Message}";
        }
        finally { IsBusy = false; }
    }
}
