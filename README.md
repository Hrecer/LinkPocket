# LinkPocket

**LinkPocket Bookmark Manager** — A desktop bookmark management tool built with WPF + .NET 8, featuring folder-based organization, smart search, Netscape format import/export, and full-featured backup/restore.

## ✨ Features

- 📁 **Folder Organization** — Multi-level nested folders for flexible bookmark categorization
- 🔍 **Multi-dimensional Search** — Search by title, URL, description, or folder path
- 📥 **Import/Export** — Import and export bookmarks in multiple formats
- 💾 **Full Backup & Restore (.lpbackup)** — Zero data loss, preserves all fields (descriptions, visit counts, favorites, favicons)
- 🗑️ **Recycle Bin** — Soft-delete protection with restore and permanent delete options
- ⭐ **Favorites / Important Marks** — Quick access to frequently used links
- 🔢 **ID Jump** — Locate bookmarks instantly by unique identifier
- 🎨 **Material Design UI** — Modern interface with smooth animations

## 🛠️ Tech Stack

| Component | Technology |
|-----------|------------|
| Framework | WPF (.NET 8) |
| Architecture | MVVM (CommunityToolkit.Mvvm) |
| Database | SQLite (Entity Framework Core) |
| UI Library | Material Design Themes |
| Animation | XamlFlair.Wpf |

## 📁 Project Structure

```
LinkPocket/
├── Data/              # Data layer (entities + DbContext)
│   └── Entities/      # Folder, Link, TrashedLink
├── Services/          # Business logic services
│   ├── LinkService    # Bookmark CRUD + metadata fetching
│   ├── FolderService  # Folder tree management
│   ├── FaviconService # Three-level favicon cache
│   ├── BookmarkExporter / BookmarkImporter  # Netscape HTML import/export
│   └── LinkPocketBackupService             # .lpbackup backup import/export
├── ViewModels/        # MVVM view models
├── Views/             # View layer (XAML)
├── Managers/          # Selection state / clipboard / navigation
├── Models/            # Data models
└── MainWindow         # Main window
```

## 🚀 Getting Started

### Prerequisites

- .NET 8.0 SDK
- Windows 10+ (WPF application)

### Build & Run

```bash
# Clone the repository
git clone https://github.com/Hrecer/LinkPocket.git
cd LinkPocket

# Restore dependencies and run
dotnet run
```

On first launch, the SQLite database (`linkpocket.db`) and favicon cache directory (`favicons/`) are created automatically.

## 📦 Backup Format

### .lpbackup (LinkPocket Native Backup)

ZIP archive structure:

```
📦 linkpocket_backup_YYYYMMDD_HHmmss.lpbackup
├── manifest.json     # Metadata (version, timestamp, statistics)
├── data.json         # Complete data (JSON serialized, 100% field preservation)
└── favicons/         # Favicon files (original format, lossless)
```

**Advantages:**
- Zero conversion loss — all 14 business fields fully preserved
- Favicons packed as original files, no Base64 encoding
- System regenerates IDs on import; supports repeated imports without conflict
- Excludes recycle bin data

## 📄 License

This project is licensed under [CC0 1.0 Universal](LICENSE). You may copy, modify, distribute, and use this work freely for any purpose without conditions.
