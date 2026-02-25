using Microsoft.Data.SqlClient;
using NlpAnalytics.Models;

namespace NlpAnalytics.Services;

public class SqlServerDatabaseService : IDatabaseService
{
    private const int MaxRows = 1000;
    private static readonly string[] ChartColors =
    [
        "#36A2EB", "#FF6384", "#FFCE56", "#4BC0C0", "#9966FF",
        "#FF9F40", "#C9CBCF", "#E7E9ED", "#71B37C", "#EC932F"
    ];

    /// <summary>All .NET numeric types that SQL Server can return.</summary>
    private static readonly HashSet<Type> NumericTypes = new()
    {
        typeof(int), typeof(long), typeof(short), typeof(byte), typeof(sbyte),
        typeof(float), typeof(double), typeof(decimal),
        typeof(uint), typeof(ulong), typeof(ushort)
    };

    private readonly string _connectionString;
    private readonly ILogger<SqlServerDatabaseService> _logger;

    public SqlServerDatabaseService(IConfiguration configuration, ILogger<SqlServerDatabaseService> logger)
    {
        _connectionString = configuration.GetConnectionString("SqlServer")
            ?? throw new InvalidOperationException("ConnectionStrings:SqlServer is not configured.");
        _logger = logger;
    }

    public async Task<QueryResult> ExecuteQueryAsync(string sql, string naturalLanguageQuery)
    {
        var result = new QueryResult
        {
            NaturalLanguageQuery = naturalLanguageQuery,
            GeneratedSql = sql
        };

        try
        {
            // Allow only SELECT statements for safety
            var trimmed = sql.Trim();
            if (!trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                && !trimmed.StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
            {
                result.ErrorMessage = "Only SELECT queries are allowed for safety reasons.";
                return result;
            }

            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            // Wrap the query to limit rows
            var limitedSql = $"SELECT TOP {MaxRows} * FROM ({sql}) AS __inner__";

            await using var cmd = new SqlCommand(limitedSql, conn)
            {
                CommandTimeout = 30
            };

            await using var reader = await cmd.ExecuteReaderAsync();

            for (var i = 0; i < reader.FieldCount; i++)
            {
                result.Columns.Add(reader.GetName(i));
                result.ColumnTypes.Add(reader.GetFieldType(i));  // capture metadata type, not runtime value type
            }

            while (await reader.ReadAsync())
            {
                var row = new List<object?>();
                for (var i = 0; i < reader.FieldCount; i++)
                    row.Add(reader.IsDBNull(i) ? null : reader.GetValue(i));
                result.Rows.Add(row);
            }

            _logger.LogInformation("Query executed: {Rows} rows returned", result.RowCount);
            result.ChartData = BuildChartData(result);
            result.IsPivotable = DetectPivotable(result);
        }
        catch (SqlException ex)
        {
            _logger.LogError(ex, "SQL execution error");
            result.ErrorMessage = $"Database error: {ex.Message}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error executing query");
            result.ErrorMessage = "An unexpected error occurred while executing the query.";
        }

        return result;
    }

    private static ChartData? BuildChartData(QueryResult result)
    {
        if (result.Columns.Count < 2 || result.Rows.Count == 0
            || result.ColumnTypes.Count != result.Columns.Count)
            return null;

        // Find the first column whose type is a label/dimension type (string, DateTime, Guid)
        var labelColIndex = 0;
        for (var i = 0; i < result.ColumnTypes.Count; i++)
        {
            var t = result.ColumnTypes[i];
            if (t == typeof(string) || t == typeof(DateTime) || t == typeof(Guid))
            {
                labelColIndex = i;
                break;
            }
        }

        // Find numeric columns (any recognised numeric type), excluding the label column
        var numericColIndices = result.ColumnTypes
            .Select((t, i) => (type: t, idx: i))
            .Where(x => x.idx != labelColIndex && NumericTypes.Contains(x.type))
            .Select(x => x.idx)
            .ToList();

        if (numericColIndices.Count == 0)
            return null;

        var labels = result.Rows
            .Select(r => r[labelColIndex]?.ToString() ?? "(null)")
            .ToList();

        // One colour array per dataset (one entry per row for pie/bar coloured bars)
        var datasets = numericColIndices.Select((colIdx, dsIdx) => new ChartDataset
        {
            Label = result.Columns[colIdx],
            Data = result.Rows.Select(r => r[colIdx]).ToList(),
            BackgroundColor = result.Rows
                .Select((_, rowIdx) => ChartColors[rowIdx % ChartColors.Length])
                .ToList(),
            BorderColor = ChartColors[dsIdx % ChartColors.Length]
        }).ToList();

        // Use 'pie' when single metric and few rows; otherwise 'bar'
        var chartType = numericColIndices.Count == 1 && result.Rows.Count <= 10 ? "pie" : "bar";

        return new ChartData
        {
            ChartType = chartType,
            Labels = labels,
            Datasets = datasets
        };
    }

    /// <summary>
    /// Detect whether the result can be meaningfully pivoted:
    /// at least 2 string columns + at least 1 numeric column, where
    /// the second string column has low cardinality (2–15 distinct values).
    /// </summary>
    private static bool DetectPivotable(QueryResult result)
    {
        if (result.ColumnTypes.Count < 3 || result.Rows.Count < 2)
            return false;

        var stringColIndices = result.ColumnTypes
            .Select((t, i) => (type: t, idx: i))
            .Where(x => x.type == typeof(string))
            .Select(x => x.idx)
            .ToList();

        var hasNumeric = result.ColumnTypes.Any(t => NumericTypes.Contains(t));

        if (stringColIndices.Count < 2 || !hasNumeric)
            return false;

        // Check cardinality of second string column
        var pivotColIdx = stringColIndices[1];
        var distinct = result.Rows
            .Select(r => r[pivotColIdx]?.ToString())
            .Distinct()
            .Count();

        return distinct is >= 2 and <= 15;
    }
}
