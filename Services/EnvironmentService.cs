using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DevSpaceManager.Core;

namespace DevSpaceManager.Services;

internal sealed class EnvironmentService
{
    private readonly ManagerConfigStore _configStore;

    public EnvironmentService(ManagerConfigStore configStore)
    {
        _configStore = configStore;
    }

    public async Task<IReadOnlyList<EnvironmentCheck>> CheckAsync()
    {
        var checks = new List<EnvironmentCheck>();
        checks.AddRange(await CheckBasicEnvironmentAsync());
        checks.AddRange(await CheckInitializationAsync());
        return checks;
    }

    public async Task<IReadOnlyList<EnvironmentCheck>> CheckBasicEnvironmentAsync()
    {
        var config = _configStore.Reload();
        var nvmCommand = FindNvmCommand();
        var cloudflaredPath = ExecutableResolver.ResolveCloudflared(config.CloudflaredPath);
        var checks = new List<EnvironmentCheck>
        {
            FileCheck("Git Bash", config.GitBashPath),
            FileCheck("nvm for Windows", nvmCommand),
            FileCheck("Node 运行时", Path.Combine(config.NodeDirectory, "node.exe")),
            FileCheck("npm 命令", config.NpmCommand),
            FileCheck("DevSpace 命令", config.DevSpaceCommand),
            FileCheck("cloudflared", cloudflaredPath)
        };

        checks.Add(await VersionCheck("nvm 版本", nvmCommand, "version"));
        checks.Add(await VersionCheck("Node 版本", Path.Combine(config.NodeDirectory, "node.exe"), "--version"));
        checks.Add(await BashVersionCheck("DevSpace 版本", config.GitBashPath, config.DevSpaceCommand, "--help"));
        checks.Add(await VersionCheck("cloudflared 版本", cloudflaredPath, "--version"));
        return checks;
    }

    public async Task<IReadOnlyList<EnvironmentCheck>> CheckInitializationAsync()
    {
        var config = _configStore.Reload();
        var checks = new List<EnvironmentCheck>();
        if (config.UseTemporaryCloudflareTunnel)
        {
            checks.Add(new EnvironmentCheck("Cloudflare 登录", true, "已选择临时 tunnel 模式，不需要登录。"));
            checks.Add(new EnvironmentCheck("Cloudflare Tunnel 配置", true, "临时 tunnel 每次启动都会生成新地址。"));
        }
        else
        {
            var cloudflaredPath = ExecutableResolver.ResolveCloudflared(config.CloudflaredPath);
            checks.Add(FileCheck("Cloudflare 登录凭据", AppPaths.CloudflaredCertPath));
            checks.Add(await CloudflareTunnelNameCheck(config, cloudflaredPath));
            checks.AddRange(CloudflareConfigChecks(config));
        }

        checks.Add(File.Exists(config.DevSpaceConfigPath)
            ? new EnvironmentCheck("DevSpace 配置", true, config.DevSpaceConfigPath)
            : new EnvironmentCheck("DevSpace 配置", false, $"缺失：{config.DevSpaceConfigPath}"));
        return await Task.FromResult(checks);
    }

    public async Task<EnvironmentDiagnostic> DiagnoseStartupAsync(HealthService health, CancellationToken cancellationToken = default)
    {
        var config = _configStore.Reload();
        var pub = await health.CheckPublicAsync(cancellationToken);
        if (pub.Ok)
        {
            return EnvironmentDiagnostic.Healthy("公网 MCP 可访问。");
        }

        var basic = await CheckBasicEnvironmentAsync();
        var failedBasic = basic.FirstOrDefault(check => !check.Ok);
        if (failedBasic is not null)
        {
            return EnvironmentDiagnostic.Unhealthy($"公网 MCP 不可访问；基础环境异常：{failedBasic.Name} - {failedBasic.Detail}");
        }

        var init = await CheckInitializationAsync();
        var failedInit = init.FirstOrDefault(check => !check.Ok);
        if (failedInit is not null)
        {
            return EnvironmentDiagnostic.Unhealthy($"公网 MCP 不可访问；初始化异常：{failedInit.Name} - {failedInit.Detail}");
        }

        var local = await health.CheckLocalAsync(cancellationToken);
        if (!local.Ok)
        {
            return EnvironmentDiagnostic.Unhealthy($"公网 MCP 不可访问；本地 DevSpace 也不可访问：{local.Message}");
        }

        if (config.UseTemporaryCloudflareTunnel)
        {
            return EnvironmentDiagnostic.Healthy("临时 tunnel 模式已启用，本地 DevSpace 可访问。临时公网地址请查看 cloudflared 日志。");
        }

        return EnvironmentDiagnostic.Unhealthy($"公网 MCP 不可访问：{pub.Message}");
    }

    public void InstallNvm() =>
        OpenPowerShellCommand("安装 nvm for Windows", "winget install --id CoreyButler.NVMforWindows -e --accept-package-agreements --accept-source-agreements");

    public void InstallGit() =>
        OpenPowerShellCommand("安装 Git", "winget install --id Git.Git -e --accept-package-agreements --accept-source-agreements");

    public void InstallSelectedNode()
    {
        var config = _configStore.Reload();
        OpenPowerShellCommand(
            $"使用 nvm 安装 Node {config.NodeVersion}",
            "$nvm = Get-Command nvm -ErrorAction SilentlyContinue" + Environment.NewLine +
            "if ($null -eq $nvm) {" + Environment.NewLine +
            "  Write-Host \"未找到 nvm。请先安装 nvm for Windows，安装后重启应用再继续。\" -ForegroundColor Yellow" + Environment.NewLine +
            "} else {" + Environment.NewLine +
            $"  nvm install {config.NodeVersion}" + Environment.NewLine +
            $"  nvm use {config.NodeVersion}" + Environment.NewLine +
            "}");
    }

    public void InstallDevSpace()
    {
        var config = _configStore.Reload();
        var nvmCommand = FindNvmCommand();
        var useNodeCommand = File.Exists(nvmCommand)
            ? $"{CommandProcess.Quote(CommandProcess.ToBashPath(nvmCommand))} use {config.NodeVersion}"
            : "";
        var npmInstallCommand = $"{CommandProcess.Quote(CommandProcess.ToBashPath(config.NpmCommand))} install -g @waishnav/devspace";
        var command = string.IsNullOrWhiteSpace(useNodeCommand)
            ? npmInstallCommand
            : $"{useNodeCommand} && {npmInstallCommand}";
        OpenBashCommand("安装或更新 DevSpace", config.GitBashPath, command);
    }

    public void RepairDevSpaceNativeDependencies()
    {
        var config = _configStore.Reload();
        var nvmCommand = FindNvmCommand();
        var nodeDirectory = config.NodeDirectory;
        var npm = Path.Combine(nodeDirectory, "npm.cmd");
        if (!File.Exists(npm))
        {
            npm = config.NpmCommand;
        }

        var packageDirectory = Path.Combine(nodeDirectory, "node_modules", "@waishnav", "devspace");
        var script = $$"""
        [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
        [Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false)
        chcp 65001 | Out-Null
        $ErrorActionPreference = "Continue"
        $Host.UI.RawUI.WindowTitle = "修复 DevSpace Node {{config.NodeVersion}} 依赖"
        Write-Host "修复 DevSpace Node {{config.NodeVersion}} 依赖" -ForegroundColor Cyan
        Write-Host ""
        Write-Host "Node 目录: {{nodeDirectory}}"
        & {{QuotePs(Path.Combine(nodeDirectory, "node.exe"))}} -p "process.version + ' / NODE_MODULE_VERSION=' + process.versions.modules"
        Write-Host ""
        {{(File.Exists(nvmCommand) ? $"Write-Host \"切换当前 nvm 版本...\"{Environment.NewLine}& {QuotePs(nvmCommand)} use {config.NodeVersion}{Environment.NewLine}Write-Host \"\"" : "")}}
        Write-Host "停止残留 DevSpace 进程..."
        Get-CimInstance Win32_Process |
          Where-Object {
            $name = $_.Name
            $cmd = $_.CommandLine
            ($name -in @('node.exe', 'bash.exe', 'sh.exe')) -and
            ($cmd -match '@waishnav\\devspace|@waishnav/devspace|[\\/]devspace(\s|$)')
          } |
          ForEach-Object {
            try { Stop-Process -Id $_.ProcessId -Force -ErrorAction Stop } catch {}
          }
        Write-Host "卸载旧包..."
        & {{QuotePs(npm)}} uninstall -g @waishnav/devspace
        Write-Host "清理残留文件..."
        Remove-Item -LiteralPath {{QuotePs(packageDirectory)}} -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath {{QuotePs(Path.Combine(nodeDirectory, "devspace"))}} -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath {{QuotePs(Path.Combine(nodeDirectory, "devspace.cmd"))}} -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath {{QuotePs(Path.Combine(nodeDirectory, "devspace.ps1"))}} -Force -ErrorAction SilentlyContinue
        Write-Host "重新安装 DevSpace..."
        & {{QuotePs(npm)}} install -g --force @waishnav/devspace
        Write-Host ""
        Write-Host "验证安装..."
        & {{QuotePs(npm)}} list -g @waishnav/devspace better-sqlite3 --depth=2
        Write-Host ""
        Read-Host "按 Enter 关闭"
        """;
        OpenScript($"修复 DevSpace Node {config.NodeVersion} 依赖", script);
    }

    public void InstallCloudflared() =>
        OpenPowerShellCommand("安装 cloudflared", "winget install --id Cloudflare.cloudflared -e --accept-package-agreements --accept-source-agreements");

    public Process? RunDevSpaceInit()
    {
        var config = _configStore.Reload();
        var command = $"{CommandProcess.Quote(CommandProcess.ToBashPath(config.DevSpaceCommand))} init";
        return OpenBashCommand("运行 devspace init", config.GitBashPath, command);
    }

    public void RunCloudflaredLogin()
    {
        var config = _configStore.Reload();
        var cloudflaredPath = ExecutableResolver.ResolveCloudflared(config.CloudflaredPath);
        OpenPowerShellCommand("Cloudflare Tunnel 授权登录", $"& {QuotePs(cloudflaredPath)} tunnel login");
    }

    public Process? SetupCloudflareTunnel()
    {
        var scriptPath = WriteCloudflareSetupScript();
        return Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoLogo -NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            UseShellExecute = true
        });
    }

    public void OpenInstallTerminal()
    {
        var scriptPath = WriteSetupScript();
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoLogo -NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            UseShellExecute = true
        });
    }

    private static EnvironmentCheck FileCheck(string name, string path) =>
        File.Exists(path)
            ? new EnvironmentCheck(name, true, path)
            : new EnvironmentCheck(name, false, $"缺失：{path}");

    private static async Task<EnvironmentCheck> CloudflareTunnelNameCheck(ManagerConfig config, string cloudflaredPath)
    {
        var tunnelName = config.CloudflareTunnelName.Trim();
        if (string.IsNullOrWhiteSpace(tunnelName))
        {
            return new EnvironmentCheck("Cloudflare 隧道名称", false, "未设置。建议每台电脑使用不同名称，例如 devspace-home。");
        }

        if (string.Equals(tunnelName, "devspace", StringComparison.OrdinalIgnoreCase))
        {
            return new EnvironmentCheck("Cloudflare 隧道名称", false, "仍是默认名 devspace。多台电脑会串线，请改成 devspace-home、devspace-company 这类唯一名称。");
        }

        if (!File.Exists(cloudflaredPath))
        {
            return new EnvironmentCheck("Cloudflare 隧道名称", false, "cloudflared 未安装，无法检查同名隧道。");
        }

        try
        {
            var output = ExtractJsonArray(await RunCaptureAsync(cloudflaredPath, $"tunnel list -o json --name {QuoteArg(tunnelName)}"));
            if (string.IsNullOrWhiteSpace(output))
            {
                return new EnvironmentCheck("Cloudflare 隧道名称", true, $"{tunnelName}（未发现远端同名，一键配置会创建）");
            }

            using var json = JsonDocument.Parse(output);
            if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() == 0)
            {
                return new EnvironmentCheck("Cloudflare 隧道名称", true, $"{tunnelName}（未发现远端同名，一键配置会创建）");
            }

            var tunnel = json.RootElement[0];
            var tunnelId = tunnel.TryGetProperty("id", out var idProperty) ? idProperty.GetString() ?? "" : "";
            var localTunnelId = File.Exists(config.CloudflaredConfigPath)
                ? ReadYamlValue(File.ReadAllLines(config.CloudflaredConfigPath), "tunnel")
                : "";
            var credentialPath = string.IsNullOrWhiteSpace(tunnelId)
                ? ""
                : Path.Combine(AppPaths.CloudflaredDirectory, $"{tunnelId}.json");

            if (string.Equals(localTunnelId, tunnelId, StringComparison.OrdinalIgnoreCase) || File.Exists(credentialPath))
            {
                return new EnvironmentCheck("Cloudflare 隧道名称", true, $"{tunnelName} -> {tunnelId}");
            }

            return new EnvironmentCheck("Cloudflare 隧道名称", false, $"Cloudflare 已有同名隧道 {tunnelName}，但本机没有它的凭据。请换一个唯一名称后点“一键配置 Cloudflare”。");
        }
        catch (Exception ex)
        {
            return new EnvironmentCheck("Cloudflare 隧道名称", false, $"检查失败：{ex.Message}");
        }
    }

    private static string ExtractJsonArray(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return "";

        var start = output.IndexOf('[');
        var end = output.LastIndexOf(']');
        if (start < 0 || end < start) return "";

        return output[start..(end + 1)];
    }

    private static IReadOnlyList<EnvironmentCheck> CloudflareConfigChecks(ManagerConfig config)
    {
        if (!File.Exists(config.CloudflaredConfigPath))
        {
            return
            [
                new EnvironmentCheck("Cloudflare Tunnel ID", false, "cloudflared 配置不存在，请点击“保存”补全配置。"),
                new EnvironmentCheck("Cloudflare 凭据路径", false, "cloudflared 配置不存在"),
                new EnvironmentCheck("Cloudflare 凭据文件", false, "cloudflared 配置不存在"),
                new EnvironmentCheck("Cloudflare 域名绑定", false, "cloudflared 配置不存在"),
                new EnvironmentCheck("Cloudflare 本地端口", false, "cloudflared 配置不存在")
            ];
        }

        try
        {
            var lines = File.ReadAllLines(config.CloudflaredConfigPath);
            var tunnelId = ReadYamlValue(lines, "tunnel");
            var credentialsFile = ReadYamlValue(lines, "credentials-file");
            var hostnames = ReadYamlValues(lines, "hostname");
            var services = ReadYamlValues(lines, "service");
            var expectedHost = ExpectedHost(config);
            var expectedPort = PublicEndpointSyncService.CloudflaredServicePort(config).ToString();
            var expectedServices = new[]
            {
                $"http://localhost:{expectedPort}",
                $"http://127.0.0.1:{expectedPort}"
            };

            var checks = new List<EnvironmentCheck>
            {
                string.IsNullOrWhiteSpace(tunnelId)
                    ? new EnvironmentCheck("Cloudflare Tunnel ID", false, "config.yml 缺少 tunnel:，请点击“保存”补全配置。")
                    : new EnvironmentCheck("Cloudflare Tunnel ID", true, tunnelId),

                string.IsNullOrWhiteSpace(credentialsFile)
                    ? new EnvironmentCheck("Cloudflare 凭据路径", false, "config.yml 缺少 credentials-file:")
                    : new EnvironmentCheck("Cloudflare 凭据路径", true, credentialsFile)
            };

            var credentialPath = ResolveCredentialsPath(credentialsFile);
            checks.Add(!string.IsNullOrWhiteSpace(credentialPath) && File.Exists(credentialPath)
                ? new EnvironmentCheck("Cloudflare 凭据文件", true, credentialPath)
                : new EnvironmentCheck("Cloudflare 凭据文件", false, string.IsNullOrWhiteSpace(credentialPath)
                    ? "未找到凭据路径"
                    : $"缺失：{credentialPath}"));

            checks.Add(hostnames.Any(host => string.Equals(host, expectedHost, StringComparison.OrdinalIgnoreCase))
                ? new EnvironmentCheck("Cloudflare 域名绑定", true, expectedHost)
                : new EnvironmentCheck("Cloudflare 域名绑定", false, hostnames.Count == 0
                    ? $"config.yml 缺少 hostname: {expectedHost}"
                    : $"当前：{string.Join(", ", hostnames)}；期望：{expectedHost}"));

            checks.Add(services.Any(service => expectedServices.Any(expected => string.Equals(service, expected, StringComparison.OrdinalIgnoreCase)))
                ? new EnvironmentCheck("Cloudflare 本地端口", true, $"localhost:{expectedPort}")
                : new EnvironmentCheck("Cloudflare 本地端口", false, services.Count == 0
                    ? $"config.yml 缺少 service: http://localhost:{expectedPort}"
                    : $"当前：{string.Join(", ", services)}；期望：http://localhost:{expectedPort}"));

            return checks;
        }
        catch (Exception ex)
        {
            return
            [
                new EnvironmentCheck("Cloudflare 配置解析", false, ex.Message)
            ];
        }
    }

    private static string ExpectedHost(ManagerConfig config)
    {
        var publicBaseUrl = string.IsNullOrWhiteSpace(config.FixedPublicBaseUrl)
            ? config.PublicBaseUrl
            : config.FixedPublicBaseUrl;
        if (Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var uri) &&
            !string.IsNullOrWhiteSpace(uri.Host))
        {
            return uri.Host;
        }

        return publicBaseUrl
            .Replace("https://", "", StringComparison.OrdinalIgnoreCase)
            .Replace("http://", "", StringComparison.OrdinalIgnoreCase)
            .Trim()
            .TrimEnd('/');
    }

    private static string ResolveCredentialsPath(string credentialsFile)
    {
        if (string.IsNullOrWhiteSpace(credentialsFile)) return "";

        var path = credentialsFile.Trim();
        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith(@"~\", StringComparison.Ordinal))
        {
            path = Path.Combine(AppPaths.UserProfile, path[2..]);
        }

        path = Environment.ExpandEnvironmentVariables(path);
        return Path.GetFullPath(path);
    }

    private static string ReadYamlValue(IEnumerable<string> lines, string key) =>
        ReadYamlValues(lines, key).FirstOrDefault() ?? "";

    private static List<string> ReadYamlValues(IEnumerable<string> lines, string key)
    {
        var values = new List<string>();
        var prefix = $"{key}:";
        var listPrefix = $"- {key}:";

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.StartsWith("#", StringComparison.Ordinal) || line.Length == 0) continue;

            string value;
            if (line.StartsWith(listPrefix, StringComparison.OrdinalIgnoreCase))
            {
                value = line[listPrefix.Length..];
            }
            else if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                value = line[prefix.Length..];
            }
            else
            {
                continue;
            }

            value = NormalizeYamlScalar(value);
            if (!string.IsNullOrWhiteSpace(value)) values.Add(value);
        }

        return values;
    }

    private static string NormalizeYamlScalar(string value)
    {
        value = value.Trim();
        var commentIndex = value.IndexOf(" #", StringComparison.Ordinal);
        if (commentIndex >= 0) value = value[..commentIndex].Trim();

        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            value = value[1..^1];
        }

        return value.Trim();
    }

    private static async Task<EnvironmentCheck> VersionCheck(string name, string file, string args)
    {
        if (!File.Exists(file)) return new EnvironmentCheck(name, false, $"缺失：{file}");

        try
        {
            var output = await RunCaptureAsync(file, args);
            return new EnvironmentCheck(name, true, output.Trim());
        }
        catch (Exception ex)
        {
            return new EnvironmentCheck(name, false, ex.Message);
        }
    }

    private static async Task<EnvironmentCheck> BashVersionCheck(string name, string bashPath, string commandPath, string args)
    {
        if (!File.Exists(bashPath)) return new EnvironmentCheck(name, false, $"缺失：{bashPath}");
        if (!File.Exists(commandPath)) return new EnvironmentCheck(name, false, $"缺失：{commandPath}");

        try
        {
            var command = $"{CommandProcess.Quote(CommandProcess.ToBashPath(commandPath))} {args}";
            var output = await RunBashCaptureAsync(bashPath, command);
            return new EnvironmentCheck(name, true, output.Trim());
        }
        catch (Exception ex)
        {
            return new EnvironmentCheck(name, false, ex.Message);
        }
    }

    private static async Task<string> RunCaptureAsync(string file, string args)
    {
        var start = CommandProcess.Create(file, args);
        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.CreateNoWindow = true;
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Failed to start {file}");
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? output : error);
        return string.IsNullOrWhiteSpace(output) ? error : output;
    }

    private static async Task<string> RunBashCaptureAsync(string bashPath, string command)
    {
        var start = CommandProcess.CreateBash(bashPath, command);
        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.CreateNoWindow = true;
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Failed to start {bashPath}");
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? output : error);
        return string.IsNullOrWhiteSpace(output) ? error : output;
    }

    private static string FindNvmCommand()
    {
        var nvmHome = Environment.GetEnvironmentVariable("NVM_HOME");
        var candidates = new[]
        {
            string.IsNullOrWhiteSpace(nvmHome) ? "" : Path.Combine(nvmHome, "nvm.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nvm", "nvm.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "nvm", "nvm.exe")
        };

        return candidates.FirstOrDefault(File.Exists) ?? "nvm.exe";
    }

    private static void OpenPowerShellCommand(string title, string command)
    {
        var script = $$"""
        [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
        [Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false)
        chcp 65001 | Out-Null
        $ErrorActionPreference = "Continue"
        $Host.UI.RawUI.WindowTitle = "{{title}}"
        Write-Host "{{title}}" -ForegroundColor Cyan
        Write-Host ""
        {{command}}
        Write-Host ""
        Read-Host "按 Enter 关闭"
        """;
        OpenScript(title, script);
    }

    private static Process? OpenBashCommand(string title, string bashPath, string command)
    {
        var script = $$"""
        [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
        [Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false)
        chcp 65001 | Out-Null
        $ErrorActionPreference = "Continue"
        $Host.UI.RawUI.WindowTitle = "{{title}}"
        Write-Host "{{title}}" -ForegroundColor Cyan
        Write-Host ""
        & {{QuotePs(bashPath)}} -lc {{QuotePs(command)}}
        Write-Host ""
        Read-Host "按 Enter 关闭"
        """;
        return OpenScript(title, script);
    }

    private static Process? OpenScript(string title, string script)
    {
        Directory.CreateDirectory(AppPaths.AppDataDirectory);
        var safeName = string.Concat(title.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-'));
        var path = Path.Combine(AppPaths.AppDataDirectory, $"{safeName}.ps1");
        File.WriteAllText(path, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoLogo -NoProfile -ExecutionPolicy Bypass -File \"{path}\"",
            UseShellExecute = true
        });
    }

    private static string QuotePs(string value) => $"'{value.Replace("'", "''")}'";

    private static string QuoteArg(string value) =>
        value.Any(char.IsWhiteSpace) ? $"\"{value.Replace("\"", "\\\"")}\"" : value;

    private string WriteSetupScript()
    {
        var config = _configStore.Current;
        var cloudflaredPath = ExecutableResolver.ResolveCloudflared(config.CloudflaredPath);
        Directory.CreateDirectory(AppPaths.AppDataDirectory);
        var path = Path.Combine(AppPaths.AppDataDirectory, "setup-devspace-manager.ps1");
        var content = $$"""
        [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
        [Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false)
        chcp 65001 | Out-Null
        $ErrorActionPreference = "Continue"
        function Pause-Step {
          Write-Host ""
          Read-Host "按 Enter 继续"
        }

        function Run-Step($label, $command) {
          Write-Host ""
          Write-Host "== $label ==" -ForegroundColor Cyan
          Write-Host $command -ForegroundColor DarkGray
          cmd.exe /d /s /c $command
          Pause-Step
        }

        function Run-BashStep($label, $command) {
          Write-Host ""
          Write-Host "== $label ==" -ForegroundColor Cyan
          Write-Host $command -ForegroundColor DarkGray
          & "{{config.GitBashPath}}" -lc $command
          Pause-Step
        }

        while ($true) {
          Clear-Host
          Write-Host "DevSpace 管理器初始化" -ForegroundColor Cyan
          Write-Host ""
          Write-Host "1. 用 nvm 安装 Node {{config.NodeVersion}}"
          Write-Host "2. 切换到 Node {{config.NodeVersion}}"
          Write-Host "3. 全局安装或更新 DevSpace"
          Write-Host "4. 用 winget 安装 cloudflared"
          Write-Host "5. 运行 devspace init"
          Write-Host "6. 运行 cloudflared tunnel login"
          Write-Host "7. 运行基础安装步骤"
          Write-Host "0. 退出"
          Write-Host ""
          $choice = Read-Host "请选择"
          switch ($choice) {
            "1" { Run-Step "安装 Node" "nvm install {{config.NodeVersion}}" }
            "2" { Run-Step "切换 Node" "nvm use {{config.NodeVersion}}" }
            "3" { Run-BashStep "安装 DevSpace" "'{{CommandProcess.ToBashPath(config.NpmCommand)}}' install -g @waishnav/devspace" }
            "4" { Run-Step "安装 cloudflared" "winget install --id Cloudflare.cloudflared -e --accept-package-agreements --accept-source-agreements" }
            "5" { Run-BashStep "DevSpace 初始化" "'{{CommandProcess.ToBashPath(config.DevSpaceCommand)}}' init" }
            "6" { Run-Step "cloudflared 登录" "`"{{cloudflaredPath}}`" tunnel login" }
            "7" {
              Run-Step "安装 Node" "nvm install {{config.NodeVersion}}"
              Run-Step "切换 Node" "nvm use {{config.NodeVersion}}"
              Run-BashStep "安装 DevSpace" "'{{CommandProcess.ToBashPath(config.NpmCommand)}}' install -g @waishnav/devspace"
              Run-Step "安装 cloudflared" "winget install --id Cloudflare.cloudflared -e --accept-package-agreements --accept-source-agreements"
            }
            "0" { exit 0 }
          }
        }
        """;
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    private string WriteCloudflareSetupScript()
    {
        var config = _configStore.Reload();
        var cloudflaredPath = ExecutableResolver.ResolveCloudflared(config.CloudflaredPath);
        Directory.CreateDirectory(AppPaths.AppDataDirectory);
        Directory.CreateDirectory(AppPaths.CloudflaredDirectory);
        var path = Path.Combine(AppPaths.AppDataDirectory, "setup-cloudflare-tunnel.ps1");
        var hostname = new Uri(string.IsNullOrWhiteSpace(config.FixedPublicBaseUrl) ? config.PublicBaseUrl : config.FixedPublicBaseUrl).Host;
        var servicePort = config.RequestProxyEnabled ? config.RequestProxyPort : config.DevSpacePort;
        var credentialsGlob = Path.Combine(AppPaths.CloudflaredDirectory, "*.json");
        var content = $$"""
        [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
        [Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false)
        chcp 65001 | Out-Null
        $ErrorActionPreference = "Stop"
        $Host.UI.RawUI.WindowTitle = "Cloudflare 一键配置"

        $cloudflared = "{{cloudflaredPath}}"
        $tunnelName = "{{config.CloudflareTunnelName}}"
        $hostname = "{{hostname}}"
        $port = {{servicePort}}
        $configPath = "{{config.CloudflaredConfigPath}}"
        $certPath = "{{AppPaths.CloudflaredCertPath}}"
        $cloudflaredDir = "{{AppPaths.CloudflaredDirectory}}"
        $credGlob = "{{credentialsGlob}}"

        function Pause-Step {
          Write-Host ""
          Read-Host "按 Enter 关闭"
        }

        function Show-Step($text) {
          Write-Host ""
          Write-Host "== $text ==" -ForegroundColor Cyan
        }

        function Ensure-Cloudflared {
          if (-not (Test-Path $cloudflared)) {
            throw "未找到 cloudflared：$cloudflared"
          }
        }

        function Ensure-Login {
          if (Test-Path $certPath) {
            Write-Host "已检测到 cert.pem，跳过登录。" -ForegroundColor Green
            return
          }

          Show-Step "需要 Cloudflare 登录"
          & $cloudflared tunnel login
          if (-not (Test-Path $certPath)) {
            throw "登录后仍未找到 cert.pem，请确认浏览器授权已完成。"
          }
        }

        function Get-ConfiguredTunnelId {
          if (-not (Test-Path $configPath)) { return $null }
          $match = Select-String -Path $configPath -Pattern '^\s*tunnel\s*:\s*(.+)\s*$' | Select-Object -First 1
          if ($null -eq $match) { return $null }
          return $match.Matches[0].Groups[1].Value.Trim().Trim('"').Trim("'")
        }

        function Select-JsonArray($text) {
          if ([string]::IsNullOrWhiteSpace($text)) { return $null }
          $start = $text.IndexOf('[')
          $end = $text.LastIndexOf(']')
          if ($start -lt 0 -or $end -lt $start) { return $null }
          return $text.Substring($start, $end - $start + 1)
        }

        function Get-TunnelInfo {
          $raw = & $cloudflared tunnel list -o json --name $tunnelName 2>$null
          $json = Select-JsonArray ($raw -join [Environment]::NewLine)
          if ([string]::IsNullOrWhiteSpace($json)) { return $null }
          try {
            $items = $json | ConvertFrom-Json
            if ($items -is [array]) {
              $item = $items | Select-Object -First 1
              if ($null -ne $item -and -not [string]::IsNullOrWhiteSpace($item.id)) { return $item }
            }
            return $null
          } catch {
            return $null
          }
        }

        function Ensure-Tunnel {
          if ($tunnelName -eq "devspace") {
            throw '当前隧道名称还是默认的 devspace。多台电脑会互相串线，请先在管理器基础页把隧道名称改成唯一名称，例如 devspace-home 或 devspace-company，然后保存后再运行一键配置。'
          }

          $existing = Get-TunnelInfo
          if ($null -ne $existing) {
            $credFile = Join-Path $cloudflaredDir ($existing.id + ".json")
            $configuredTunnelId = Get-ConfiguredTunnelId
            if ((-not (Test-Path $credFile)) -and ($configuredTunnelId -ne $existing.id)) {
              throw "Cloudflare 账号里已经有同名隧道：$tunnelName，但这台电脑没有对应凭据。为避免多台电脑共用同一个 tunnel，请换一个唯一隧道名称后再试，例如 $tunnelName-$($env:COMPUTERNAME)。"
            }

            Write-Host "已找到 tunnel：$($existing.id)" -ForegroundColor Green
            return $existing
          }

          Show-Step "创建 tunnel"
          & $cloudflared tunnel create $tunnelName
          $created = Get-TunnelInfo
          if ($null -eq $created) {
            throw "创建 tunnel 后仍未查询到：$tunnelName"
          }
          return $created
        }

        function Ensure-Credentials($tunnel) {
          $credFile = Join-Path $cloudflaredDir ($tunnel.id + ".json")
          if (-not (Test-Path $credFile)) {
            Show-Step "拉取 tunnel 凭据"
            & $cloudflared tunnel token --cred-file $credFile $tunnel.id | Out-Null
          }
          if (-not (Test-Path $credFile)) {
            throw "未生成 tunnel 凭据文件：$credFile"
          }
          return $credFile
        }

        function Write-Config($tunnel, $credFile) {
          Show-Step "写入 cloudflared 配置"
          $yaml = @(
            "tunnel: $($tunnel.id)",
            "credentials-file: $credFile",
            "",
            "ingress:",
            "  - hostname: $hostname",
            "    service: http://localhost:$port",
            "  - service: http_status:404"
          ) -join [Environment]::NewLine
          $yaml | Set-Content -Path $configPath -Encoding UTF8
        }

        function Route-Dns($tunnel) {
          Show-Step "绑定域名"
          & $cloudflared tunnel route dns -f $tunnel.id $hostname
        }

        try {
          Ensure-Cloudflared
          Ensure-Login
          $tunnel = Ensure-Tunnel
          $credFile = Ensure-Credentials $tunnel
          Write-Config $tunnel $credFile
          Route-Dns $tunnel
          Write-Host ""
          Write-Host "Cloudflare Tunnel 配置完成。" -ForegroundColor Green
          Write-Host "域名：https://$hostname" -ForegroundColor Green
          Write-Host "下一步：回到管理器，点击“重启全部”或“启动隧道”。" -ForegroundColor Yellow
        } catch {
          Write-Host ""
          Write-Host $_.Exception.Message -ForegroundColor Red
        }

        Pause-Step
        """;
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }
}

internal sealed record EnvironmentCheck(string Name, bool Ok, string Detail);

internal sealed record EnvironmentDiagnostic(bool Ok, string Message)
{
    public static EnvironmentDiagnostic Healthy(string message) => new(true, message);

    public static EnvironmentDiagnostic Unhealthy(string message) => new(false, message);
}
