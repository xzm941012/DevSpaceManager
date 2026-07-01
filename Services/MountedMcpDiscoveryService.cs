using System.Text;
using DevSpaceManager.Core;

namespace DevSpaceManager.Services;

internal sealed class MountedMcpDiscoveryService
{
    public IReadOnlyList<MountedMcpDefinition> Discover()
    {
        if (!File.Exists(AppPaths.CodexConfigPath)) return [];

        var builders = new Dictionary<string, Builder>(StringComparer.OrdinalIgnoreCase);
        Builder? current = null;
        var inEnv = false;

        foreach (var rawLine in File.ReadLines(AppPaths.CodexConfigPath))
        {
            var line = StripComment(rawLine).Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var section = line[1..^1].Trim();
                current = null;
                inEnv = false;

                if (section.StartsWith("mcp_servers.", StringComparison.OrdinalIgnoreCase))
                {
                    var rest = section["mcp_servers.".Length..];
                    if (rest.EndsWith(".env", StringComparison.OrdinalIgnoreCase))
                    {
                        var name = rest[..^".env".Length];
                        current = GetBuilder(builders, UnquoteKey(name));
                        inEnv = true;
                    }
                    else if (!string.IsNullOrWhiteSpace(rest))
                    {
                        current = GetBuilder(builders, UnquoteKey(rest));
                    }
                }

                continue;
            }

            if (current is null) continue;
            var equals = line.IndexOf('=');
            if (equals <= 0) continue;
            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();

            if (inEnv)
            {
                current.Env[UnquoteKey(key)] = ParseString(value);
                continue;
            }

            switch (key)
            {
                case "type":
                    current.Type = ParseString(value);
                    break;
                case "command":
                    current.Command = ParseString(value);
                    break;
                case "args":
                    current.Args = ParseStringArray(value);
                    break;
                case "startup_timeout_ms" when int.TryParse(value, out var ms):
                    current.StartupTimeoutMs = Math.Clamp(ms, 1_000, 120_000);
                    break;
                case "startup_timeout_sec" when int.TryParse(value, out var sec):
                    current.StartupTimeoutMs = Math.Clamp(sec * 1000, 1_000, 120_000);
                    break;
            }
        }

        return builders.Values
            .Where(item => !string.IsNullOrWhiteSpace(item.Command))
            .Select(item => new MountedMcpDefinition
            {
                Name = item.Name,
                Type = string.IsNullOrWhiteSpace(item.Type) ? "stdio" : item.Type,
                Command = item.Command,
                Args = item.Args,
                Env = item.Env,
                StartupTimeoutMs = item.StartupTimeoutMs
            })
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static Builder GetBuilder(Dictionary<string, Builder> builders, string name)
    {
        if (builders.TryGetValue(name, out var existing)) return existing;
        var builder = new Builder(name);
        builders[name] = builder;
        return builder;
    }

    private static string StripComment(string line)
    {
        var quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quote == '\0' && (ch == '"' || ch == '\''))
            {
                quote = ch;
                continue;
            }

            if (quote != '\0')
            {
                if (ch == '\\' && quote == '"' && i + 1 < line.Length)
                {
                    i++;
                    continue;
                }

                if (ch == quote) quote = '\0';
                continue;
            }

            if (ch == '#') return line[..i];
        }

        return line;
    }

    private static string UnquoteKey(string value) => ParseString(value.Trim());

    private static string ParseString(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return UnescapeDoubleQuoted(value[1..^1]);
        }

        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
        {
            return value[1..^1];
        }

        return value;
    }

    private static List<string> ParseStringArray(string value)
    {
        value = value.Trim();
        if (!value.StartsWith('[') || !value.EndsWith(']')) return [];

        var result = new List<string>();
        var inner = value[1..^1];
        var token = new StringBuilder();
        var quote = '\0';

        for (var i = 0; i < inner.Length; i++)
        {
            var ch = inner[i];
            if (quote == '\0' && (ch == '"' || ch == '\''))
            {
                quote = ch;
                token.Append(ch);
                continue;
            }

            if (quote != '\0')
            {
                token.Append(ch);
                if (ch == '\\' && quote == '"' && i + 1 < inner.Length)
                {
                    token.Append(inner[++i]);
                    continue;
                }

                if (ch == quote) quote = '\0';
                continue;
            }

            if (ch == ',')
            {
                AddArrayToken(result, token);
                continue;
            }

            token.Append(ch);
        }

        AddArrayToken(result, token);
        return result;
    }

    private static void AddArrayToken(List<string> result, StringBuilder token)
    {
        var value = token.ToString().Trim();
        token.Clear();
        if (!string.IsNullOrWhiteSpace(value)) result.Add(ParseString(value));
    }

    private static string UnescapeDoubleQuoted(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch != '\\' || i + 1 >= value.Length)
            {
                builder.Append(ch);
                continue;
            }

            var next = value[++i];
            builder.Append(next switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                '"' => '"',
                '\\' => '\\',
                _ => next
            });
        }

        return builder.ToString();
    }

    private sealed class Builder(string name)
    {
        public string Name { get; } = name;
        public string Type { get; set; } = "stdio";
        public string Command { get; set; } = "";
        public List<string> Args { get; set; } = [];
        public Dictionary<string, string> Env { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int StartupTimeoutMs { get; set; } = 20_000;
    }
}
