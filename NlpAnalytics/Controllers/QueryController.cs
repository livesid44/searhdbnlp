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

            var result = await _databaseService.ExecuteQueryAsync(sql, request.NaturalLanguageQuery);
            result.Interpretation = interpretation;
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
}
