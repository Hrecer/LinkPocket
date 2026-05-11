using System;

namespace LinkPocket.Models
{
    public class TagItem
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Color { get; set; } = "#1976D2";
        public string? Description { get; set; }
        
        // 统计字段
        public int ViewCount { get; set; }
        public DateTime? LastViewedAt { get; set; }
        
        // 时间戳
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        
        // 关联数据
        public int LinksCount { get; set; }
        
        // UI辅助属性
        public string DisplayText => $"{Name} ({LinksCount})";
        public bool IsSelected { get; set; }
    }
}
