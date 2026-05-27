using System.Data;
using Microsoft.Data.SqlClient;
using DTSAG.HRSuite.SQLImport.Models;

namespace DTSAG.HRSuite.SQLImport.Services;

public class DatabaseService(AppSettings settings)
{
    private SqlConnection CreateConnection(string? dbOverride = null)
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = settings.SelectedServer,
            InitialCatalog = dbOverride ?? settings.Database,
            TrustServerCertificate = true,
            ConnectTimeout = 10
        };
        if (settings.UseNtAuth)
            b.IntegratedSecurity = true;
        else
        {
            b.UserID = settings.Username;
            b.Password = settings.Password;
        }
        return new SqlConnection(b.ConnectionString);
    }

    public async Task<List<string>> GetDatabasesAsync()
    {
        using var conn = CreateConnection("master");
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT name FROM sys.databases
            WHERE state_desc = 'ONLINE'
              AND name NOT IN ('master','tempdb','model','msdb')
            ORDER BY name
            """;
        using var r = await cmd.ExecuteReaderAsync();
        var result = new List<string>();
        while (await r.ReadAsync()) result.Add(r.GetString(0));
        return result;
    }

    public async Task<List<string>> GetTablesAsync()
    {
        using var conn = CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT TABLE_SCHEMA + '.' + TABLE_NAME
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_TYPE = 'BASE TABLE'
            ORDER BY TABLE_SCHEMA, TABLE_NAME
            """;
        using var r = await cmd.ExecuteReaderAsync();
        var result = new List<string>();
        while (await r.ReadAsync()) result.Add(r.GetString(0));
        return result;
    }

    public async Task<List<ColumnMapping>> GetColumnsAsync(string schemaTable)
    {
        var parts = schemaTable.Split('.', 2);
        var schema = parts.Length == 2 ? parts[0] : "dbo";
        var table = parts.Length == 2 ? parts[1] : parts[0];

        using var conn = CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = @s AND TABLE_NAME = @t
            ORDER BY ORDINAL_POSITION
            """;
        cmd.Parameters.AddWithValue("@s", schema);
        cmd.Parameters.AddWithValue("@t", table);
        using var r = await cmd.ExecuteReaderAsync();
        var result = new List<ColumnMapping>();
        while (await r.ReadAsync())
            result.Add(new() { DbColumn = r.GetString(0), DbColumnType = r.GetString(1), IsNullable = r.GetString(2) == "YES" });
        return result;
    }

    public async Task<int> ImportAsync(string schemaTable, List<ColumnMapping> mappings, List<Dictionary<string, string>> rows)
    {
        var active = mappings
            .Where(m => m.CsvColumn != "(ignorieren)" && !string.IsNullOrEmpty(m.CsvColumn))
            .ToList();
        if (active.Count == 0) return 0;

        var dt = BuildDataTable(active, rows);

        using var conn = CreateConnection();
        await conn.OpenAsync();
        using var bc = new SqlBulkCopy(conn) { DestinationTableName = schemaTable, BatchSize = 500 };
        foreach (var m in active) bc.ColumnMappings.Add(m.DbColumn, m.DbColumn);
        await bc.WriteToServerAsync(dt);
        return dt.Rows.Count;
    }

    public async Task<(int Inserted, int Updated)> MergeAsync(
        string schemaTable, List<ColumnMapping> mappings, List<Dictionary<string, string>> rows)
    {
        var active = mappings
            .Where(m => m.CsvColumn != "(ignorieren)" && !string.IsNullOrEmpty(m.CsvColumn))
            .ToList();
        var keyColumns = active.Where(m => m.IsKey).ToList();
        var valueColumns = active.Where(m => !m.IsKey).ToList();

        if (keyColumns.Count == 0)
            throw new InvalidOperationException(
                "Für den Update-Modus muss mindestens eine Schlüsselspalte (Häkchen in Spalte 'Schlüssel') ausgewählt sein.");

        var dt = BuildDataTable(active, rows);

        var parts = schemaTable.Split('.', 2);
        var bracketedTable = parts.Length == 2
            ? $"[{parts[0]}].[{parts[1]}]"
            : $"[dbo].[{parts[0]}]";

        using var conn = CreateConnection();
        await conn.OpenAsync();

        // Temp staging table mit NVARCHAR(MAX) – SQL Server konvertiert beim MERGE
        var colDefs = string.Join(", ", active.Select(m => $"[{m.DbColumn}] NVARCHAR(MAX)"));
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"CREATE TABLE #ImportStage ({colDefs})";
            await cmd.ExecuteNonQueryAsync();
        }

        using (var bc = new SqlBulkCopy(conn) { DestinationTableName = "#ImportStage", BatchSize = 500 })
        {
            foreach (var m in active) bc.ColumnMappings.Add(m.DbColumn, m.DbColumn);
            await bc.WriteToServerAsync(dt);
        }

        var onClause = string.Join(" AND ",
            keyColumns.Select(m => $"t.[{m.DbColumn}] = s.[{m.DbColumn}]"));

        var updateClause = valueColumns.Count > 0
            ? "WHEN MATCHED THEN UPDATE SET " +
              string.Join(", ", valueColumns.Select(m => $"t.[{m.DbColumn}] = s.[{m.DbColumn}]"))
            : string.Empty;

        var insertCols = string.Join(", ", active.Select(m => $"[{m.DbColumn}]"));
        var insertVals = string.Join(", ", active.Select(m => $"s.[{m.DbColumn}]"));

        var mergeSql = $"""
            MERGE {bracketedTable} AS t
            USING #ImportStage AS s ON {onClause}
            {updateClause}
            WHEN NOT MATCHED BY TARGET THEN
                INSERT ({insertCols}) VALUES ({insertVals})
            OUTPUT $action;
            """;

        int inserted = 0, updated = 0;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = mergeSql;
            cmd.CommandTimeout = 300;
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (reader.GetString(0) == "INSERT") inserted++;
                else updated++;
            }
        }

        return (inserted, updated);
    }

    private static DataTable BuildDataTable(List<ColumnMapping> active, List<Dictionary<string, string>> rows)
    {
        var dt = new DataTable();
        foreach (var m in active) dt.Columns.Add(m.DbColumn);
        foreach (var row in rows)
        {
            var dr = dt.NewRow();
            foreach (var m in active)
                dr[m.DbColumn] = row.TryGetValue(m.CsvColumn, out var v) && !string.IsNullOrEmpty(v)
                    ? v
                    : DBNull.Value;
            dt.Rows.Add(dr);
        }
        return dt;
    }
}
