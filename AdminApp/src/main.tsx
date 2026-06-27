import React from "react";
import { createRoot } from "react-dom/client";
import {
  Activity,
  Bug,
  CheckCircle2,
  Globe2,
  HardDrive,
  Loader2,
  Network,
  RefreshCw,
  Settings,
  TerminalSquare,
} from "lucide-react";
import "./styles.css";

type BridgeResponse<T = unknown> = {
  id: string;
  ok: boolean;
  data?: T;
  error?: string;
};

type PublicProfile = {
  id: string;
  name: string;
  userDataFolder: string;
  proxyConfigured: boolean;
  language: string;
  temporary: boolean;
};

type PublicConfig = {
  publicBaseUrl: string;
  mcpUrl: string;
  localHealthUrl: string;
  publicHealthUrl: string;
  tunnelName: string;
  cloudflaredProtocol: string;
  devSpacePort: number;
  requestProxyEnabled: boolean;
  requestProxyPort: number;
  autoStartDevSpace: boolean;
  autoStartTunnel: boolean;
  autoRestart: boolean;
  localDebugEnabled: boolean;
  localDebugPort: number;
  activeProfileId: string;
  profiles: PublicProfile[];
};

type Snapshot = {
  checkedAt: string;
  config: PublicConfig;
  services: {
    devspace: { running: boolean; healthOk: boolean; message: string };
    tunnel: { running: boolean; healthOk: boolean; message: string };
  };
  logs: Record<string, string>;
};

type SectionKey = "overview" | "browser" | "proxy" | "debug" | "logs";

declare global {
  interface Window {
    chrome?: {
      webview?: {
        postMessage: (message: unknown) => void;
        addEventListener: (event: "message", handler: (event: MessageEvent<BridgeResponse>) => void) => void;
      };
    };
  }
}

let nextId = 0;
const pending = new Map<string, { resolve: (value: unknown) => void; reject: (error: Error) => void }>();

window.chrome?.webview?.addEventListener("message", (event) => {
  const response = event.data;
  const resolver = pending.get(response.id);
  if (!resolver) return;
  pending.delete(response.id);
  if (response.ok) resolver.resolve(response.data);
  else resolver.reject(new Error(response.error ?? "操作失败"));
});

function bridge<T>(command: string, payload: Record<string, unknown> = {}) {
  const id = String(++nextId);
  window.chrome?.webview?.postMessage({ id, command, payload });
  return new Promise<T>((resolve, reject) => pending.set(id, { resolve: resolve as (value: unknown) => void, reject }));
}

function App() {
  const [active, setActive] = React.useState<SectionKey>("overview");
  const [snapshot, setSnapshot] = React.useState<Snapshot | null>(null);
  const [loading, setLoading] = React.useState(true);
  const [saving, setSaving] = React.useState(false);
  const [error, setError] = React.useState("");
  const [debugEnabled, setDebugEnabled] = React.useState(false);
  const [debugPort, setDebugPort] = React.useState("9223");

  const load = React.useCallback(async () => {
    setLoading(true);
    setError("");
    try {
      const next = await bridge<Snapshot>("app.snapshot");
      setSnapshot(next);
      setDebugEnabled(next.config.localDebugEnabled);
      setDebugPort(String(next.config.localDebugPort));
    } catch (err) {
      setError(err instanceof Error ? err.message : "刷新失败");
    } finally {
      setLoading(false);
    }
  }, []);

  React.useEffect(() => {
    void load();
  }, [load]);

  async function saveDebug() {
    const port = Number(debugPort);
    setSaving(true);
    setError("");
    try {
      const config = await bridge<PublicConfig>("debug.save", { enabled: debugEnabled, port });
      setSnapshot((current) => (current ? { ...current, config } : current));
    } catch (err) {
      setError(err instanceof Error ? err.message : "保存失败");
    } finally {
      setSaving(false);
    }
  }

  const config = snapshot?.config;
  const sections: Array<{ key: SectionKey; label: string; icon: React.ComponentType<{ size?: number; strokeWidth?: number }> }> = [
    { key: "overview", label: "总览", icon: Activity },
    { key: "browser", label: "浏览器", icon: Globe2 },
    { key: "proxy", label: "代理", icon: Network },
    { key: "debug", label: "本地调试", icon: Bug },
    { key: "logs", label: "日志", icon: TerminalSquare },
  ];

  return (
    <main className="settings-shell">
      <aside className="sidebar">
        <div className="sidebar-title">
          <Settings size={15} strokeWidth={1.4} aria-hidden="true" />
          <span>设置</span>
        </div>
        <nav className="nav-list" aria-label="设置分类">
          {sections.map((item) => {
            const Icon = item.icon;
            return (
              <button
                key={item.key}
                className={active === item.key ? "nav-item active" : "nav-item"}
                onClick={() => setActive(item.key)}
                type="button"
              >
                <Icon size={15} strokeWidth={1.35} aria-hidden="true" />
                <span>{item.label}</span>
              </button>
            );
          })}
        </nav>
      </aside>

      <section className="content">
        <header className="content-header">
          <div>
            <h1>{sections.find((item) => item.key === active)?.label}</h1>
            <p>{subtitle(active)}</p>
          </div>
          <button className="ghost-button" onClick={load} disabled={loading} type="button">
            {loading ? <Loader2 className="spin" size={14} strokeWidth={1.4} /> : <RefreshCw size={14} strokeWidth={1.4} />}
            刷新
          </button>
        </header>

        {error && <div className="error-banner">{error}</div>}

        {!snapshot || !config ? (
          <div className="empty-state">{loading ? "正在读取设置..." : "暂无设置数据"}</div>
        ) : (
          <>
            {active === "overview" && <Overview snapshot={snapshot} />}
            {active === "browser" && <BrowserSettings config={config} />}
            {active === "proxy" && <ProxySettings config={config} />}
            {active === "debug" && (
              <DebugSettings
                enabled={debugEnabled}
                port={debugPort}
                saving={saving}
                onEnabledChange={setDebugEnabled}
                onPortChange={setDebugPort}
                onSave={saveDebug}
              />
            )}
            {active === "logs" && <LogSettings logs={snapshot.logs} />}
          </>
        )}
      </section>
    </main>
  );
}

function Overview({ snapshot }: { snapshot: Snapshot }) {
  return (
    <div className="settings-panel">
      <SettingRow title="DevSpace" description={snapshot.services.devspace.message}>
        <StatusPill ok={snapshot.services.devspace.running && snapshot.services.devspace.healthOk} />
      </SettingRow>
      <SettingRow title="Cloudflare Tunnel" description={snapshot.services.tunnel.message}>
        <StatusPill ok={snapshot.services.tunnel.running && snapshot.services.tunnel.healthOk} />
      </SettingRow>
      <SettingRow title="MCP 地址" description={snapshot.config.mcpUrl}>
        <CopyText text={snapshot.config.mcpUrl} />
      </SettingRow>
      <SettingRow title="上次检查" description={new Date(snapshot.checkedAt).toLocaleString()} />
    </div>
  );
}

function BrowserSettings({ config }: { config: PublicConfig }) {
  const activeProfile = config.profiles.find((profile) => profile.id === config.activeProfileId);
  return (
    <div className="settings-panel">
      <SettingRow title="当前 Profile" description={activeProfile ? activeProfile.name : config.activeProfileId}>
        <span className="muted-value">{config.activeProfileId}</span>
      </SettingRow>
      <SettingRow title="用户数据目录" description={activeProfile?.userDataFolder ?? "未配置"} />
      <SettingRow title="语言" description={activeProfile?.language ?? "zh-CN"} />
      <SettingRow title="代理状态" description={activeProfile?.proxyConfigured ? "当前 Profile 已配置出站代理" : "当前 Profile 未配置出站代理"}>
        <StatusPill ok={Boolean(activeProfile?.proxyConfigured)} okText="已配置" failText="未配置" />
      </SettingRow>
    </div>
  );
}

function ProxySettings({ config }: { config: PublicConfig }) {
  return (
    <div className="settings-panel">
      <SettingRow title="请求监控代理" description={config.requestProxyEnabled ? "DevSpace MCP 请求会经过本地监控代理。" : "当前直连 DevSpace 服务。"}>
        <StatusPill ok={config.requestProxyEnabled} okText="开启" failText="关闭" />
      </SettingRow>
      <SettingRow title="监控代理端口" description={`127.0.0.1:${config.requestProxyPort}`} />
      <SettingRow title="DevSpace 端口" description={`127.0.0.1:${config.devSpacePort}`} />
      <SettingRow title="公网地址" description={config.publicBaseUrl} />
    </div>
  );
}

function DebugSettings({
  enabled,
  port,
  saving,
  onEnabledChange,
  onPortChange,
  onSave,
}: {
  enabled: boolean;
  port: string;
  saving: boolean;
  onEnabledChange: (value: boolean) => void;
  onPortChange: (value: string) => void;
  onSave: () => void;
}) {
  const debugUrl = `http://127.0.0.1:${port || "9223"}/json`;
  return (
    <div className="settings-panel">
      <SettingRow
        title="WebView2 CDP 调试端口"
        description="开启后可通过 Chrome DevTools Protocol 检查当前 ChatGPT WebView。仅建议开发时使用。"
      >
        <Switch checked={enabled} onChange={onEnabledChange} label="开启本地调试" />
      </SettingRow>
      <SettingRow title="端口" description="保存后需要重启应用才会对 ChatGPT WebView 生效。">
        <input
          className="number-input"
          inputMode="numeric"
          min="1024"
          max="65535"
          value={port}
          onChange={(event) => onPortChange(event.target.value.replace(/[^\d]/g, "").slice(0, 5))}
          aria-label="本地调试端口"
        />
      </SettingRow>
      <SettingRow title="调试入口" description={debugUrl}>
        <CopyText text={debugUrl} />
      </SettingRow>
      <div className="panel-footer">
        <p>端口只用于本机调试。ChatGPT 页面仍然不会获得本地 bridge 权限。</p>
        <button className="primary-button" onClick={onSave} disabled={saving} type="button">
          {saving ? <Loader2 className="spin" size={14} strokeWidth={1.4} /> : <CheckCircle2 size={14} strokeWidth={1.4} />}
          保存
        </button>
      </div>
    </div>
  );
}

function LogSettings({ logs }: { logs: Record<string, string> }) {
  return (
    <div className="settings-panel">
      {Object.entries(logs).map(([key, path]) => (
        <SettingRow key={key} title={logLabel(key)} description={path}>
          <HardDrive size={15} strokeWidth={1.35} aria-hidden="true" />
        </SettingRow>
      ))}
    </div>
  );
}

function SettingRow({ title, description, children }: { title: string; description?: string; children?: React.ReactNode }) {
  return (
    <div className="setting-row">
      <div className="setting-copy">
        <div className="setting-title">{title}</div>
        {description && <div className="setting-description">{description}</div>}
      </div>
      {children && <div className="setting-control">{children}</div>}
    </div>
  );
}

function Switch({ checked, onChange, label }: { checked: boolean; onChange: (value: boolean) => void; label: string }) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      aria-label={label}
      className={checked ? "switch checked" : "switch"}
      onClick={() => onChange(!checked)}
    >
      <span />
    </button>
  );
}

function StatusPill({ ok, okText = "正常", failText = "注意" }: { ok: boolean; okText?: string; failText?: string }) {
  return <span className={ok ? "status-pill ok" : "status-pill warn"}>{ok ? okText : failText}</span>;
}

function CopyText({ text }: { text: string }) {
  return (
    <button className="secondary-button" type="button" onClick={() => void navigator.clipboard?.writeText(text)}>
      复制
    </button>
  );
}

function subtitle(section: SectionKey) {
  switch (section) {
    case "overview":
      return "查看当前服务状态和连接地址。";
    case "browser":
      return "查看 ChatGPT WebView 使用的 Profile 信息。";
    case "proxy":
      return "查看 DevSpace 请求监控和端口配置。";
    case "debug":
      return "配置仅本机可用的 WebView2 调试入口。";
    case "logs":
      return "定位本地运行日志文件。";
  }
}

function logLabel(key: string) {
  switch (key) {
    case "devspaceOut":
      return "DevSpace 输出";
    case "devspaceErr":
      return "DevSpace 错误";
    case "tunnelOut":
      return "隧道输出";
    case "tunnelErr":
      return "隧道错误";
    default:
      return key;
  }
}

createRoot(document.getElementById("root")!).render(<App />);
