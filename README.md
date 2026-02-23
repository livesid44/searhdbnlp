# searhdbnlp – NLP Analytics Platform

A .NET web application that lets users ask **natural-language questions** about data stored in **Microsoft SQL Server**. Azure OpenAI translates the question into a T-SQL query, executes it, and displays results in an interactive **table** and **chart**.

## Architecture

```
┌──────────────┐   NL question   ┌──────────────────────┐   T-SQL   ┌─────────────┐
│  Browser UI  │ ──────────────► │  ASP.NET Core MVC    │ ────────► │  MSSQL DB   │
│  (Razor/JS)  │ ◄────────────── │  (NlpAnalytics)      │ ◄──────── │             │
└──────────────┘   table+chart   └──────────┬───────────┘  results  └─────────────┘
                                             │
                                             │ schema + question
                                             ▼
                                   ┌──────────────────┐
                                   │  Azure OpenAI    │
                                   │  (GPT-4o)        │
                                   └──────────────────┘
```

## Features

- 🔍 **Natural Language Queries** – type any question in plain English
- 🤖 **Azure OpenAI Integration** – generates optimised T-SQL automatically
- 🗄️ **Live MSSQL Execution** – runs queries against your database in real time
- 📋 **Tabular View** – pageable, scrollable result table
- 📊 **Chart View** – auto-detected bar / pie / line chart, switchable at runtime
- 💡 **AI Interpretation** – plain-English summary of what the data means
- 🔒 **Read-Only Safety** – only `SELECT` and `WITH…SELECT` statements allowed

## Project Structure

```
NlpAnalytics/
├── Controllers/
│   ├── HomeController.cs          # Redirects root to Query page
│   └── QueryController.cs         # Handles NLP → SQL → execute flow
├── Models/
│   ├── ChartData.cs               # Chart.js data model
│   ├── QueryRequest.cs            # Form input model
│   ├── QueryResult.cs             # Execution result model
│   └── SchemaInfo.cs              # DB schema representation
├── Services/
│   ├── IAzureOpenAIQueryGeneratorService.cs
│   ├── AzureOpenAIQueryGeneratorService.cs   # Calls Azure OpenAI
│   ├── IDatabaseService.cs
│   ├── SqlServerDatabaseService.cs           # Executes T-SQL on MSSQL
│   ├── ISchemaService.cs
│   └── SqlServerSchemaService.cs             # Reads INFORMATION_SCHEMA
├── Views/
│   ├── Query/Index.cshtml         # NLP input page
│   └── Query/Result.cshtml        # Table + chart result page
├── appsettings.json               # Configuration placeholders
└── Program.cs                     # DI registration
```

## Prerequisites

| Requirement | Version |
|---|---|
| .NET SDK | 10.0+ |
| SQL Server | 2019+ (or Azure SQL) |
| Azure OpenAI resource | GPT-4o deployment |

## Configuration

Edit **`NlpAnalytics/appsettings.json`** (or use environment variables / Azure Key Vault in production):

```jsonc
{
  "ConnectionStrings": {
    "SqlServer": "Server=<YOUR_SERVER>;Database=<YOUR_DB>;User Id=<USER>;Password=<PWD>;TrustServerCertificate=True;"
  },
  "AzureOpenAI": {
    "Endpoint": "https://<YOUR_RESOURCE>.openai.azure.com/",
    "ApiKey": "<YOUR_API_KEY>",
    "DeploymentName": "gpt-4o"
  }
}
```

> **Security note:** Never commit real credentials. Use [.NET User Secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets) locally and Azure Key Vault or environment variables in production.

## Running Locally

```bash
cd NlpAnalytics
# Set secrets via User Secrets (recommended)
dotnet user-secrets set "ConnectionStrings:SqlServer" "<connection-string>"
dotnet user-secrets set "AzureOpenAI:ApiKey" "<api-key>"
dotnet user-secrets set "AzureOpenAI:Endpoint" "https://<resource>.openai.azure.com/"
dotnet user-secrets set "AzureOpenAI:DeploymentName" "gpt-4o"

dotnet run
```

Open `https://localhost:5001` in your browser.

## Deploying to Azure

1. Create an **Azure App Service** (Linux, .NET 10).
2. Set Application Settings for `ConnectionStrings__SqlServer`, `AzureOpenAI__Endpoint`, `AzureOpenAI__ApiKey`, `AzureOpenAI__DeploymentName`.
3. `dotnet publish -c Release -o ./publish` then deploy the `publish` folder.

## Query Examples

| Question | What it does |
|---|---|
| "Show me total sales by region this year" | Aggregates sales with GROUP BY |
| "Top 10 customers by revenue" | Ranked SELECT with TOP |
| "List products with stock below 50" | Filtered SELECT |
| "Monthly order count for 2024" | Date-based aggregation |
