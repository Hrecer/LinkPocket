using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkPocket.Data;

[Table("search_histories")]
public class SearchHistory
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(500)]
    public string Query { get; set; } = string.Empty;

    [Column("results_count")]
    public int ResultsCount { get; set; } = 0;

    [Column("user_id")]
    public int? UserId { get; set; }

    public DateTime SearchedAt { get; set; } = DateTime.UtcNow;
}
