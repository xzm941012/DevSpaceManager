# DevSpaceManager Agents Guide

This repository contains a Windows-only tray manager for DevSpace and Cloudflare Tunnel.

## Scope

The app is intentionally small and practical:

- WinForms UI
- `.NET 8`
- Windows Task Scheduler integration
- Git Bash launch path for DevSpace
- `cloudflared` orchestration

Avoid turning it into a large framework or background platform.

## Priorities

When changing this project, optimize for:

1. Reliability on real Windows machines
2. Small distribution size
3. Clear Chinese UI for non-technical users
4. Safe multi-machine Cloudflare tunnel behavior
5. Predictable startup even after `nvm` changes

## Product Rules

- Windows only
- Chinese UI text unless there is a strong reason otherwise
- Prefer simple user-facing settings over exposing raw paths
- Keep first-run and recovery flows obvious
- Do not silently reuse a Cloudflare tunnel in a way that can cause multi-PC routing conflicts

## Technical Rules

- Prefer framework-dependent publish output over self-contained output
- Do not commit build output such as `bin/`, `obj/`, or `publish-fd/`
- Keep dependencies minimal
- Avoid adding third-party UI frameworks unless truly necessary
- Preserve the current `Git Bash -> devspace serve` launch model on Windows

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
  -r win-x64 `
  --self-contained false `
  -o .\tools\DevSpaceManager\publish-fd
```

Target machines are expected to install `.NET 8 Desktop Runtime` separately.

## Cloudflare Safety

Each machine should use a unique tunnel name.

Examples:

- `devspace-home`
- `devspace-company`
- `devspace-laptop`

Avoid defaulting all machines to a shared tunnel like `devspace`.
