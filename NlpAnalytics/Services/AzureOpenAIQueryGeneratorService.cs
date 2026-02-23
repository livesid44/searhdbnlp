using Azure;
using Azure.AI.OpenAI;
using OpenAI.Chat;
using NlpAnalytics.Models;

namespace NlpAnalytics.Services;

public class AzureOpenAIQueryGeneratorService : IQueryGeneratorService
{
    private readonly ChatClient _chatClient;
    private readonly ILogger<AzureOpenAIQueryGeneratorService> _logger;

    public AzureOpenAIQueryGeneratorService(IConfiguration configuration, ILogger<AzureOpenAIQueryGeneratorService> logger)
    {
        var endpoint = configuration["AzureOpenAI:Endpoint"]
            ?? throw new InvalidOperationException("AzureOpenAI:Endpoint is not configured.");
        var apiKey = configuration["AzureOpenAI:ApiKey"]
            ?? throw new InvalidOperationException("AzureOpenAI:ApiKey is not configured.");
        var deploymentName = configuration["AzureOpenAI:DeploymentName"] ?? "gpt-4o";

        var client = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
        _chatClient = client.GetChatClient(deploymentName);
        _logger = logger;
    }

    public async Task<(string Sql, string Interpretation)> GenerateSqlAsync(string naturalLanguageQuery, SchemaInfo schema)
    {
        var schemaDescription = BuildSchemaDescription(schema);

        var systemPrompt = "You are an expert SQL Server data analyst assistant.\n" +
            "The user will ask questions in natural language about a MSSQL database.\n" +
            "Your job is to:\n" +
            "1. Generate a valid T-SQL SELECT query that answers the question.\n" +
            "2. Provide a brief, friendly interpretation of what the data means.\n\n" +
            $"Database: {schema.DatabaseName}\n\n" +
            "Schema:\n" +
            schemaDescription + "\n" +
            "Rules:\n" +
            "- Return ONLY a JSON object with exactly two keys: \"sql\" and \"interpretation\".\n" +
            "- The \"sql\" value must be a valid T-SQL SELECT statement (no trailing semicolon).\n" +
            "- Do NOT include markdown code fences, comments, or any text outside the JSON.\n" +
            "- Only generate SELECT or WITH...SELECT statements; never INSERT, UPDATE, DELETE, DROP, or EXEC.\n" +
            "- Use table and column names exactly as defined in the schema.\n" +
            "- If the question cannot be answered with the available schema, set \"sql\" to \"\" and explain in \"interpretation\".\n\n" +
            "Example response:\n" +
            "{\"sql\": \"SELECT TOP 10 CustomerName, TotalOrders FROM Customers ORDER BY TotalOrders DESC\", " +
            "\"interpretation\": \"These are the top 10 customers by number of orders.\"}";

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(naturalLanguageQuery)
        };

        _logger.LogInformation("Calling Azure OpenAI for query: {Query}", naturalLanguageQuery);

        var response = await _chatClient.CompleteChatAsync(messages);
        var content = response.Value.Content[0].Text ?? string.Empty;

        _logger.LogInformation("Azure OpenAI response: {Response}", content);

        return ParseResponse(content);
    }

    private static string BuildSchemaDescription(SchemaInfo schema)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var table in schema.Tables)
        {
            sb.AppendLine($"Table: {table.FullName}");
            foreach (var col in table.Columns)
            {
                var pk = col.IsPrimaryKey ? " [PK]" : "";
                var nullable = col.IsNullable ? " NULL" : " NOT NULL";
                sb.AppendLine($"  - {col.Name} ({col.DataType}{nullable}{pk})");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static (string Sql, string Interpretation) ParseResponse(string content)
    {
        try
        {
            // Strip markdown code fences if model returns them anyway
            var json = content.Trim();
            if (json.StartsWith("```"))
            {
                var end = json.LastIndexOf("```");
                var start = json.IndexOf('\n') + 1;
                json = json[start..end].Trim();
            }

            var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            var sql = root.TryGetProperty("sql", out var sqlProp) ? sqlProp.GetString() ?? "" : "";
            var interp = root.TryGetProperty("interpretation", out var interpProp) ? interpProp.GetString() ?? "" : "";
            return (sql, interp);
        }
        catch
        {
            // Fallback: return the raw content as interpretation
            return ("", $"Could not parse model response: {content}");
        }
    }
}
