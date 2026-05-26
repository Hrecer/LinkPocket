using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkPocket.Data;

[Table("links")]
public class Link
{
    [Key]
    [Required]
    [MaxLength(20)]
    [Column("link_id")]
    public string LinkId { get; set; } = GenerateLinkId();

    private static string GenerateLinkId()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var rng = Random.Shared;
        return new string(Enumerable.Range(0, 16).Select(_ => chars[rng.Next(chars.Length)]).ToArray());
    }

    [Required]
    [MaxLength(2048)]
    public string Url { get; set; } = string.Empty;

    [MaxLength(255)]
    public string? Title { get; set; }

    [MaxLength(255)]
    [Column("original_title")]
    public string? OriginalTitle { get; set; }

    [Column(TypeName = "text")]
    public string? Description { get; set; }

    [MaxLength(512)]
    [Column("favicon_url")]
    public string? FaviconUrl { get; set; }

    [Column("list_id")]
    public string? ListId { get; set; }

    [Column("last_visited_at")]
    public DateTime? LastVisitedAt { get; set; }

    [Column("visit_count")]
    public int VisitCount { get; set; } = 0;

    [Column("is_important")]
    public bool IsImportant { get; set; } = false;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // 导航属性
    [ForeignKey(nameof(ListId))]
    public virtual Folder? Folder { get; set; }
}