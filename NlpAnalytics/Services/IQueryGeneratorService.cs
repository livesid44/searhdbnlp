using NlpAnalytics.Models;

namespace NlpAnalytics.Services;

public interface IQueryGeneratorService
{
    Task<(string Sql, string Interpretation)> GenerateSqlAsync(string naturalLanguageQuery, SchemaInfo schema);
    Task<string> GenerateInsightsAsync(string naturalLanguageQuery, List<string> columns, List<List<object?>> rows);
}
