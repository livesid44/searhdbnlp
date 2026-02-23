using NlpAnalytics.Models;

namespace NlpAnalytics.Services;

public interface IQueryGeneratorService
{
    Task<(string Sql, string Interpretation)> GenerateSqlAsync(string naturalLanguageQuery, SchemaInfo schema);
}
