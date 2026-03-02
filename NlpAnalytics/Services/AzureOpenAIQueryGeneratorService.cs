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
            "- CRITICAL COLUMN RULE: Every column name you use in the SQL MUST appear VERBATIM in the " +
            "schema listing above for that table. Do NOT invent column names, do NOT combine two separate " +
            "columns into one name (e.g. if the schema has 'Month' and 'FY' as separate columns, you must " +
            "NEVER write 'FY_Year' — they are two different columns). Check the exact column names in the " +
            "schema before writing the query.\n" +
            "- Every column name and table name in the generated SQL MUST be enclosed in square brackets, e.g. [ColumnName].\n" +
            "- Use the NULLIF function in any denominator to avoid divide-by-zero errors, e.g. NULLIF([Denominator], 0).\n" +
            "- Always format percentages to two decimal places.\n" +
            "- For 'account name' or 'customer name' always use the column [Customer Name as per MIS].\n" +
            "- When filtering or searching on [KPI_Name], always use the LIKE clause (e.g. [KPI_Name] LIKE '%AHT%').\n" +
            "- When applying any condition on the [Actual] column, cast it using TRY_CAST(REPLACE([Actual], '%', '') AS float). Never use a column called ActualScore.\n" +
            "- When applying any condition on the [Target] column, cast it using TRY_CAST(REPLACE([Target], '%', '') AS float).\n" +
            "- For customer KPI or KPI trend queries, use table [tbl_ExecutiveSummaryDashboard] INNER JOIN [tbl_Project_Mapping] AS b ON a.[Mapping_Id] = b.[Mapping_Id].\n" +
            "- For operation KPI queries, use table [tbl_OperationDashboard] INNER JOIN [tbl_Project_Mapping] AS b ON a.[Mapping_Id] = b.[Mapping_Id].\n" +
            "- For finance-related queries (revenue, EBITDA, billing, projection), use table [tbl_Projection] INNER JOIN [tbl_Project_Mapping] AS b ON a.[Mapping_Id] = b.[Mapping_Id].\n" +
            "- For attrition-related queries, use table [tbl_People_Attrition_Flat] and apply GROUP BY on every non-aggregated column used in the SELECT.\n" +
            "- For 'KPI not met for 3 months in a row' or consecutive failure queries, use the column [Con_PassFail] = 'Fail'; do NOT apply any other window-function logic to determine consecutive months.\n" +
            "- If the question cannot be answered with the available schema, set \"sql\" to \"\" and explain in \"interpretation\".\n\n" +
            "Example response:\n" +
            "{\"sql\": \"SELECT TOP 10 [AccountName], [TotalOrders] FROM [Customers] ORDER BY [TotalOrders] DESC\", " +
            "\"interpretation\": \"These are the top 10 accounts by number of orders.\"}";

        // Few-shot examples — ground the model with real domain Q→SQL pairs
        var fewShotPairs = new (string Question, string SqlAnswer)[]
        {
            (
                "Show me accounts where a specific customer KPI is not met for 3 months in a row",
                "SELECT [Year], [MonthName], [Customer Name as per MIS] AS [AccountName], [KPI_Name] " +
                "FROM [tbl_ExecutiveSummaryDashboard] AS a " +
                "INNER JOIN [tbl_Project_Mapping] AS b ON a.[Mapping_Id] = b.[Mapping_Id] " +
                "WHERE [Con_PassFail] = 'Fail'"
            ),
            (
                "Show me accounts where Service Level is below 60% for any month",
                "SELECT [Customer Name as per MIS] AS [Account], [MonthName], [MonthSeq], [Target], [Actual], [KPI_Name], [KPI_Status] " +
                "FROM [dbo].[tbl_OperationDashboard] AS a " +
                "INNER JOIN [tbl_Project_Mapping] AS b ON a.[Mapping_Id] = b.[Mapping_Id] " +
                "WHERE [KPI_Name] LIKE '%Service Level%' " +
                "AND TRY_CAST(REPLACE([Actual], '%', '') AS float) < 60"
            ),
            (
                "Show me accounts where AHT for Customer Service process is more than 800 seconds",
                "SELECT [Customer Name as per MIS] AS [Account], [MonthName], [MonthSeq], [Target], [Actual], [KPI_Name], [KPI_Status] " +
                "FROM [dbo].[tbl_ExecutiveSummaryDashboard] AS a " +
                "INNER JOIN [tbl_Project_Mapping] AS b ON a.[Mapping_Id] = b.[Mapping_Id] " +
                "WHERE [KPI_Name] LIKE '%AHT%' " +
                "AND TRY_CAST(REPLACE([Actual], '%', '') AS float) > 800"
            ),
            (
                "Show me accounts where attrition is higher than 8% for the month January and year 2025-2026",
                "SELECT [Customer Name as per MIS] AS [AccountName], [Delivery_IBG_Description], [FY], [MonthName], [MonthPercentage] " +
                "FROM [dbo].[tbl_People_Attrition_Flat] AS a " +
                "WHERE TRY_CAST(REPLACE([MonthPercentage], '%', '') AS float) > 8 " +
                "AND [MonthName] = 'Jan' AND [FY] = '2025-2026'"
            )
        };

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt)
        };

        // Inject few-shot pairs as alternating user/assistant messages
        foreach (var (question, sql) in fewShotPairs)
        {
            messages.Add(new UserChatMessage(question));
            messages.Add(new AssistantChatMessage(
                $"{{\"sql\": \"{sql.Replace("\"", "\\\"")}\", " +
                "\"interpretation\": \"Query generated based on domain rules.\"}}"));
        }

        // Actual user question
        messages.Add(new UserChatMessage(naturalLanguageQuery));

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
            if (table.SampleRows.Count > 0)
            {
                sb.AppendLine("  Sample data:");
                var header = string.Join(", ", table.Columns.Select(c => c.Name));
                sb.AppendLine($"    [{header}]");
                foreach (var row in table.SampleRows)
                    sb.AppendLine($"    [{string.Join(", ", row)}]");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public async Task<string> GenerateInsightsAsync(
        string naturalLanguageQuery,
        List<string> columns,
        List<List<object?>> rows)
    {
        const int MaxInsightRows = 50;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.Join("\t", columns));
        foreach (var row in rows.Take(MaxInsightRows))
            sb.AppendLine(string.Join("\t", row.Select(v => v?.ToString() ?? "NULL")));

        var prompt = "You are a data analyst. Analyse the following query result and produce 3-5 concise, " +
            "actionable bullet-point insights. Use plain English. Start each bullet with an emoji that reflects the insight type. " +
            "Do NOT include any preamble or headers—return only the bullet points.\n" +
            "IMPORTANT: All revenue, amount, sales, cost, and financial values in the data are in US Dollars expressed in millions (M). " +
            "Always state monetary figures as '$X.XXM' in your insights.\n\n" +
            $"User question: {naturalLanguageQuery}\n\n" +
            "Data (tab-separated, first row is header):\n" +
            sb.ToString();

        _logger.LogInformation("Generating AI insights for query: {Query}", naturalLanguageQuery);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("You are an expert data analyst who produces concise, insightful data summaries."),
            new UserChatMessage(prompt)
        };

        var response = await _chatClient.CompleteChatAsync(messages);
        return response.Value.Content[0].Text ?? string.Empty;
    }

    public async Task<string> RepairSqlAsync(
        string brokenSql,
        string sqlError,
        Dictionary<string, List<string>> actualColumnsByTable)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("The following T-SQL query failed with a column or table name error.");
        sb.AppendLine("Fix ONLY the invalid column/table names. Do not change any other part of the query logic.");
        sb.AppendLine("Return ONLY the corrected T-SQL SELECT statement — no JSON, no markdown, no explanation.");
        sb.AppendLine();
        sb.AppendLine("Broken SQL:");
        sb.AppendLine(brokenSql);
        sb.AppendLine();
        sb.AppendLine($"Error: {sqlError}");
        sb.AppendLine();
        sb.AppendLine("Actual columns available in each referenced table:");
        foreach (var (table, cols) in actualColumnsByTable)
            sb.AppendLine($"  {table}: {string.Join(", ", cols)}");

        _logger.LogInformation("Requesting SQL repair for error: {Error}", sqlError);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("You are an expert T-SQL developer. Correct invalid column and table names in SQL queries."),
            new UserChatMessage(sb.ToString())
        };

        var response = await _chatClient.CompleteChatAsync(messages);
        var repaired = response.Value.Content[0].Text ?? string.Empty;

        // Strip markdown fences if present
        repaired = repaired.Trim();
        if (repaired.StartsWith("```"))
        {
            var end = repaired.LastIndexOf("```");
            var start = repaired.IndexOf('\n') + 1;
            repaired = repaired[start..end].Trim();
        }

        _logger.LogInformation("Repaired SQL: {Sql}", repaired);
        return repaired;
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
