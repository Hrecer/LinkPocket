namespace LinkPocket.Models;

public class LinkQueryParams
{
    public string? Search { get; set; }
    public int? ListId { get; set; }
    public int? TagId { get; set; }
    public bool? IsImportant { get; set; }
    public string? DateFrom { get; set; }
    public string? DateTo { get; set; }
    public string SortBy { get; set; } = "created_at";
    public string SortOrder { get; set; } = "desc";
    public int Page { get; set; } = 1;
    public int PerPage { get; set; } = 20;
}
