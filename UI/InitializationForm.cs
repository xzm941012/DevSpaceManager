using DevSpaceManager.Core;
using DevSpaceManager.Services;

namespace DevSpaceManager.UI;

internal sealed class InitializationForm : Form
{
    private readonly AppHost _app;
    private readonly ListView _checks = new();
    private readonly Label _status = new();
    private readonly Button _refreshButton;

    public InitializationForm(AppHost app)
    {
        _app = app;
        Text = "初始化 / 环境检查";
        Icon = NotifyIconFactory.Create(false);
        StartPosition = FormStartPosition.CenterParent;
        Width = 860;
        Height = 620;
        MinimumSize = new Size(760, 540);

        _refreshButton = Button("重新检查", async (_, _) => await RefreshChecksAsync());
        BuildUi();
        _ = RefreshChecksAsync();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 1,
            RowCount = 4
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 158));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

        root.Controls.Add(new Label
        {
            Text = "先检查缺什么，再点对应按钮安装或初始化。DevSpace 会安装到基础页当前选择的 Node/nvm 版本里。Cloudflare 推荐直接点“一键配置 Cloudflare”。",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        _checks.Dock = DockStyle.Fill;
        _checks.View = View.Details;
        _checks.FullRowSelect = true;
        _checks.GridLines = true;
        _checks.Columns.Add("项目", 180);
        _checks.Columns.Add("状态", 90);
        _checks.Columns.Add("说明", 520);
        root.Controls.Add(_checks, 0, 1);

        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 3 };
        for (var i = 0; i < 4; i++) actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        actions.Controls.Add(Button("安装 Git Bash", (_, _) => RunAction("安装 Git Bash", _app.Environment.InstallGit)), 0, 0);
        actions.Controls.Add(Button("安装 nvm", (_, _) => RunAction("安装 nvm", _app.Environment.InstallNvm)), 1, 0);
        actions.Controls.Add(Button("安装 Node", (_, _) => RunAction("安装 Node", _app.Environment.InstallSelectedNode)), 2, 0);
        actions.Controls.Add(Button("安装 cloudflared", (_, _) => RunAction("安装 cloudflared", _app.Environment.InstallCloudflared)), 3, 0);
        actions.Controls.Add(Button("安装 DevSpace", (_, _) => RunAction("安装 DevSpace", _app.Environment.InstallDevSpace)), 0, 1);
        actions.Controls.Add(Button("DevSpace 初始化", (_, _) => RunAction("DevSpace 初始化", _app.Environment.RunDevSpaceInit)), 2, 1);
        actions.Controls.Add(Button("Cloudflare 登录", (_, _) => RunAction("Cloudflare 登录", _app.Environment.RunCloudflaredLogin)), 3, 1);
        actions.Controls.Add(Button("一键配置 Cloudflare", (_, _) => RunAction("一键配置 Cloudflare", _app.Environment.SetupCloudflareTunnel)), 0, 2);
        root.Controls.Add(actions, 0, 2);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        footer.Controls.Add(_status, 0, 0);
        footer.Controls.Add(_refreshButton, 1, 0);
        footer.Controls.Add(Button("关闭", (_, _) => Close()), 2, 0);
        root.Controls.Add(footer, 0, 3);

        Controls.Add(root);
    }

    private async Task RefreshChecksAsync()
    {
        SetBusy(true, "正在检查环境...");
        try
        {
            var checks = await _app.Environment.CheckAsync();
            _checks.Items.Clear();
            foreach (var check in checks)
            {
                var item = new ListViewItem(check.Name);
                item.SubItems.Add(check.Ok ? "正常" : "缺失");
                item.SubItems.Add(check.Detail);
                item.ForeColor = check.Ok ? Color.FromArgb(24, 115, 82) : Color.FromArgb(170, 92, 22);
                _checks.Items.Add(item);
            }

            var okCount = checks.Count(check => check.Ok);
            _status.Text = $"检查完成：{okCount}/{checks.Count} 项正常。安装或初始化完成后请重新检查。";
        }
        catch (Exception ex)
        {
            _status.Text = "检查失败。";
            MessageBox.Show(ex.Message, "环境检查失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false, _status.Text);
        }
    }

    private void RunAction(string name, Action action)
    {
        try
        {
            action();
            _status.Text = $"已打开{name}窗口，完成后点击重新检查。";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SetBusy(bool busy, string text)
    {
        UseWaitCursor = busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        _refreshButton.Enabled = !busy;
        _status.Text = text;
    }

    private static Button Button(string text, EventHandler click)
    {
        var button = new Button { Text = text, Dock = DockStyle.Fill, Margin = new Padding(4), Height = 32 };
        button.Click += click;
        return button;
    }
}
