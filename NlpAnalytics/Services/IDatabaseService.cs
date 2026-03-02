using NlpAnalytics.Models;

namespace NlpAnalytics.Services;

public interface IDatabaseService
{
    Task<QueryResult> ExecuteQueryAsync(string sql, string naturalLanguageQuery);

    /// <summary>
    /// Validates the SQL without returning data (SELECT TOP 0).
    /// Returns (true, null, empty) when valid.
    /// Returns (false, errorMessage, actualColumnsByTable) when the query references
    /// invalid column or table names — the dictionary maps each referenced table
    /// to its real column names (discovered via SELECT TOP 1 *).
    /// </summary>
    Task<(bool IsValid, string? SqlError, Dictionary<string, List<string>> ActualColumnsByTable)>
        ValidateQueryAsync(string sql);
}
