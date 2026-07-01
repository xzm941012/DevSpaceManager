import React from "react";
import { createRoot } from "react-dom/client";
import {
  Activity,
  ArrowLeft,
  Bug,
  CheckCircle2,
  Eye,
  EyeOff,
  RotateCw,
  Info,
  Globe2,
  HardDrive,
  Loader2,
  ListTree,
  Network,
  Pencil,
  Plug,
  Plus,
  RefreshCw,
  Settings,
  TerminalSquare,
  Trash2,
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
  proxyServer: string;
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
  startWithWindows: boolean;
  startMinimizedToTray: boolean;
  autoRestart: boolean;
  localDebugEnabled: boolean;
  localDebugPort: number;
  activeProfileId: string;
  profiles: PublicProfile[];
};

type Snapshot = {
  checkedAt: string;
  config: PublicConfig;
  ownerPassword: string;
  services: {
    devspace: { running: boolean; healthOk: boolean; message: string };
    tunnel: { running: boolean; healthOk: boolean; message: string };
  };
  logs: Record<string, string>;
};

type ProfileList = {
  activeProfileId: string;
  profiles: PublicProfile[];
};

type MountedMcpTool = {
  name: string;
  description: string;
  inputSchema: unknown;
};

type MountedMcpServer = {
  name: string;
  type: string;
  command: string;
  enabled: boolean;
  description: string;
  instructions: string;
  refreshedAt?: string;
  toolCount: number;
  tools: MountedMcpTool[];
};

type MountedMcpList = {
  servers: MountedMcpServer[];
};

type SectionKey = "overview" | "browser" | "mcp" | "proxy" | "debug" | "logs";

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
  const [toast, setToast] = React.useState("");
  const [debugEnabled, setDebugEnabled] = React.useState(false);
  const [debugPort, setDebugPort] = React.useState("9223");
  const [proxyEnabled, setProxyEnabled] = React.useState(false);
  const [startWithWindows, setStartWithWindows] = React.useState(false);
  const [startMinimizedToTray, setStartMinimizedToTray] = React.useState(false);
  const [mountedMcps, setMountedMcps] = React.useState<MountedMcpServer[]>([]);
  const [mcpLoading, setMcpLoading] = React.useState(false);

  const load = React.useCallback(async () => {
    setLoading(true);
    setError("");
    try {
      const next = await bridge<Snapshot>("app.snapshot");
      setSnapshot(next);
      setDebugEnabled(next.config.localDebugEnabled);
      setDebugPort(String(next.config.localDebugPort));
      setProxyEnabled(next.config.requestProxyEnabled);
      setStartWithWindows(next.config.startWithWindows);
      setStartMinimizedToTray(next.config.startMinimizedToTray);
    } catch (err) {
      setError(err instanceof Error ? err.message : "刷新失败");
    } finally {
      setLoading(false);
    }
  }, []);

  React.useEffect(() => {
    void load();
  }, [load]);

  const loadMountedMcps = React.useCallback(async (showToast = false) => {
    setMcpLoading(true);
    setError("");
    try {
      const next = await bridge<MountedMcpList>("mcp.list");
      setMountedMcps(next.servers);
      if (showToast) setToast("MCP 列表已刷新。");
    } catch (err) {
      setError(err instanceof Error ? err.message : "读取 MCP 失败");
    } finally {
      setMcpLoading(false);
    }
  }, []);

  React.useEffect(() => {
    if (active === "mcp") void loadMountedMcps();
  }, [active, loadMountedMcps]);

  React.useEffect(() => {
    if (!toast) return;
    const timer = window.setTimeout(() => setToast(""), 2800);
    return () => window.clearTimeout(timer);
  }, [toast]);

  async function saveDebug() {
    const port = Number(debugPort);
    setSaving(true);
    setError("");
    setToast("");
    try {
      const config = await bridge<PublicConfig>("debug.save", { enabled: debugEnabled, port });
      setSnapshot((current) => (current ? { ...current, config } : current));
      setToast("设置已保存，重启应用后生效。");
    } catch (err) {
      setError(err instanceof Error ? err.message : "保存失败");
    } finally {
      setSaving(false);
    }
  }

  function applyProfiles(next: ProfileList) {
    setSnapshot((current) =>
      current
        ? {
            ...current,
            config: {
              ...current.config,
              activeProfileId: next.activeProfileId,
              profiles: next.profiles,
            },
          }
        : current,
    );
  }

  async function switchProfile(id: string) {
    setSaving(true);
    setError("");
    setToast("");
    try {
      const next = await bridge<ProfileList>("profile.switch", { id });
      applyProfiles(next);
      setToast("Profile 已切换，重启应用后生效。");
    } catch (err) {
      setError(err instanceof Error ? err.message : "切换失败");
    } finally {
      setSaving(false);
    }
  }

  async function saveProfile(profile: ProfileDraft) {
    setSaving(true);
    setError("");
    setToast("");
    try {
      const next = await bridge<ProfileList>("profile.save", profile);
      applyProfiles(next);
      setToast(profile.id ? "Profile 已保存，重启应用后生效。" : "Profile 已新增。");
    } catch (err) {
      setError(err instanceof Error ? err.message : "保存失败");
      throw err;
    } finally {
      setSaving(false);
    }
  }

  async function deleteProfile(id: string) {
    if (!window.confirm("删除这个 Profile？用户数据目录不会被删除。")) return;
    setSaving(true);
    setError("");
    setToast("");
    try {
      const next = await bridge<ProfileList>("profile.delete", { id });
      applyProfiles(next);
      setToast("Profile 已删除。");
    } catch (err) {
      setError(err instanceof Error ? err.message : "删除失败");
    } finally {
      setSaving(false);
    }
  }

  async function exitApplication() {
    setSaving(true);
    setError("");
    setToast("");
    try {
      await bridge("app.exit");
    } catch (err) {
      setError(err instanceof Error ? err.message : "关闭失败");
    } finally {
      setSaving(false);
    }
  }

  async function saveProxyEnabled(enabled: boolean) {
    setProxyEnabled(enabled);
    setSaving(true);
    setError("");
    setToast("");
    try {
      const config = await bridge<PublicConfig>("config.saveBasics", {
        requestProxyEnabled: enabled,
        requestProxyPort: snapshot?.config.requestProxyPort,
      });
      setSnapshot((current) => (current ? { ...current, config } : current));
      setToast("请求监控代理已保存，重启应用后生效。");
    } catch (err) {
      setProxyEnabled(snapshot?.config.requestProxyEnabled ?? false);
      setError(err instanceof Error ? err.message : "保存失败");
    } finally {
      setSaving(false);
    }
  }

  async function saveStartupSetting(next: Partial<Pick<PublicConfig, "startWithWindows" | "startMinimizedToTray">>) {
    const previousStartWithWindows = startWithWindows;
    const previousStartMinimizedToTray = startMinimizedToTray;
    const payload = {
      startWithWindows,
      startMinimizedToTray,
      ...next,
    };

    setStartWithWindows(payload.startWithWindows);
    setStartMinimizedToTray(payload.startMinimizedToTray);
    setSaving(true);
    setError("");
    setToast("");
    try {
      const config = await bridge<PublicConfig>("startup.save", payload);
      setSnapshot((current) => (current ? { ...current, config } : current));
      setToast("启动设置已保存。");
    } catch (err) {
      setStartWithWindows(previousStartWithWindows);
      setStartMinimizedToTray(previousStartMinimizedToTray);
      setError(err instanceof Error ? err.message : "保存失败");
    } finally {
      setSaving(false);
    }
  }

  async function setMountedMcpEnabled(name: string, enabled: boolean) {
    setSaving(true);
    setError("");
    setToast("");
    try {
      const next = await bridge<MountedMcpList>("mcp.setEnabled", { name, enabled });
      setMountedMcps(next.servers);
      setToast(enabled ? "MCP 已启用。" : "MCP 已停用。");
    } catch (err) {
      setError(err instanceof Error ? err.message : "保存失败");
    } finally {
      setSaving(false);
    }
  }

  async function refreshMountedMcp(name: string) {
    setSaving(true);
    setError("");
    setToast("");
    try {
      const next = await bridge<MountedMcpList>("mcp.refresh", { name });
      setMountedMcps(next.servers);
      setToast("工具列表已刷新。");
    } catch (err) {
      setError(err instanceof Error ? err.message : "刷新 MCP 失败");
    } finally {
      setSaving(false);
    }
  }

  const config = snapshot?.config;
  const sections: Array<{ key: SectionKey; label: string; icon: React.ComponentType<{ size?: number; strokeWidth?: number }> }> = [
    { key: "overview", label: "总览", icon: Activity },
    { key: "browser", label: "浏览器", icon: Globe2 },
    { key: "mcp", label: "MCP", icon: Plug },
    { key: "proxy", label: "代理", icon: Network },
    { key: "debug", label: "本地调试", icon: Bug },
    { key: "logs", label: "日志", icon: TerminalSquare },
  ];

  return (
    <main className="settings-shell">
      <aside className="sidebar">
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
        {active !== "browser" && active !== "mcp" && (
          <PageHeader
            title={sections.find((item) => item.key === active)?.label ?? ""}
            subtitle={subtitle(active)}
            loading={loading}
            onRefresh={load}
          />
        )}

        {error && <div className="error-banner">{error}</div>}
        {toast && <div className="toast-banner">{toast}</div>}

        {!snapshot || !config ? (
          <div className="empty-state">{loading ? "正在读取设置..." : "暂无设置数据"}</div>
        ) : (
          <>
            {active === "overview" && (
              <Overview
                snapshot={snapshot}
                saving={saving}
                startWithWindows={startWithWindows}
                startMinimizedToTray={startMinimizedToTray}
                onStartupSettingChange={saveStartupSetting}
              />
            )}
            {active === "browser" && (
              <BrowserSettings
                config={config}
                loading={loading}
                saving={saving}
                onRefresh={load}
                onRestartApplication={exitApplication}
                onSwitchProfile={switchProfile}
                onSaveProfile={saveProfile}
                onDeleteProfile={deleteProfile}
              />
            )}
            {active === "mcp" && (
              <McpSettings
                servers={mountedMcps}
                loading={mcpLoading}
                saving={saving}
                onRefresh={() => void loadMountedMcps(true)}
                onSetEnabled={setMountedMcpEnabled}
                onRefreshServer={refreshMountedMcp}
              />
            )}
            {active === "proxy" && (
              <ProxySettings
                config={config}
                proxyEnabled={proxyEnabled}
                saving={saving}
                onProxyEnabledChange={saveProxyEnabled}
                onRestartApplication={exitApplication}
              />
            )}
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

function Overview({
  snapshot,
  saving,
  startWithWindows,
  startMinimizedToTray,
  onStartupSettingChange,
}: {
  snapshot: Snapshot;
  saving: boolean;
  startWithWindows: boolean;
  startMinimizedToTray: boolean;
  onStartupSettingChange: (next: Partial<Pick<PublicConfig, "startWithWindows" | "startMinimizedToTray">>) => Promise<void>;
}) {
  const [secretVisible, setSecretVisible] = React.useState(false);
  const ownerPassword = snapshot.ownerPassword || "";
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
      <SettingRow title="OAuth 密钥" description={ownerPassword ? (secretVisible ? ownerPassword : maskSecret(ownerPassword)) : "未生成"}>
        <IconButton
          label={secretVisible ? "隐藏 OAuth 密钥" : "显示 OAuth 密钥"}
          disabled={!ownerPassword}
          onClick={() => setSecretVisible((value) => !value)}
        >
          {secretVisible ? <EyeOff size={14} strokeWidth={1.4} aria-hidden="true" /> : <Eye size={14} strokeWidth={1.4} aria-hidden="true" />}
        </IconButton>
      </SettingRow>
      <SettingRow title="开机启动" description="登录 Windows 后自动启动当前版本，保存时会清理旧版本启动项。">
        <Switch checked={startWithWindows} onChange={(value) => void onStartupSettingChange({ startWithWindows: value })} label="开机启动" disabled={saving} />
      </SettingRow>
      <SettingRow title="启动时最小化到托盘" description="启动后只保留托盘图标，不自动打开 ChatGPT 主窗口。">
        <Switch
          checked={startMinimizedToTray}
          onChange={(value) => void onStartupSettingChange({ startMinimizedToTray: value })}
          label="启动时最小化到托盘"
          disabled={saving}
        />
      </SettingRow>
      <SettingRow title="上次检查" description={new Date(snapshot.checkedAt).toLocaleString()} />
    </div>
  );
}

function maskSecret(value: string) {
  if (!value) return "";
  if (value.length <= 8) return "••••••••";
  return `${value.slice(0, 4)}${"•".repeat(Math.min(18, value.length - 8))}${value.slice(-4)}`;
}

function PageHeader({
  title,
  subtitle,
  loading,
  onRefresh,
  refreshLabel = "刷新",
}: {
  title: string;
  subtitle: string;
  loading: boolean;
  onRefresh: () => void;
  refreshLabel?: string;
}) {
  return (
    <header className="content-header">
      <div>
        <h1 className="title-row">
          <Settings size={16} strokeWidth={1.4} aria-hidden="true" />
          <span>{title}</span>
        </h1>
        <p>{subtitle}</p>
      </div>
      <button className="ghost-button" onClick={onRefresh} disabled={loading} type="button">
        <RefreshCw className={loading ? "spin" : undefined} size={14} strokeWidth={1.4} />
        {refreshLabel}
      </button>
    </header>
  );
}

type BrowserView = "list" | "create" | "edit" | "info";

type ProfileDraft = {
  id?: string;
  name: string;
  proxyServer: string;
  language: string;
  userDataFolder?: string;
};

function BrowserSettings({
  config,
  loading,
  saving,
  onRefresh,
  onRestartApplication,
  onSwitchProfile,
  onSaveProfile,
  onDeleteProfile,
}: {
  config: PublicConfig;
  loading: boolean;
  saving: boolean;
  onRefresh: () => void;
  onRestartApplication: () => Promise<void>;
  onSwitchProfile: (id: string) => Promise<void>;
  onSaveProfile: (profile: ProfileDraft) => Promise<void>;
  onDeleteProfile: (id: string) => Promise<void>;
}) {
  const [view, setView] = React.useState<BrowserView>("list");
  const [selectedProfile, setSelectedProfile] = React.useState<PublicProfile | null>(null);
  const [reverse, setReverse] = React.useState(false);

  function openProfile(nextView: Exclude<BrowserView, "list">, profile: PublicProfile | null = null) {
    setSelectedProfile(profile);
    setReverse(false);
    setView(nextView);
  }

  function backToList() {
    setReverse(true);
    setView("list");
  }

  async function saveDraft(draft: ProfileDraft) {
    await onSaveProfile(draft);
    backToList();
  }

  const activeProfile = config.profiles.find((profile) => profile.id === config.activeProfileId);
  return (
    <div className="settings-panel browser-panel">
      <div className={view === "list" ? `browser-view ${reverse ? "slide-in-left" : ""}` : "browser-view slide-out-left"}>
        {view === "list" && (
          <ProfileListView
            profiles={config.profiles}
            activeProfileId={config.activeProfileId}
            loading={loading}
            saving={saving}
            onRefresh={onRefresh}
            onRestartApplication={onRestartApplication}
            onCreate={() => openProfile("create")}
            onInfo={(profile) => openProfile("info", profile)}
            onEdit={(profile) => openProfile("edit", profile)}
            onSwitchProfile={onSwitchProfile}
            onDeleteProfile={onDeleteProfile}
          />
        )}
      </div>
      {view !== "list" && (
        <div className="browser-view slide-in-right">
          {view === "info" && selectedProfile && (
            <ProfileInfoView
              profile={selectedProfile}
              active={selectedProfile.id === config.activeProfileId}
              onBack={backToList}
              onEdit={() => openProfile("edit", selectedProfile)}
            />
          )}
          {view !== "info" && (
            <ProfileEditorView
              profile={view === "edit" ? selectedProfile : null}
              activeProfile={activeProfile}
              saving={saving}
              onBack={backToList}
              onSave={saveDraft}
            />
          )}
        </div>
      )}
    </div>
  );
}

function ProfileListView({
  profiles,
  activeProfileId,
  loading,
  saving,
  onRefresh,
  onRestartApplication,
  onCreate,
  onInfo,
  onEdit,
  onSwitchProfile,
  onDeleteProfile,
}: {
  profiles: PublicProfile[];
  activeProfileId: string;
  loading: boolean;
  saving: boolean;
  onRefresh: () => void;
  onRestartApplication: () => Promise<void>;
  onCreate: () => void;
  onInfo: (profile: PublicProfile) => void;
  onEdit: (profile: PublicProfile) => void;
  onSwitchProfile: (id: string) => Promise<void>;
  onDeleteProfile: (id: string) => Promise<void>;
}) {
  return (
    <>
      <PageHeader title="浏览器" subtitle={subtitle("browser")} loading={loading} onRefresh={onRefresh} />
      <div className="browser-toolbar">
        <div>
          <div className="toolbar-title">Profile</div>
          <div className="toolbar-subtitle">管理 ChatGPT WebView 的用户数据目录和出站代理。</div>
        </div>
        <button className="primary-button" type="button" onClick={onCreate}>
          <Plus size={14} strokeWidth={1.4} aria-hidden="true" />
          新增
        </button>
      </div>

      <div className="profile-list" aria-label="浏览器 Profile 列表">
        {profiles.map((profile) => {
          const active = profile.id === activeProfileId;
          return (
            <div className="profile-row" key={profile.id}>
              <div className="profile-main">
                <div className="profile-name-line">
                  <span className="profile-name">{profile.name}</span>
                  {active && <StatusPill ok okText="当前" />}
                  {profile.proxyConfigured ? <StatusPill ok okText="代理" /> : <StatusPill ok={false} failText="系统代理" />}
                </div>
                <div className="profile-meta">{profile.userDataFolder}</div>
                <div className="profile-meta">{profile.proxyConfigured ? profile.proxyServer : "未设置浏览器代理，将使用系统代理"}</div>
              </div>
              <div className="profile-actions">
                {!active && (
                  <button className="secondary-button" type="button" disabled={saving} onClick={() => void onSwitchProfile(profile.id)}>
                    切换
                  </button>
                )}
                <IconButton label="信息" onClick={() => onInfo(profile)}>
                  <Info size={14} strokeWidth={1.4} aria-hidden="true" />
                </IconButton>
                <IconButton label="修改" onClick={() => onEdit(profile)}>
                  <Pencil size={14} strokeWidth={1.4} aria-hidden="true" />
                </IconButton>
                <IconButton label="删除" disabled={profiles.length <= 1 || saving} danger onClick={() => void onDeleteProfile(profile.id)}>
                  <Trash2 size={14} strokeWidth={1.4} aria-hidden="true" />
                </IconButton>
              </div>
            </div>
          );
        })}
      </div>
      <RestartNotice
        text="切换或修改当前 Profile 后，需要重启应用才会使用新的浏览器配置。"
        buttonText="重启应用"
        saving={saving}
        onRestart={onRestartApplication}
      />
    </>
  );
}

function ProfileInfoView({
  profile,
  active,
  onBack,
  onEdit,
}: {
  profile: PublicProfile;
  active: boolean;
  onBack: () => void;
  onEdit: () => void;
}) {
  return (
    <>
      <SubpageHeader title="Profile 信息" onBack={onBack}>
        <button className="secondary-button" type="button" onClick={onEdit}>
          <Pencil size={14} strokeWidth={1.4} aria-hidden="true" />
          修改
        </button>
      </SubpageHeader>
      <SettingRow title="名称" description={profile.name}>
        {active && <StatusPill ok okText="当前" />}
      </SettingRow>
      <SettingRow title="ID" description={profile.id} />
      <SettingRow title="用户数据目录" description={profile.userDataFolder} />
      <SettingRow title="语言" description={profile.language || "zh-CN"} />
      <SettingRow title="浏览器代理" description={profile.proxyServer || "未设置，将使用系统代理"}>
        <StatusPill ok={profile.proxyConfigured} okText="已配置" failText="系统代理" />
      </SettingRow>
    </>
  );
}

function ProfileEditorView({
  profile,
  activeProfile,
  saving,
  onBack,
  onSave,
}: {
  profile: PublicProfile | null;
  activeProfile?: PublicProfile;
  saving: boolean;
  onBack: () => void;
  onSave: (profile: ProfileDraft) => Promise<void>;
}) {
  const [name, setName] = React.useState(profile?.name ?? "新 Profile");
  const [language, setLanguage] = React.useState(profile?.language ?? activeProfile?.language ?? "zh-CN");
  const [proxyServer, setProxyServer] = React.useState(profile?.proxyServer ?? "");
  const [localError, setLocalError] = React.useState("");
  const isEdit = Boolean(profile);

  async function submit() {
    setLocalError("");
    const trimmedName = name.trim();
    if (!trimmedName) {
      setLocalError("Profile 名称不能为空。");
      return;
    }

    try {
      await onSave({
        id: profile?.id,
        name: trimmedName,
        language: language.trim() || "zh-CN",
        proxyServer: proxyServer.trim(),
        userDataFolder: profile?.userDataFolder,
      });
    } catch (err) {
      setLocalError(err instanceof Error ? err.message : "保存失败");
    }
  }

  return (
    <>
      <SubpageHeader title={isEdit ? "修改 Profile" : "新增 Profile"} onBack={onBack} />
      {localError && <div className="inline-error">{localError}</div>}
      <EditRow label="名称">
        <input className="text-input" value={name} onChange={(event) => setName(event.target.value)} aria-label="Profile 名称" />
      </EditRow>
      <EditRow label="语言">
        <input className="text-input compact" value={language} onChange={(event) => setLanguage(event.target.value)} aria-label="语言" />
      </EditRow>
      <EditRow label="代理">
        <input
          className="text-input wide"
          value={proxyServer}
          onChange={(event) => setProxyServer(event.target.value)}
          placeholder="socks5://127.0.0.1:7890"
          aria-label="浏览器代理"
        />
      </EditRow>
      <div className="panel-footer">
        <p>{proxyServer.trim() ? "保存后需要重启应用才会使用新的代理。" : "不填写代理时会使用系统代理。"}</p>
        <button className="primary-button" type="button" onClick={() => void submit()} disabled={saving}>
          {saving ? <Loader2 className="spin" size={14} strokeWidth={1.4} /> : <CheckCircle2 size={14} strokeWidth={1.4} />}
          {saving ? "保存中" : "保存"}
        </button>
      </div>
    </>
  );
}

type McpView = "list" | "tools";

function McpSettings({
  servers,
  loading,
  saving,
  onRefresh,
  onSetEnabled,
  onRefreshServer,
}: {
  servers: MountedMcpServer[];
  loading: boolean;
  saving: boolean;
  onRefresh: () => void;
  onSetEnabled: (name: string, enabled: boolean) => Promise<void>;
  onRefreshServer: (name: string) => Promise<void>;
}) {
  const [view, setView] = React.useState<McpView>("list");
  const [selectedName, setSelectedName] = React.useState("");
  const [refreshingServer, setRefreshingServer] = React.useState("");
  const [reverse, setReverse] = React.useState(false);
  const selected = servers.find((server) => server.name === selectedName) ?? null;

  function openTools(server: MountedMcpServer) {
    setSelectedName(server.name);
    setReverse(false);
    setView("tools");
  }

  function backToList() {
    setReverse(true);
    setView("list");
  }

  async function refreshServer(name: string) {
    setRefreshingServer(name);
    try {
      await onRefreshServer(name);
    } finally {
      setRefreshingServer("");
    }
  }

  return (
    <div className="settings-panel browser-panel">
      <div className={view === "list" ? `browser-view ${reverse ? "slide-in-left" : ""}` : "browser-view slide-out-left"}>
        {view === "list" && (
          <McpListView
            servers={servers}
            loading={loading}
            saving={saving}
            onRefresh={onRefresh}
            onOpenTools={openTools}
            onSetEnabled={onSetEnabled}
            onRefreshServer={refreshServer}
            refreshingServer={refreshingServer}
          />
        )}
      </div>
      {view === "tools" && selected && (
        <div className="browser-view slide-in-right">
          <McpToolsView server={selected} saving={saving} onBack={backToList} onRefreshServer={refreshServer} />
        </div>
      )}
    </div>
  );
}

function McpListView({
  servers,
  loading,
  saving,
  onRefresh,
  onOpenTools,
  onSetEnabled,
  onRefreshServer,
  refreshingServer,
}: {
  servers: MountedMcpServer[];
  loading: boolean;
  saving: boolean;
  onRefresh: () => void;
  onOpenTools: (server: MountedMcpServer) => void;
  onSetEnabled: (name: string, enabled: boolean) => Promise<void>;
  onRefreshServer: (name: string) => Promise<void>;
  refreshingServer: string;
}) {
  return (
    <>
      <PageHeader title="MCP" subtitle={subtitle("mcp")} loading={loading} onRefresh={onRefresh} refreshLabel="扫描" />

      <div className="profile-list" aria-label="本机 MCP 列表">
        {servers.length === 0 && <div className="empty-state">没有在 Codex 配置中发现 MCP。</div>}
        {servers.map((server) => {
          const refreshing = refreshingServer === server.name;
          return (
            <div className="profile-row mcp-row" key={server.name}>
              <div className="profile-main">
                <div className="profile-name-line">
                  <span className="profile-name">{server.name}</span>
                  <span className="mcp-tool-count">{server.toolCount} 工具</span>
                </div>
                <div className="profile-meta">{server.description || server.command}</div>
                <div className="profile-meta">{server.command}</div>
              </div>
              <div className="mcp-actions">
                <IconButton label={`刷新 ${server.name} 工具`} disabled={saving || refreshing} onClick={() => void onRefreshServer(server.name)}>
                  <RefreshCw className={refreshing ? "spin" : undefined} size={14} strokeWidth={1.4} aria-hidden="true" />
                </IconButton>
                <IconButton label="设置" onClick={() => onOpenTools(server)}>
                  <Settings size={14} strokeWidth={1.4} aria-hidden="true" />
                </IconButton>
                <Switch checked={server.enabled} onChange={(value) => void onSetEnabled(server.name, value)} label={`切换 ${server.name}`} disabled={saving} />
              </div>
            </div>
          );
        })}
      </div>
    </>
  );
}

function McpToolsView({
  server,
  saving,
  onBack,
  onRefreshServer,
}: {
  server: MountedMcpServer;
  saving: boolean;
  onBack: () => void;
  onRefreshServer: (name: string) => Promise<void>;
}) {
  return (
    <>
      <SubpageHeader title={`${server.name} 工具`} onBack={onBack}>
        <button className="secondary-button" type="button" disabled={saving} onClick={() => void onRefreshServer(server.name)}>
          {saving ? <Loader2 className="spin" size={14} strokeWidth={1.4} /> : <RefreshCw size={14} strokeWidth={1.4} />}
          刷新工具
        </button>
      </SubpageHeader>
      <SettingRow title="说明" description={server.description || "暂无说明"} />
      <SettingRow title="工具" description={server.refreshedAt ? `${server.toolCount} 个，上次刷新：${new Date(server.refreshedAt).toLocaleString()}` : "尚未刷新工具列表"} />
      <div className="tool-list" aria-label={`${server.name} 工具列表`}>
        {server.tools.length === 0 && <div className="empty-state">暂无工具缓存，点击刷新工具读取该 MCP 的 tools/list。</div>}
        {server.tools.map((tool) => (
          <div className="tool-row" key={tool.name}>
            <div className="tool-name">{tool.name}</div>
            <div className="tool-description">{tool.description || "暂无描述"}</div>
          </div>
        ))}
      </div>
    </>
  );
}

function SubpageHeader({ title, onBack, children }: { title: string; onBack: () => void; children?: React.ReactNode }) {
  return (
    <div className="subpage-header">
      <button className="back-button" type="button" onClick={onBack} aria-label="返回">
        <ArrowLeft size={15} strokeWidth={1.45} aria-hidden="true" />
      </button>
      <div className="subpage-title">{title}</div>
      <div className="subpage-actions">{children}</div>
    </div>
  );
}

function EditRow({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="edit-row">
      <label>{label}</label>
      <div className="edit-control">{children}</div>
    </div>
  );
}

function IconButton({
  label,
  children,
  onClick,
  disabled,
  danger,
}: {
  label: string;
  children: React.ReactNode;
  onClick: () => void;
  disabled?: boolean;
  danger?: boolean;
}) {
  return (
    <button className={danger ? "icon-button danger" : "icon-button"} type="button" onClick={onClick} disabled={disabled} aria-label={label} title={label}>
      {children}
    </button>
  );
}

function ProxySettings({
  config,
  proxyEnabled,
  saving,
  onProxyEnabledChange,
  onRestartApplication,
}: {
  config: PublicConfig;
  proxyEnabled: boolean;
  saving: boolean;
  onProxyEnabledChange: (value: boolean) => Promise<void>;
  onRestartApplication: () => Promise<void>;
}) {
  return (
    <div className="settings-panel">
      <SettingRow title="请求监控代理" description={config.requestProxyEnabled ? "DevSpace MCP 请求会经过本地监控代理。" : "当前直连 DevSpace 服务。"}>
        <Switch checked={proxyEnabled} onChange={(value) => void onProxyEnabledChange(value)} label="切换请求监控代理" disabled={saving} />
      </SettingRow>
      <SettingRow title="监控代理端口" description={`127.0.0.1:${config.requestProxyPort}`} />
      <SettingRow title="DevSpace 端口" description={`127.0.0.1:${config.devSpacePort}`} />
      <SettingRow title="公网地址" description={config.publicBaseUrl} />
      <RestartNotice
        text="请求监控代理切换后，需要重启应用才会使用新的转发链路。"
        buttonText="重启应用"
        saving={saving}
        onRestart={onRestartApplication}
      />
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
          {saving ? "保存中" : "保存"}
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

function RestartNotice({
  text,
  buttonText,
  saving,
  onRestart,
}: {
  text: string;
  buttonText: string;
  saving: boolean;
  onRestart: () => Promise<void>;
}) {
  return (
    <div className="panel-footer restart-footer">
      <p>{text}</p>
      <button className="secondary-button" type="button" disabled={saving} onClick={() => void onRestart()}>
        {saving ? <Loader2 className="spin" size={14} strokeWidth={1.4} /> : <RotateCw size={14} strokeWidth={1.4} />}
        {saving ? "处理中" : buttonText}
      </button>
    </div>
  );
}

function Switch({
  checked,
  onChange,
  label,
  disabled,
}: {
  checked: boolean;
  onChange: (value: boolean) => void;
  label: string;
  disabled?: boolean;
}) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      aria-label={label}
      className={checked ? "switch checked" : "switch"}
      disabled={disabled}
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
      return "管理 ChatGPT WebView 使用的 Profile、用户数据目录和浏览器代理。";
    case "mcp":
      return "选择要通过 DevSpaceManager 代理挂载给 ChatGPT 的本机 MCP。";
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
