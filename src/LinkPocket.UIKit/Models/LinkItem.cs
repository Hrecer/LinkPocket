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

        /// <summary>
        /// 两个结果集是否**渲染等价**：ID 序列 + 全部展示字段逐项一致（含顺序）。
        /// 用途：VM 在静默刷新后判断"要不要通知视图重建表格行"——工厂模式整表重建（N 行 × 单元格）是
        /// 同步主线程重活，内容未变时重建纯属白烧（低性能设备切到搜索页会偶发卡顿）。
        /// 只做等价判定、不做任何业务合并：字段有任何差异即视为"需要重建"。
        /// </summary>
        public static bool SameSequence(IReadOnlyList<LinkItem>? a, IReadOnlyList<LinkItem>? b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            if (a.Count != b.Count) return false;
            for (var i = 0; i < a.Count; i++)
            {
                var x = a[i];
                var y = b[i];
                if (x.LinkId != y.LinkId || x.Url != y.Url || x.Title != y.Title
                    || x.Description != y.Description || x.FaviconUrl != y.FaviconUrl
                    || x.ListId != y.ListId || x.VisitCount != y.VisitCount
                    || x.IsImportant != y.IsImportant || x.LastVisitedAt != y.LastVisitedAt
                    || x.CreatedAt != y.CreatedAt || x.UpdatedAt != y.UpdatedAt) return false;
            }
            return true;
        }

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