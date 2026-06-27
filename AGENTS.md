# DevSpaceManager Agents Guide

This project is evolving from a Windows-only DevSpace/Cloudflare Tunnel tray
manager into a Windows-first ChatGPT desktop client that embeds ChatGPT through
WebView2 and folds DevSpace management into a modern settings UI.

Before working on product, browser, shell, settings, or frontend changes, read
the shared Chinese project context:

- [桌面客户端背景与目标](../../.trellis/项目说明/桌面客户端背景与目标.md)
- [桌面客户端架构设计](../../.trellis/项目说明/桌面客户端架构设计.md)
- [桌面客户端前端规范](../../.trellis/项目说明/桌面客户端前端规范.md)
- [桌面客户端安全与浏览器策略](../../.trellis/项目说明/桌面客户端安全与浏览器策略.md)
- [桌面客户端本地调试说明](../../.trellis/项目说明/桌面客户端本地调试说明.md)

## Scope

The app should stay small and practical while becoming a polished desktop
client:

- `.NET 8 + WPF` shell
- `Microsoft WebView2` for ChatGPTView and AdminView
- `React + Vite + TypeScript` for Admin UI
- `Tailwind CSS + shadcn/ui` style constraints for web UI
- `Lucide` as the unified icon system
- Windows Task Scheduler integration
- Git Bash launch path for DevSpace
- `cloudflared` orchestration

Avoid turning it into a large framework, a generic browser, or a heavy
operations dashboard.

## Priorities

When changing this project, optimize for:

1. Reliability on real Windows machines
2. A clean ChatGPT-first desktop experience
3. Small distribution size
4. Clear Chinese UI for non-technical users
5. Safe multi-machine Cloudflare tunnel behavior
6. Predictable startup even after `nvm` changes

## Product Rules

- Windows only
- Chinese UI text unless there is a strong reason otherwise
- Main window defaults to ChatGPT, not a dashboard or landing page
- Settings and diagnostics belong in the modern settings window
- Prefer simple user-facing settings over exposing raw paths
- Keep first-run and recovery flows obvious
- Do not silently reuse a Cloudflare tunnel in a way that can cause multi-PC routing conflicts
- ChatGPTView must not have access to the local C# bridge
- AdminView bridge commands must remain explicitly whitelisted

## Technical Rules

- Prefer framework-dependent publish output over self-contained output
- Do not commit build output such as `bin/`, `obj/`, or `publish-fd/`
- Keep dependencies minimal
- Use React/Tailwind/shadcn-compatible patterns for Admin UI
- Use Lucide icons for shell, settings, dialogs, and injected controls
- Preserve the current `Git Bash -> devspace serve` launch model on Windows
- Production Admin UI must load built static assets, not a Vite dev server
- WebView2 proxy/profile changes may require recreating the WebView2 environment

## Editing Guidance

- Use small, modular files
- Keep service responsibilities separated
- Favor explicit process control over hidden automation
- If a workflow can fail because of missing local tools, surface the missing step clearly in the UI
- If PowerShell scripts are generated, avoid characters or quoting patterns that can break parsing on Windows PowerShell

## Release Guidance

Preferred publish command:

```powershell
dotnet publish .\tools\DevSpaceManager\DevSpaceManager.csproj `
  -c Release `
  -o .\tools\DevSpaceManager\publish-wpf
```

Target machines are expected to install `.NET 8 Desktop Runtime` separately.

## Cloudflare Safety

Each machine should use a unique tunnel name.

Examples:

- `devspace-home`
- `devspace-company`
- `devspace-laptop`

Avoid defaulting all machines to a shared tunnel like `devspace`.
