using NlpAnalytics.Models;

namespace NlpAnalytics.Services;

public interface IQueryGeneratorService
{
    Task<(string Sql, string Interpretation)> GenerateSqlAsync(string naturalLanguageQuery, SchemaInfo schema);
    Task<string> GenerateInsightsAsync(string naturalLanguageQuery, List<string> columns, List<List<object?>> rows);

    /// <summary>
    /// Asks the AI to repair a SQL query that failed validation.
    /// Provides the original SQL, the database error, and the real column names
    /// discovered from each referenced table.
    /// </summary>
    Task<string> RepairSqlAsync(
        string brokenSql,
        string sqlError,
        Dictionary<string, List<string>> actualColumnsByTable);
}
