using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using LinkPocket.Contracts;

namespace LinkPocket.Models
{
    public class LinkItem : INotifyPropertyChanged
    {
        /// <summary>DTO → 行模型的**唯一映射**（搜索页 / 智能列表 / 去重明细共用，勿再各写一份）。</summary>
        public static LinkItem FromDto(LinkDto link) => new()
        {
            LinkId = link.LinkId,
            Url = link.Url,
            Title = link.Title ?? "",
            Description = link.Description ?? "",
            FaviconUrl = link.FaviconUrl ?? "",
            ListId = link.ListId,
            LastVisitedAt = link.LastVisitedAt,
            VisitCount = link.VisitCount,
            IsImportant = link.IsImportant,
            CreatedAt = link.CreatedAt,
            UpdatedAt = link.UpdatedAt
        };

        public string LinkId { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string FaviconUrl { get; set; } = string.Empty;
        public string? ListId { get; set; }

        // 状态字段
        public DateTime? LastVisitedAt { get; set; }
        public int VisitCount { get; set; }
        public bool IsImportant { get; set; }

        // 时间戳
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        // 选择状态
        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        private bool _isCut;
        public bool IsCut
        {
            get => _isCut;
            set { _isCut = value; OnPropertyChanged(); }
        }

        // UI辅助属性
        public string DisplayTitle => !string.IsNullOrEmpty(Title) ? Title : Url;
        public string DisplayUrl => Url.Length > 50 ? Url.Substring(0, 47) + "..." : Url;
        public string TitleLetter
        {
            get
            {
                var title = DisplayTitle;
                if (string.IsNullOrEmpty(title))
                    return Url.Length > 0 ? char.ToUpper(Url[0]).ToString() : "?";

                var firstChar = title[0];
                return char.ToUpper(firstChar).ToString();
            }
        }
        public string VisitCountText => VisitCount == 0 ? "未访问" : $"{VisitCount} 次访问";
        public string LastVisitedText => LastVisitedAt.HasValue ? LastVisitedAt.Value.ToString("yyyy-MM-dd HH:mm") : "从未";

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}