using Microsoft.AspNetCore.Mvc;
using NlpAnalytics.Models;
using NlpAnalytics.Services;

namespace NlpAnalytics.Controllers;

public class QueryController : Controller
{
    private readonly IQueryGeneratorService _queryGenerator;
    private readonly IDatabaseService _databaseService;
    private readonly ISchemaService _schemaService;
    private readonly ILogger<QueryController> _logger;

    public QueryController(
        IQueryGeneratorService queryGenerator,
        IDatabaseService databaseService,
        ISchemaService schemaService,
        ILogger<QueryController> logger)
    {
        _queryGenerator = queryGenerator;
        _databaseService = databaseService;
        _schemaService = schemaService;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult Index()
    {
        return View(new QueryRequest());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Execute(QueryRequest request)
    {
        if (!ModelState.IsValid)
            return View("Index", request);

        try
        {
            var schema = await _schemaService.GetSchemaAsync();
            var (sql, interpretation) = await _queryGenerator.GenerateSqlAsync(request.NaturalLanguageQuery, schema);

            if (string.IsNullOrWhiteSpace(sql))
            {
                var emptyResult = new QueryResult
                {
                    NaturalLanguageQuery = request.NaturalLanguageQuery,
                    GeneratedSql = sql,
                    Interpretation = interpretation,
                    ErrorMessage = string.IsNullOrWhiteSpace(interpretation)
                        ? "The AI could not generate a SQL query for this question."
                        : null
                };
                return View("Result", emptyResult);
            }

            var result = await ExecuteWithValidationAsync(
                sql, request.NaturalLanguageQuery, interpretation);

            // Generate AI insights on the returned data
            if (result.HasData && !result.HasError)
            {
                try
                {
                    result.AiInsights = await _queryGenerator.GenerateInsightsAsync(
                        request.NaturalLanguageQuery, result.Columns, result.Rows);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not generate AI insights; continuing without them.");
                }
            }

            return View("Result", result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing query: {Query}", request.NaturalLanguageQuery);
            var errorResult = new QueryResult
            {
                NaturalLanguageQuery = request.NaturalLanguageQuery,
                ErrorMessage = "An error occurred while processing your request. Please check configuration and try again."
            };
            return View("Result", errorResult);
        }
    }

    /// <summary>
    /// Validates the generated SQL before execution. If column/table names are wrong,
    /// asks the AI to repair it using the real column names discovered via SELECT TOP 1 *.
    /// </summary>
    private async Task<QueryResult> ExecuteWithValidationAsync(
        string sql, string naturalLanguageQuery, string interpretation)
    {
        var (isValid, sqlError, actualColumns) = await _databaseService.ValidateQueryAsync(sql);

        string finalSql = sql;
        string? originalSql = null;

        if (!isValid && actualColumns.Count > 0)
        {
            _logger.LogInformation(
                "SQL failed validation: {Error}. Attempting AI repair with actual columns.", sqlError);
            try
            {
                var repairedSql = await _queryGenerator.RepairSqlAsync(sql, sqlError!, actualColumns);
                if (!string.IsNullOrWhiteSpace(repairedSql)
                    && (repairedSql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                        || repairedSql.StartsWith("WITH", StringComparison.OrdinalIgnoreCase)))
                {
                    originalSql = sql;
                    finalSql = repairedSql;
                    _logger.LogInformation("Using repaired SQL: {Sql}", finalSql);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SQL repair failed; proceeding with original SQL.");
            }
        }

        var result = await _databaseService.ExecuteQueryAsync(finalSql, naturalLanguageQuery);
        result.Interpretation = interpretation;
        result.OriginalSql = originalSql;
        return result;
    }
}
