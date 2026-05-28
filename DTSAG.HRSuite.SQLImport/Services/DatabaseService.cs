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

    private static string BracketTable(string schemaTable)
    {
        var parts = schemaTable.Split('.', 2);
        return parts.Length == 2 ? $"[{parts[0]}].[{parts[1]}]" : $"[dbo].[{parts[0]}]";
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
            SELECT
                c.COLUMN_NAME,
                c.DATA_TYPE,
                c.IS_NULLABLE,
                CAST(ISNULL((
                    SELECT 1
                    FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                    JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE ku
                        ON tc.CONSTRAINT_NAME = ku.CONSTRAINT_NAME
                       AND tc.TABLE_SCHEMA    = ku.TABLE_SCHEMA
                       AND tc.TABLE_NAME      = ku.TABLE_NAME
                    WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
                      AND tc.TABLE_SCHEMA    = c.TABLE_SCHEMA
                      AND tc.TABLE_NAME      = c.TABLE_NAME
                      AND ku.COLUMN_NAME     = c.COLUMN_NAME
                ), 0) AS BIT) AS IS_PK,
                CAST(ISNULL((
                    SELECT 1
                    FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                    JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE ku
                        ON tc.CONSTRAINT_NAME = ku.CONSTRAINT_NAME
                       AND tc.TABLE_SCHEMA    = ku.TABLE_SCHEMA
                       AND tc.TABLE_NAME      = ku.TABLE_NAME
                    WHERE tc.CONSTRAINT_TYPE = 'FOREIGN KEY'
                      AND tc.TABLE_SCHEMA    = c.TABLE_SCHEMA
                      AND tc.TABLE_NAME      = c.TABLE_NAME
                      AND ku.COLUMN_NAME     = c.COLUMN_NAME
                ), 0) AS BIT) AS IS_FK,
                CAST(ISNULL(
                    COLUMNPROPERTY(
                        OBJECT_ID(QUOTENAME(c.TABLE_SCHEMA) + '.' + QUOTENAME(c.TABLE_NAME)),
                        c.COLUMN_NAME, 'IsIdentity'
                    ), 0
                ) AS BIT) AS IS_IDENTITY
            FROM INFORMATION_SCHEMA.COLUMNS c
            WHERE c.TABLE_SCHEMA = @s AND c.TABLE_NAME = @t
            ORDER BY c.ORDINAL_POSITION
            """;
        cmd.Parameters.AddWithValue("@s", schema);
        cmd.Parameters.AddWithValue("@t", table);

        using var r = await cmd.ExecuteReaderAsync();
        var result = new List<ColumnMapping>();
        while (await r.ReadAsync())
            result.Add(new()
            {
                DbColumn = r.GetString(0),
                DbColumnType = r.GetString(1),
                IsNullable = r.GetString(2) == "YES",
                IsPrimaryKey = r.GetBoolean(3),
                IsForeignKey = r.GetBoolean(4),
                IsIdentity = r.GetBoolean(5)
            });
        return result;
    }

    public async Task<int> ImportAsync(
        string schemaTable, List<ColumnMapping> mappings,
        List<Dictionary<string, string>> rows,
        bool identityInsert, bool disableConstraints)
    {
        var active = mappings
            .Where(m => (m.CsvColumn != "(ignorieren)" && !string.IsNullOrEmpty(m.CsvColumn))
                     || !string.IsNullOrEmpty(m.FixedValue))
            .ToList();
        if (active.Count == 0) return 0;

        var dt = BuildDataTable(active, rows);
        var bracketedTable = BracketTable(schemaTable);
        var hasIdentityInActive = active.Any(m => m.IsIdentity);

        using var conn = CreateConnection();
        await conn.OpenAsync();

        if (disableConstraints)
            await ExecAsync(conn, $"ALTER TABLE {bracketedTable} NOCHECK CONSTRAINT ALL");

        try
        {
            var colDefs = string.Join(", ", active.Select(m => $"[{m.DbColumn}] NVARCHAR(MAX)"));
            await ExecAsync(conn, $"CREATE TABLE #ImportStage ({colDefs})");

            using (var bc = new SqlBulkCopy(conn)
                   { DestinationTableName = "#ImportStage", BatchSize = 500 })
            {
                foreach (var m in active) bc.ColumnMappings.Add(m.DbColumn, m.DbColumn);
                await bc.WriteToServerAsync(dt);
            }

            if (identityInsert && hasIdentityInActive)
                await ExecAsync(conn, $"SET IDENTITY_INSERT {bracketedTable} ON");

            try
            {
                var cols = string.Join(", ", active.Select(m => $"[{m.DbColumn}]"));
                await ExecAsync(conn,
                    $"INSERT INTO {bracketedTable} ({cols}) SELECT {cols} FROM #ImportStage");
            }
            finally
            {
                if (identityInsert && hasIdentityInActive)
                    await ExecAsync(conn, $"SET IDENTITY_INSERT {bracketedTable} OFF");
            }
        }
        finally
        {
            if (disableConstraints)
                await ExecAsync(conn, $"ALTER TABLE {bracketedTable} CHECK CONSTRAINT ALL");
        }

        return dt.Rows.Count;
    }

    public async Task<(int Inserted, int Updated)> MergeAsync(
        string schemaTable, List<ColumnMapping> mappings,
        List<Dictionary<string, string>> rows,
        bool identityInsert, bool disableConstraints)
    {
        var active = mappings
            .Where(m => (m.CsvColumn != "(ignorieren)" && !string.IsNullOrEmpty(m.CsvColumn))
                     || !string.IsNullOrEmpty(m.FixedValue))
            .ToList();
        var keyColumns = active.Where(m => m.IsKey).ToList();
        var valueColumns = active.Where(m => !m.IsKey).ToList();

        if (keyColumns.Count == 0)
            throw new InvalidOperationException(
                "Für den Update-Modus muss mindestens eine Schlüsselspalte (Häkchen in Spalte 'Schlüssel') ausgewählt sein.");

        var hasIdentityInActive = active.Any(m => m.IsIdentity);
        var dt = BuildDataTable(active, rows);
        var bracketedTable = BracketTable(schemaTable);

        using var conn = CreateConnection();
        await conn.OpenAsync();

        if (disableConstraints)
            await ExecAsync(conn, $"ALTER TABLE {bracketedTable} NOCHECK CONSTRAINT ALL");

        try
        {
            // Temp staging table (NVARCHAR MAX – SQL Server converts on MERGE)
            var colDefs = string.Join(", ", active.Select(m => $"[{m.DbColumn}] NVARCHAR(MAX)"));
            await ExecAsync(conn, $"CREATE TABLE #ImportStage ({colDefs})");

            using (var bc = new SqlBulkCopy(conn)
                   { DestinationTableName = "#ImportStage", BatchSize = 500 })
            {
                foreach (var m in active) bc.ColumnMappings.Add(m.DbColumn, m.DbColumn);
                await bc.WriteToServerAsync(dt);
            }

            if (identityInsert && hasIdentityInActive)
                await ExecAsync(conn, $"SET IDENTITY_INSERT {bracketedTable} ON");

            try
            {
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
                using var cmd = conn.CreateCommand();
                cmd.CommandText = mergeSql;
                cmd.CommandTimeout = 300;
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    if (reader.GetString(0) == "INSERT") inserted++;
                    else updated++;
                }

                return (inserted, updated);
            }
            finally
            {
                if (identityInsert && hasIdentityInActive)
                    await ExecAsync(conn, $"SET IDENTITY_INSERT {bracketedTable} OFF");
            }
        }
        finally
        {
            if (disableConstraints)
                await ExecAsync(conn, $"ALTER TABLE {bracketedTable} CHECK CONSTRAINT ALL");
        }
    }

    private static async Task ExecAsync(SqlConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static DataTable BuildDataTable(List<ColumnMapping> active, List<Dictionary<string, string>> rows)
    {
        var dt = new DataTable();
        foreach (var m in active) dt.Columns.Add(m.DbColumn);
        foreach (var row in rows)
        {
            var dr = dt.NewRow();
            foreach (var m in active)
            {
                if (m.CsvColumn != "(ignorieren)" && row.TryGetValue(m.CsvColumn, out var v) && !string.IsNullOrEmpty(v))
                    dr[m.DbColumn] = v;                          // CSV-Wert hat Vorrang
                else if (!string.IsNullOrEmpty(m.FixedValue))
                    dr[m.DbColumn] = m.FixedValue;               // Fester Wert als Fallback
                else
                    dr[m.DbColumn] = DBNull.Value;
            }
            dt.Rows.Add(dr);
        }
        return dt;
    }
}
