namespace LinkPocket.Models;

public class SearchHistoryItem
{
    public int Id { get; set; }
    public string Query { get; set; } = string.Empty;
    public int ResultsCount { get; set; }
    public DateTime SearchedAt { get; set; }
}
