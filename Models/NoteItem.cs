using System;

namespace LinkPocket.Models
{
    public class NoteItem
    {
        public int Id { get; set; }
        public int LinkId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        
        // 时间戳
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        
        // 关联数据
        public LinkItem? Link { get; set; }
        
        // UI辅助属性
        public string DisplayTitle => !string.IsNullOrEmpty(Title) ? Title : "无标题笔记";
        public string ContentPreview => Content.Length > 100 ? Content.Substring(0, 97) + "..." : Content;
        public string CreatedTimeText => CreatedAt.ToString("yyyy-MM-dd HH:mm");
    }
}
