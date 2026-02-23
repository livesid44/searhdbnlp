using NlpAnalytics.Models;

namespace NlpAnalytics.Services;

public interface ISchemaService
{
    Task<SchemaInfo> GetSchemaAsync();
}
