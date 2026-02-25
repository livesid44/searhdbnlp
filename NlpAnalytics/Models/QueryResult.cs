namespace NlpAnalytics.Models;

public class QueryResult
{
    public string NaturalLanguageQuery { get; set; } = string.Empty;
    public string GeneratedSql { get; set; } = string.Empty;
    /// <summary>Set when the first AI-generated SQL was invalid and was automatically repaired.</summary>
    public string? OriginalSql { get; set; }
    public bool WasRepaired => OriginalSql != null;
    public string Interpretation { get; set; } = string.Empty;
    public List<string> Columns { get; set; } = new();
    /// <summary>Runtime .NET types for each column, parallel to Columns.</summary>
    public List<Type> ColumnTypes { get; set; } = new();
    public List<List<object?>> Rows { get; set; } = new();
    public ChartData? ChartData { get; set; }
    public string? AiInsights { get; set; }
    public bool IsPivotable { get; set; }
    public string? ErrorMessage { get; set; }
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public int RowCount => Rows.Count;
    public bool HasData => Columns.Count > 0 && Rows.Count > 0;
}
