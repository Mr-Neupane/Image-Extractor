using Microsoft.Data.SqlClient;

namespace ImageExtractor.Services;

public class DiscoveredTable
{
    public string Schema { get; set; } = "";
    public string Name { get; set; } = "";
    public int BinaryColumnCount { get; set; }
    public string FullName => $"[{Schema}].[{Name}]";
}

public class ColumnInfo
{
    public string Name { get; set; } = "";
    public string DataType { get; set; } = "";
    public int? MaxLength { get; set; }
    public bool IsNullable { get; set; }
}

public class SchemaDiscovery
{
    public static async Task<List<string>> GetDatabasesAsync(string connectionString)
    {
        var databases = new List<string>();
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        var sql = @"
            SELECT name FROM sys.databases
            WHERE name NOT IN ('master', 'tempdb', 'model', 'msdb')
              AND state = 0
            ORDER BY name";

        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            databases.Add(reader.GetString(0));

        return databases;
    }

    public static async Task<List<DiscoveredTable>> GetTablesWithBinaryColumnsAsync(string connectionString)
    {
        var tables = new List<DiscoveredTable>();
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        var sql = @"
            SELECT
                TABLE_SCHEMA,
                TABLE_NAME,
                COUNT(*) AS BinaryColumnCount
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE DATA_TYPE IN ('image', 'varbinary')
            GROUP BY TABLE_SCHEMA, TABLE_NAME
            ORDER BY TABLE_SCHEMA, TABLE_NAME";

        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add(new DiscoveredTable
            {
                Schema = reader.GetString(0),
                Name = reader.GetString(1),
                BinaryColumnCount = reader.GetInt32(2)
            });
        }
        return tables;
    }

    public static async Task<List<ColumnInfo>> GetTableColumnsAsync(
        string connectionString, string schema, string table)
    {
        var columns = new List<ColumnInfo>();
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        var sql = @"
            SELECT
                COLUMN_NAME,
                DATA_TYPE,
                CHARACTER_MAXIMUM_LENGTH,
                IS_NULLABLE
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table
            ORDER BY ORDINAL_POSITION";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@table", table);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(new ColumnInfo
            {
                Name = reader.GetString(0),
                DataType = reader.GetString(1),
                MaxLength = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                IsNullable = reader.GetString(3) == "YES"
            });
        }
        return columns;
    }
}
