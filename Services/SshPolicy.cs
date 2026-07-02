using System.Text.RegularExpressions;
namespace DevSpaceManager.Services;

internal static class SshPolicy
{
    public const string ModeReadonly = "readonly";
    public const string ModeRestricted = "restricted";
    public const string ModeUnrestricted = "unrestricted";

    public static readonly IReadOnlyList<SshPolicyTemplate> Templates =
    [
        new("inspection", "巡检", "允许常见系统巡检、状态查看和目录读取命令。", [
            @"^\s*(pwd|whoami|hostname|date|uptime|id)\b.*$",
            @"^\s*(ls|dir)\b.*$",
            @"^\s*(cat|head|tail|grep|find)\b.*$",
            @"^\s*(df|du|free|top|ps|ss|netstat|ip|ifconfig)\b.*$",
            @"^\s*systemctl\s+status\b.*$",
            @"^\s*journalctl\b.*$",
            @"^\s*docker\s+(ps|logs|inspect|stats)\b.*$",
            @"^\s*git\s+(status|log|diff|show|branch)\b.*$"
        ]),
        new("logs", "日志排查", "适合查看 /var/log、应用日志和服务状态。", [
            @"^\s*(pwd|whoami|hostname|date|uptime)\b.*$",
            @"^\s*(ls|cat|head|tail|grep|find)\b.*(/var/log|logs?|\.log).*$",
            @"^\s*journalctl\b.*$",
            @"^\s*systemctl\s+status\b.*$",
            @"^\s*docker\s+logs\b.*$",
            @"^\s*docker\s+ps\b.*$"
        ]),
        new("custom", "自定义", "只使用当前服务器手动维护的 allow/deny 规则。", [])
    ];

    private static readonly Regex[] ReadonlyDenyRegexes =
    [
        new(@"(^|[\s;&|])rm(\s|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(^|[\s;&|])rmdir(\s|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(^|[\s;&|])mv(\s|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(^|[\s;&|])dd(\s|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(^|[\s;&|])mkfs(\.|\s|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(^|[\s;&|])chmod(\s|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(^|[\s;&|])chown(\s|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(^|[\s;&|])truncate(\s|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(^|[\s;&|])tee(\s|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(^|[\s;&|])sudo(\s|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(^|[\s;&|])su(\s|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(^|[\s;&|])kill(all)?(\s|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(^|[\s;&|])pkill(\s|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(^|[\s;&|])(shutdown|reboot|halt|poweroff)(\s|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(^|[\s;&|])systemctl\s+(restart|stop|reload|start|enable|disable|mask)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(^|[\s;&|])service\s+\S+\s+(restart|stop|reload|start)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(^|[\s;&|])docker\s+(rm|stop|restart|kill|prune|system)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(^|[\s;&|])apt(-get)?\s+(install|remove|purge|upgrade|update)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(^|[\s;&|])(yum|dnf)\s+(install|remove|update|upgrade)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(^|[\s;&|])pip\s+(install|uninstall)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(^|[\s;&|])npm\s+(install|uninstall|publish)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(^|[\s;&|])git\s+(reset\s+--hard|push\s+.*--force|clean\s+-fd?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@">\s*/(?!dev/null|dev/stdout|dev/stderr|tmp)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@">>\s*/(?!dev/null|tmp)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\|\s*(sh|bash)(\s|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(curl|wget)\s+[^|]*\|\s*(sh|bash)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)
    ];

    public static PolicyResult Evaluate(ISshPolicySubject profile, string command)
    {
        var mode = NormalizeMode(profile.SecurityMode);
        if (mode == ModeUnrestricted) return PolicyResult.Allowed("完全允许");

        foreach (var deny in ReadonlyDenyRegexes)
        {
            if (deny.IsMatch(command))
            {
                return PolicyResult.Denied($"命令命中内置危险模式：{deny}");
            }
        }

        foreach (var pattern in profile.DenyPatterns ?? [])
        {
            if (Matches(pattern, command, out var error))
            {
                return PolicyResult.Denied($"命令命中拒绝规则：{pattern}");
            }

            if (!string.IsNullOrWhiteSpace(error))
            {
                return PolicyResult.Denied($"拒绝规则无效：{pattern}");
            }
        }

        if (mode == ModeReadonly) return PolicyResult.Allowed("只读模式未命中危险规则");

        var allowPatterns = EffectiveAllowPatterns(profile).ToArray();
        if (allowPatterns.Length == 0)
        {
            return PolicyResult.Denied("受限模式没有可用允许规则。");
        }

        foreach (var pattern in allowPatterns)
        {
            if (Matches(pattern, command, out var error))
            {
                return PolicyResult.Allowed($"命中允许规则：{pattern}");
            }

            if (!string.IsNullOrWhiteSpace(error))
            {
                return PolicyResult.Denied($"允许规则无效：{pattern}");
            }
        }

        return PolicyResult.Denied("命令没有命中任何允许规则。");
    }

    public static IEnumerable<string> EffectiveAllowPatterns(ISshPolicySubject profile)
    {
        foreach (var pattern in profile.AllowPatterns ?? [])
        {
            if (!string.IsNullOrWhiteSpace(pattern)) yield return pattern.Trim();
        }

        if (!string.Equals(profile.PolicyTemplate, "custom", StringComparison.OrdinalIgnoreCase))
        {
            var template = Templates.FirstOrDefault(item => string.Equals(item.Id, profile.PolicyTemplate, StringComparison.OrdinalIgnoreCase));
            if (template is not null)
            {
                foreach (var pattern in template.AllowPatterns)
                {
                    yield return pattern;
                }
            }
        }
    }

    public static string NormalizeMode(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized is ModeReadonly or ModeRestricted or ModeUnrestricted ? normalized : ModeRestricted;
    }

    private static bool Matches(string pattern, string command, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        try
        {
            return Regex.IsMatch(command, pattern.Trim(), RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250));
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}

internal sealed record SshPolicyTemplate(string Id, string Name, string Description, IReadOnlyList<string> AllowPatterns);

internal interface ISshPolicySubject
{
    string SecurityMode { get; }
    string PolicyTemplate { get; }
    List<string> AllowPatterns { get; }
    List<string> DenyPatterns { get; }
}

internal sealed record PolicyResult(bool IsAllowed, string Reason)
{
    public static PolicyResult Allowed(string reason) => new(true, reason);

    public static PolicyResult Denied(string reason) => new(false, reason);
}
