using Microsoft.Data.SqlClient;
using NlpAnalytics.Models;

namespace NlpAnalytics.Services;

public class SqlServerDatabaseService : IDatabaseService
{
    private const int MaxRows = 1000;
    private static readonly string[] ChartColors =
    [
        "#E31837",  // Pulse360 brand red
        "#4a453d",  // warm dark
        "#0A0838",  // dark navy
        "#f3901d",  // accent orange
        "#5F0229",  // deep maroon
        "#F8B4A3",  // light salmon
        "#4D4D4F",  // medium grey
        "#ffc06a",  // light gold
        "#808080",  // grey
        "#C9CBCF"   // light grey
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

            // Strip any top-level ORDER BY before wrapping in the row-limit subquery.
            // SQL Server error 1033: "ORDER BY invalid in derived tables without TOP/OFFSET".
            // The ORDER BY has no effect anyway since TOP already controls the row set.
            var sqlForExecution = RemoveTopLevelOrderBy(trimmed);

            // Wrap the query to limit rows
            var limitedSql = $"SELECT TOP {MaxRows} * FROM ({sqlForExecution}) AS __inner__";

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

    public async Task<(bool IsValid, string? SqlError, Dictionary<string, List<string>> ActualColumnsByTable)>
        ValidateQueryAsync(string sql)
    {
        var empty = new Dictionary<string, List<string>>();
        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            // Strip any top-level ORDER BY before wrapping — SQL Server raises error 1033
            // ("ORDER BY is invalid in derived tables/subqueries without TOP/OFFSET") which
            // would mask the real error 207/208 (invalid column/table name) we want to catch.
            var sqlForValidation = RemoveTopLevelOrderBy(sql.Trim());

            // Zero-row fetch — parses and validates all column/table names but returns no data
            var checkSql = $"SELECT TOP 0 * FROM ({sqlForValidation}) AS __chk__";
            await using var cmd = new SqlCommand(checkSql, conn) { CommandTimeout = 15 };
            await using var reader = await cmd.ExecuteReaderAsync();
            // No rows read — we only need the schema validation
            return (true, null, empty);
        }
        catch (SqlException ex) when (IsColumnOrTableError(ex))
        {
            _logger.LogWarning("SQL validation failed ({Number}): {Message}", ex.Number, ex.Message);

            // Discover actual columns for tables mentioned in the query
            var actualColumns = await FetchActualColumnsForTablesAsync(sql);
            return (false, ex.Message, actualColumns);
        }
        catch (Exception ex)
        {
            // Other errors (syntax errors, etc.) — still report as invalid
            _logger.LogWarning("SQL validation error: {Message}", ex.Message);
            return (false, ex.Message, empty);
        }
    }

    /// <summary>
    /// SQL Server error numbers for invalid column / object names.
    /// 207 = Invalid column name, 208 = Invalid object name, 4104 = multi-part identifier
    /// </summary>
    private static bool IsColumnOrTableError(SqlException ex)
        => ex.Errors.Cast<SqlError>().Any(e => e.Number is 207 or 208 or 4104);

    /// <summary>
    /// Removes any top-level ORDER BY clause (at parenthesis depth 0) from the SQL text.
    /// SQL Server raises error 1033 when ORDER BY appears inside a bare subquery/derived
    /// table without a TOP or OFFSET clause, which would prevent the SELECT TOP 0 validation
    /// wrapper from reaching the real column-name error (207).
    /// </summary>
    private static string RemoveTopLevelOrderBy(string sql)
    {
        var lastOrderByAt = -1;
        var depth = 0;

        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            if (c == '(') { depth++; continue; }
            if (c == ')') { depth--; continue; }
            if (depth != 0) continue;
            if (i + 7 >= sql.Length) continue;

            // Word boundary before 'O'
            if (i > 0 && (char.IsLetterOrDigit(sql[i - 1]) || sql[i - 1] == '_')) continue;

            if (!string.Equals(sql.Substring(i, 5), "ORDER", StringComparison.OrdinalIgnoreCase))
                continue;

            // Skip whitespace between ORDER and BY
            var j = i + 5;
            while (j < sql.Length && char.IsWhiteSpace(sql[j])) j++;
            if (j + 2 > sql.Length) continue;
            if (!string.Equals(sql.Substring(j, 2), "BY", StringComparison.OrdinalIgnoreCase))
                continue;

            // BY must be followed by a non-identifier character (or end of string)
            var afterBy = j + 2;
            if (afterBy < sql.Length && (char.IsLetterOrDigit(sql[afterBy]) || sql[afterBy] == '_'))
                continue;

            lastOrderByAt = i;
        }

        return lastOrderByAt >= 0 ? sql[..lastOrderByAt].TrimEnd() : sql;
    }

    /// <summary>
    /// Extracts fully-qualified table names from the SQL text using a simple
    /// [schema].[table] / schema.table pattern, then runs SELECT TOP 1 * on each
    /// to obtain the real column list.
    /// </summary>
    private async Task<Dictionary<string, List<string>>> FetchActualColumnsForTablesAsync(string sql)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        // Match [schema].[table] or schema.table patterns
        var pattern = new System.Text.RegularExpressions.Regex(
            @"\[?(\w+)\]?\.\[?(\w+)\]?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var matches = pattern.Matches(sql);
        var tables = matches
            .Cast<System.Text.RegularExpressions.Match>()
            .Select(m => (schema: m.Groups[1].Value, name: m.Groups[2].Value,
                          full: $"[{m.Groups[1].Value}].[{m.Groups[2].Value}]"))
            .DistinctBy(t => t.full, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (tables.Count == 0)
            return result;

        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            foreach (var tbl in tables)
            {
                try
                {
                    var sampleSql = $"SELECT TOP 1 * FROM {tbl.full}";
                    await using var cmd = new SqlCommand(sampleSql, conn) { CommandTimeout = 10 };
                    await using var reader = await cmd.ExecuteReaderAsync(
                        System.Data.CommandBehavior.SchemaOnly);

                    var cols = new List<string>();
                    for (var i = 0; i < reader.FieldCount; i++)
                        cols.Add(reader.GetName(i));

                    result[tbl.full] = cols;
                    _logger.LogInformation("Fetched {Count} columns for {Table}", cols.Count, tbl.full);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Could not fetch columns for {Table}: {Message}", tbl.full, ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not open connection for column discovery: {Message}", ex.Message);
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
