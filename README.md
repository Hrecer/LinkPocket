# LinkPocket

**LinkPocket 网站收藏管理器** — 一款基于 WPF + .NET 8 的桌面书签管理工具，支持文件夹分类、智能搜索、Netscape 格式导入导出、以及专有格式的完整备份恢复。

## ✨ 功能特性

- 📁 **文件夹分类管理** — 支持多级嵌套文件夹，自由整理书签
- 🔍 **多维度搜索** — 按标题、URL、描述、路径搜索
- 📥 **Netscape 格式导入/导出** — 兼容 Chrome / Firefox / Edge 浏览器书签
- 💾 **完整备份与恢复 (.lpbackup)** — 零转换损失，保留全部字段（描述、访问统计、重要标记、图标）
- 🗑️ **回收站机制** — 软删除保护，支持恢复或永久清除
- ⭐ **重要标记** — 快速定位常用链接
- 🔢 **ID跳转** — 通过唯一标识快速定位书签
- 🎨 **Material Design UI** — 现代化界面，流畅动画

## 🛠️ 技术栈

| 组件 | 技术 |
|------|------|
| 框架 | WPF (.NET 8) |
| 架构 | MVVM (CommunityToolkit.Mvvm) |
| 数据库 | SQLite (Entity Framework Core) |
| UI库 | Material Design Themes |
| 动画 | XamlFlair.Wpf |

## 📁 项目结构

```
LinkPocket/
├── Data/              # 数据层（实体 + DbContext）
│   └── Entities/      # Folder, Link, TrashedLink
├── Services/          # 业务逻辑服务层
│   ├── LinkService    # 书签 CRUD + 元数据抓取
│   ├── FolderService  # 文件夹树管理
│   ├── FaviconService # 图标三级缓存
│   ├── BookmarkExporter / BookmarkImporter  # Netscape HTML 导入导出
│   └── LinkPocketBackupService             # .lpbackup 备份导入导出
├── ViewModels/        # MVVM 视图模型层
├── Views/             # 视图层（XAML）
├── Managers/          # 选择状态 / 剪贴板 / 导航
├── Models/            # 数据模型
└── MainWindow         # 主窗口
```

## 🚀 运行项目

### 前置要求

- .NET 8.0 SDK
- Windows 10+ (WPF 应用)

### 构建与运行

```bash
# 克隆仓库
git clone https://github.com/Hrecer/LinkPocket.git
cd LinkPocket

# 还原依赖并运行
dotnet run
```

首次启动会自动创建 SQLite 数据库 (`linkpocket.db`) 和图标缓存目录 (`favicons/`)。

## 📦 备份格式说明

### .lpbackup (LinkPocket 专用备份)

ZIP 压缩包结构：

```
📦 backup_YYYYMMDD_HHmmss.lpbackup
├── manifest.json     # 元数据（版本、时间、统计）
├── data.json         # 完整数据（JSON序列化，100%字段保留）
└── favicons/         # 图标文件（原始格式，无损）
```

**优势：**
- 零转换损失，所有14个业务字段完整保留
- 图标以原始文件形式打包，不使用 Base64
- 导入时系统重新生成 ID，支持多次重复导入
- 不包含回收站数据

### Netscape Bookmark HTML

标准浏览器书签格式，用于跨平台迁移。部分扩展字段（Description、VisitCount 等）无法在此格式中保留。

## 📄 许可证

本项目采用 [CC0 1.0 Universal](LICENSE) 公共领域声明。你可以自由复制、修改、分发和使用本作品，无需任何条件。
