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
                result.Columns.Add(reader.GetName(i));

            while (await reader.ReadAsync())
            {
                var row = new List<object?>();
                for (var i = 0; i < reader.FieldCount; i++)
                    row.Add(reader.IsDBNull(i) ? null : reader.GetValue(i));
                result.Rows.Add(row);
            }

            _logger.LogInformation("Query executed: {Rows} rows returned", result.RowCount);
            result.ChartData = BuildChartData(result);
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
        if (result.Columns.Count < 2 || result.Rows.Count == 0)
            return null;

        // Find first string-like column as labels and numeric columns as datasets
        var labelColIndex = result.Columns
            .Select((_, i) => i)
            .FirstOrDefault(i => result.Rows.All(r => r[i] is string or null));

        var numericColIndices = result.Columns
            .Select((_, i) => i)
            .Where(i => i != labelColIndex && result.Rows.Any(r => r[i] is int or long or float or double or decimal))
            .ToList();

        if (numericColIndices.Count == 0)
            return null;

        var labels = result.Rows
            .Select(r => r[labelColIndex]?.ToString() ?? "(null)")
            .ToList();

        var datasets = numericColIndices.Select((colIdx, dsIdx) => new ChartDataset
        {
            Label = result.Columns[colIdx],
            Data = result.Rows.Select(r => r[colIdx]).ToList(),
            BackgroundColor = result.Rows
                .Select((_, rowIdx) => ChartColors[rowIdx % ChartColors.Length])
                .ToList(),
            BorderColor = ChartColors[dsIdx % ChartColors.Length]
        }).ToList();

        // Use 'pie' when there's a single numeric column with few rows, otherwise 'bar'
        var chartType = numericColIndices.Count == 1 && result.Rows.Count <= 10 ? "pie" : "bar";

        return new ChartData
        {
            ChartType = chartType,
            Labels = labels,
            Datasets = datasets
        };
    }
}
