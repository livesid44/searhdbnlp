using Microsoft.Data.SqlClient;
using NlpAnalytics.Models;

namespace NlpAnalytics.Services;

public class SqlServerSchemaService : ISchemaService
{
    private readonly string _connectionString;
    private readonly ILogger<SqlServerSchemaService> _logger;

    public SqlServerSchemaService(IConfiguration configuration, ILogger<SqlServerSchemaService> logger)
    {
        _connectionString = configuration.GetConnectionString("SqlServer")
            ?? throw new InvalidOperationException("ConnectionStrings:SqlServer is not configured.");
        _logger = logger;
    }

    public async Task<SchemaInfo> GetSchemaAsync()
    {
        var schema = new SchemaInfo();

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        schema.DatabaseName = conn.Database;

        const string tablesSql = @"
            SELECT t.TABLE_SCHEMA, t.TABLE_NAME
            FROM INFORMATION_SCHEMA.TABLES t
            WHERE t.TABLE_TYPE = 'BASE TABLE'
            ORDER BY t.TABLE_SCHEMA, t.TABLE_NAME";

        const string columnsSql = @"
            SELECT
                c.TABLE_SCHEMA,
                c.TABLE_NAME,
                c.COLUMN_NAME,
                c.DATA_TYPE,
                c.IS_NULLABLE,
                CASE WHEN kcu.COLUMN_NAME IS NOT NULL THEN 1 ELSE 0 END AS IS_PRIMARY_KEY
            FROM INFORMATION_SCHEMA.COLUMNS c
            LEFT JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                ON tc.TABLE_SCHEMA = c.TABLE_SCHEMA
                AND tc.TABLE_NAME = c.TABLE_NAME
                AND tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
            LEFT JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
                ON kcu.CONSTRAINT_NAME = tc.CONSTRAINT_NAME
                AND kcu.TABLE_SCHEMA = c.TABLE_SCHEMA
                AND kcu.TABLE_NAME = c.TABLE_NAME
                AND kcu.COLUMN_NAME = c.COLUMN_NAME
            ORDER BY c.TABLE_SCHEMA, c.TABLE_NAME, c.ORDINAL_POSITION";

        var tableDict = new Dictionary<string, TableInfo>();

        await using (var cmd = new SqlCommand(tablesSql, conn))
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var tblSchema = reader.GetString(0);
                var tblName = reader.GetString(1);
                var key = $"{tblSchema}.{tblName}";
                tableDict[key] = new TableInfo { Schema = tblSchema, Name = tblName };
            }
        }

        await using (var cmd = new SqlCommand(columnsSql, conn))
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var tblSchema = reader.GetString(0);
                var tblName = reader.GetString(1);
                var key = $"{tblSchema}.{tblName}";

                if (!tableDict.TryGetValue(key, out var table)) continue;

                table.Columns.Add(new ColumnInfo
                {
                    Name = reader.GetString(2),
                    DataType = reader.GetString(3),
                    IsNullable = reader.GetString(4).Equals("YES", StringComparison.OrdinalIgnoreCase),
                    IsPrimaryKey = reader.GetInt32(5) == 1
                });
            }
        }

        schema.Tables = tableDict.Values.ToList();
        _logger.LogInformation("Schema loaded: {Count} tables from {Database}", schema.Tables.Count, schema.DatabaseName);

        // Populate sample rows so the AI knows actual data values (e.g. "Met"/"Not Met" for a Status column).
        // Limit to first 30 tables to avoid excessive startup time on very large databases.
        foreach (var table in schema.Tables.Take(30))
        {
            try
            {
                var sampleSql = $"SELECT TOP 5 * FROM {table.FullName}";
                await using var cmd = new SqlCommand(sampleSql, conn) { CommandTimeout = 10 };
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var row = new string[reader.FieldCount];
                    for (var i = 0; i < reader.FieldCount; i++)
                        row[i] = reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i)) ?? "NULL";
                    table.SampleRows.Add(row);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not sample table {Table}: {Message}", table.FullName, ex.Message);
            }
        }

        return schema;
    }
}
