using System;
using System.Collections.Generic;

namespace LinkPocket.Models
{
    public class FolderItem
    {
        public string Id { get; set; } = string.Empty;
        public string FolderId { get; set; } = string.Empty;
        public string? ParentId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? ParentName { get; set; }  // 添加父目录名称
        public string? Description { get; set; }

        // 统计字段
        public int LinkCount { get; set; }
        public DateTime? LastVisitedAt { get; set; }
        
        // 时间戳
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        
        // 关联数据（树形结构）
        public FolderItem? Parent { get; set; }
        public List<FolderItem> Children { get; set; } = new();
        public List<LinkItem> Links { get; set; } = new();
        
        // UI辅助属性
        public string DisplayText => $"{Name} ({LinkCount})";
        public bool IsRoot => string.IsNullOrEmpty(ParentId);
        public bool HasChildren => Children != null && Children.Count > 0;
    }
}
