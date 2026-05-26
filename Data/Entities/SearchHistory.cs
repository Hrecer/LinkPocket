using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkPocket.Data;

[Table("search_histories")]
public class SearchHistory
{
    [Key]
    [Required]
    [MaxLength(20)]
    [Column("search_id")]
    public string Id { get; set; } = GenerateSearchId();

    [Required]
    [MaxLength(500)]
    public string Query { get; set; } = string.Empty;

    [Column("results_count")]
    public int ResultsCount { get; set; } = 0;

    public DateTime SearchedAt { get; set; } = DateTime.UtcNow;

    private static string GenerateSearchId()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var rng = Random.Shared;
        return new string(Enumerable.Range(0, 16).Select(_ => chars[rng.Next(chars.Length)]).ToArray());
    }
}
