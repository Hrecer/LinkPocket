namespace LinkPocket.Models;

public class SearchHistoryItem
{
    public string Id { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public int ResultsCount { get; set; }
    public DateTime SearchedAt { get; set; }
}
