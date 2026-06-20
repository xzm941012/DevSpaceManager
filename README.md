# DevSpaceManager

Windows tray manager for [DevSpace](https://github.com/Waishnav/devspace) and its Cloudflare Tunnel.

This project is a small WinForms utility focused on one job: keep a local DevSpace instance and its public Cloudflare Tunnel easy to install, start, monitor, and recover on Windows.

It was built for a practical workflow:

- run DevSpace through Git Bash on Windows
- pin startup to a specific `nvm` Node version
- expose the local MCP server through a fixed Cloudflare hostname
- manage the whole flow from a tray app with a Chinese settings UI

## What It Does

- Starts, stops, and restarts DevSpace and `cloudflared`
- Launches DevSpace through Git Bash to match upstream shell expectations
- Stores fixed Node/npm/devspace paths so later `nvm use` changes do not break startup
- Checks local and public `/healthz`
- Supports tray-at-logon startup
- Supports a background worker mode for Windows Task Scheduler
- Provides environment checks for:
  - `.NET Desktop Runtime 8`
  - `nvm for Windows`
  - Node runtime
  - Git Bash
  - DevSpace
  - `cloudflared`
  - Cloudflare login state
  - Cloudflare tunnel config integrity
- Provides one-click Cloudflare tunnel setup
- Detects risky multi-machine tunnel-name reuse
- Checks DevSpace updates from npm and supports user-confirmed update + restart
- Includes raw config editors for:
  - `%USERPROFILE%\.devspace\config.json`
  - `%USERPROFILE%\.cloudflared\config.yml`

## App Modes

- `DevSpaceManager.exe`
  Opens the tray app and settings UI.

- `DevSpaceManager.exe --tray`
  Same tray mode, useful for startup tasks.

- `DevSpaceManager.exe --worker`
  Runs the background monitor loop without the tray UI. Intended for Task Scheduler.

## Requirements

- Windows 10 or 11
- `.NET 8 Desktop Runtime` on the target machine
- `nvm for Windows`
- Git for Windows
- Cloudflare account with a managed domain

The manager can help check or install most of these from the initialization screen.

## First-Time Setup

1. Install `.NET 8 Desktop Runtime`
2. Install `nvm for Windows`
3. Install Git for Windows
4. Install the desired Node version
5. Install `@waishnav/devspace`
6. Run `devspace init`
7. Sign in with `cloudflared tunnel login`
8. In the manager, choose:
   - a public hostname
   - a unique tunnel name for this machine
   - the local DevSpace port
9. Click `一键配置 Cloudflare`
10. Click `重启全部`

## Important Multi-PC Note

If you run DevSpace on more than one computer, **do not reuse the same Cloudflare tunnel name**.

Good examples:

- `devspace-home`
- `devspace-company`
- `devspace-laptop`

Bad example:

- `devspace` on every machine

Reusing one tunnel name on multiple machines can make Cloudflare send requests to the wrong computer, which shows up as random `404`, `502`, or unstable public health checks.

## Build

This project is intended to ship as a **framework-dependent** Windows app.

Debug / local build:

```powershell
dotnet build .\tools\DevSpaceManager\DevSpaceManager.csproj -c Release
```

Recommended publish:

```powershell
dotnet publish .\tools\DevSpaceManager\DevSpaceManager.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false `
  -o .\tools\DevSpaceManager\publish-fd
```

Output:

```text
tools\DevSpaceManager\publish-fd
```

This keeps the app package small. Target machines only need `.NET 8 Desktop Runtime` installed once.

## Project Structure

```text
Core/       App configuration, paths, roles, host wiring
Services/   Process control, health checks, updates, scheduler, auth, environment checks
UI/         Tray app, settings window, initialization window
assets/     App icon and image assets
scripts/    Small helper scripts for icon generation
```

## Local Data

Manager settings:

```text
%USERPROFILE%\.devspace-manager\config.json
```

Logs:

```text
%USERPROFILE%\.devspace-manager\logs
```

DevSpace config:

```text
%USERPROFILE%\.devspace\config.json
```

Cloudflare config:

```text
%USERPROFILE%\.cloudflared\config.yml
```

## Notes

- The app currently targets Windows only.
- The UI is intentionally simplified for normal use and hides most path-heavy configuration.
- The worker mode uses Task Scheduler rather than a Windows service so it can keep using the current user's existing Node, DevSpace, and Cloudflare credentials.
