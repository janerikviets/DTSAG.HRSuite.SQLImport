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

        using var conn = CreateConnection();
        await conn.OpenAsync();
        using var bc = new SqlBulkCopy(conn) { DestinationTableName = schemaTable, BatchSize = 500 };
        foreach (var m in active) bc.ColumnMappings.Add(m.DbColumn, m.DbColumn);
        await bc.WriteToServerAsync(dt);
        return dt.Rows.Count;
    }
}
