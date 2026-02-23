namespace NlpAnalytics.Models;

public class QueryResult
{
    public string NaturalLanguageQuery { get; set; } = string.Empty;
    public string GeneratedSql { get; set; } = string.Empty;
    public string Interpretation { get; set; } = string.Empty;
    public List<string> Columns { get; set; } = new();
    public List<List<object?>> Rows { get; set; } = new();
    public ChartData? ChartData { get; set; }
    public string? ErrorMessage { get; set; }
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public int RowCount => Rows.Count;
    public bool HasData => Columns.Count > 0 && Rows.Count > 0;
}
