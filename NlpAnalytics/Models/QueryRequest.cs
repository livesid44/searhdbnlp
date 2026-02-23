using System.ComponentModel.DataAnnotations;

namespace NlpAnalytics.Models;

public class QueryRequest
{
    [Required(ErrorMessage = "Please enter a question.")]
    [StringLength(1000, ErrorMessage = "Question must be at most 1000 characters.")]
    public string NaturalLanguageQuery { get; set; } = string.Empty;
}
