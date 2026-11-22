# BaBaBooeyStrap

A modern, high-performance Roblox bootstrapper for Windows (`win-x64`) built with **.NET 8 & WPF-UI (Fluent Design)**, combining **Bloxstrap / Fishstrap** customization capabilities with a **preemptive multi-instance & multi-account engine**.

---

## ✨ Key Features

### 🔐 Multi-Account & Preemptive Multi-Instance Engine
- **Preemptive Mutex Acquisition**: Holds `ROBLOX_singletonMutex` and `ROBLOX_singletonEvent` on a dedicated STA thread so multiple Roblox clients can run simultaneously without closing existing sessions.
- **DPAPI Encrypted Account Store**: Securely stores `.ROBLOSECURITY` cookies using Windows Data Protection API (`CurrentUser` scope)—never saved in plain text.
- **Built-in WebView2 Login & Cookie Verification**: Add accounts via an isolated WebView2 browser session or paste cookies directly, with batch cookie health verification.
- **Smart Launch Targets**: Launch accounts directly into a `PlaceId`, game URL, VIP private server (`privateServerLinkCode`), server share link (`/share?code=...&type=Server`), specific `JobId`, or **Follow User** (`@username` / `user:id`).
- **Per-Account Settings & Batch AFK Preset**: Override framerate cap, graphics quality, master volume, or fullscreen mode per account.

### 🚀 Fast Deployment & Bootstrapper Pipeline
- **Automatic Fastest CDN Mirror Probing**: Races Roblox CDN mirrors and caches the fastest endpoint in memory.
- **Parallel Package Download & Verification**: Concurrent downloading with MD5 verification, automatic retry, disk space pre-checks, and parallel directory-grouped extraction.
- **Self-Healing Installation**: Automatically recovers missing `WebView2Loader.dll`, `AppSettings.xml`, or corrupted files from cached package archives, and keeps `%LOCALAPPDATA%\BaBaBooeyStrap` and `roblox-player://` protocol handlers synchronized.
- **11 Bootstrapper Dialog Styles**: Choose between Fluent, Fluent Aero, Classic Fluent, Byfron, Terminal, 2025, Legacy Progress, or custom XML themes.

### 🛠️ FastFlags, Mods & Global Settings
- **FastFlag Presets & Full JSON Editor**: Built-in presets for Rendering Mode (D3D11, Vulkan, OpenGL), MSAA, Lighting Technology, Texture Quality, DPI Scaling, and Font Size, plus a full search/filter/import/export `ClientAppSettings.json` editor.
- **Mod Overlay Engine**: Non-destructively applies custom death sounds (`oof`), cursor presets (2006 / 2013), old avatar background, custom fonts, and custom file overlays under `Modifications\`, restoring original files from package caches when mods are removed.
- **Global Basic Settings Manager**: Edit `GlobalBasicSettings_13.xml` directly (FPS Cap, Saved Quality Level, Mouse Sensitivity, VR, Reduced Motion, Read-Only Lock).

### 🌐 Activity Watcher, Server Location & Integrations
- **Real-Time Log Activity Watcher**: Tracks game joins, in-game teleports, UDMUX server IPs/ports, and `BloxstrapRPC` messages across multiple concurrent Roblox processes.
- **Server Geolocation & Tray Toast**: Resolves server city, country, and region via RoValra / IPInfo, showing country flag notifications and a live active-servers submenu in the system tray.
- **Better Matchmaking**: Optional lowest-ping server selection before joining public games.
- **Discord Rich Presence**: Displays current game, server location, account username, and a one-click join button.
- **Window Manipulation & Custom Integrations**: Custom window titles, icons, borderless fullscreen (`WinEventHook`), and pre/post-launch companion apps with auto-close binding.

---

## 📦 Installation & Usage

1. Download the latest `BaBaBooeyStrap.exe` from [**Releases**](https://github.com/MeowHappyHappyHappy/BaBaBooeyStrap/releases/latest).
2. Run `BaBaBooeyStrap.exe`. On first launch, complete the quick setup dialog to register `roblox-player://` / `roblox://` browser protocols and optional shortcuts.
3. Launch games directly from your browser on `roblox.com`, from the system tray **Quick Launch** menu, or through the **Accounts** tab for multi-account sessions.

### Command-Line Arguments

| Flag | Description |
| :--- | :--- |
| `-menu` / `-settings` | Open the main BaBaBooeyStrap settings window |
| `-player "<uri>"` | Launch Roblox Player with a protocol URI |
| `-studio "<path_or_uri>"` | Launch Roblox Studio or open a local `.rbxl` / `.rbxlx` file |
| `-account "<username_or_guid>" -target "<place_or_url>"` | Headless/CLI launch for a specific saved account and target (optional `-jobid "<id>"`, `-detach`) |
| `-hold` | Enable multi-instance mutex holding immediately |
| `-nohold` | Temporarily disable multi-instance mutex holding for this launch |
| `-install` / `-uninstall` | Install or uninstall BaBaBooeyStrap and protocol registrations |
| `-quiet` | Suppress UI dialogs during launch/install |

---

## 🔧 Building from Source

### Prerequisites
- Windows 10 / 11 (x64)
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### Build, Test & Publish

```powershell
# Build the solution
dotnet build BaBaBooeyStrap.sln

# Run the full test suite
dotnet test tests/BaBaBooeyStrap.Tests/BaBaBooeyStrap.Tests.csproj

# Publish standalone single-file win-x64 executable to ./publish/
dotnet publish src/BaBaBooeyStrap.App/BaBaBooeyStrap.App.csproj -p:PublishProfile=win-x64
```
