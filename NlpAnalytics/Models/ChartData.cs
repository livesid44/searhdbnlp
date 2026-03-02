namespace NlpAnalytics.Models;

public class ChartDataset
{
    public string Label { get; set; } = string.Empty;
    public List<object?> Data { get; set; } = new();
    public List<string> BackgroundColor { get; set; } = new();
    public string BorderColor { get; set; } = "#36A2EB";
    public int BorderWidth { get; set; } = 1;
}

public class ChartData
{
    public string ChartType { get; set; } = "bar"; // bar, line, pie, doughnut
    public List<string> Labels { get; set; } = new();
    public List<ChartDataset> Datasets { get; set; } = new();
}
