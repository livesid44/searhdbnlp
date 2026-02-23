using NlpAnalytics.Models;

namespace NlpAnalytics.Services;

public interface IDatabaseService
{
    Task<QueryResult> ExecuteQueryAsync(string sql, string naturalLanguageQuery);
}
