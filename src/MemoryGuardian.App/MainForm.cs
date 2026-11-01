using System.Diagnostics;
using Microsoft.Win32;

namespace MemoryGuardian.App;

public sealed class MainForm : Form
{
    private const string DisplayName = "说的道理 · 内存助手";
    private readonly MonitorHost host;
    private readonly NotifyIcon tray;
    private readonly DaoliMascot mascot = new() { Dock = DockStyle.Fill, Margin = new Padding(8, 4, 8, 4) };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 30_000 };
    private readonly Label percentage = new() { AutoSize = true, Font = new Font("Segoe UI", 34, FontStyle.Bold), ForeColor = Color.FromArgb(31, 57, 70) };
    private readonly Label details = new() { AutoSize = true };
    private readonly Label status = new() { AutoSize = false, Height = 58, Dock = DockStyle.Top };
    private readonly Label last = new() { AutoSize = false, Dock = DockStyle.Fill };
    private readonly ProgressBar bar = new() { Height = 18, Dock = DockStyle.Top };
    private readonly Button clean = new() { Text = "立即整理", Width = 145, Height = 42, BackColor = Color.FromArgb(20, 147, 120), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    private readonly Button pause = new() { Width = 145, Height = 42 };
    private readonly CheckBox startup = new() { Text = "登录后自动运行", AutoSize = true };
    private readonly ToolStripMenuItem trayPause = new();
    private readonly ToolStripMenuItem trayClean = new("立即整理一次");
    private readonly LocalControl control;
    private bool allowExit, exitQueued, firstVisibility = true;
    private readonly bool startHidden;
    private bool updating;

    public MainForm(bool background, bool readOnly, string? controlPipeName = null)
    {
        startHidden = background;
        host = new(readOnly);
        Text = DisplayName;
        Font = new Font("Microsoft YaHei UI", 10);
        BackColor = Color.White;
        ClientSize = new Size(700, 620);
        MinimumSize = SizeFromClientSize(new Size(700, 620));
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Icon = SystemIcons.Application;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(26), ColumnCount = 1, RowCount = 9 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 136));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 2, RowCount = 2 };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        heading.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        heading.Controls.Add(new Label { Text = "说的道理", Font = new Font(Font.FontFamily, 24, FontStyle.Bold), ForeColor = Color.FromArgb(20, 147, 120), Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft }, 0, 0);
        heading.Controls.Add(new Label { Text = "内存助手 · 手动整理 / 自动监测", ForeColor = Color.DimGray, Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) }, 0, 1);
        heading.Controls.Add(mascot, 1, 0);
        heading.SetRowSpan(mascot, 2);
        root.Controls.Add(heading, 0, 0);
        root.Controls.Add(percentage, 0, 1);
        root.Controls.Add(details, 0, 2);
        root.Controls.Add(bar, 0, 3);
        root.Controls.Add(status, 0, 4);
        root.Controls.Add(last, 0, 5);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        actions.Controls.Add(clean);
        actions.Controls.Add(pause);
        var hide = new Button { Text = "隐藏到托盘", Width = 125, Height = 42 };
        hide.Click += (_, _) => Hide();
        actions.Controls.Add(hide);
        root.Controls.Add(actions, 0, 6);
        root.Controls.Add(startup, 0, 7);
        root.Controls.Add(new Label { Text = "自动：每 30 秒检查，内存超过 70% 即整理一次。\n至少间隔 30 分钟；手动整理随时可用。", AutoSize = true, ForeColor = Color.DimGray }, 0, 8);
        Controls.Add(root);

        var menu = new ContextMenuStrip();
        menu.Items.Add("打开窗口", null, (_, _) => ShowWindow());
        trayClean.Click += async (_, _) => await ManualClean();
        menu.Items.Add(trayClean);
        trayPause.Click += (_, _) => host.SetPaused(!host.Paused);
        menu.Items.Add(trayPause);
        menu.Items.Add("打开记录", null, (_, _) => OpenRecords());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => RequestExit());
        tray = new NotifyIcon { Icon = SystemIcons.Information, Text = DisplayName, Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => ShowWindow();
        clean.Click += async (_, _) => await ManualClean();
        pause.Click += (_, _) => host.SetPaused(!host.Paused);
        startup.Checked = host.StartupEnabled;
        startup.Enabled = !readOnly;
        startup.CheckedChanged += (_, _) =>
        {
            if (updating) return;
            try { host.SetStartup(startup.Checked); }
            catch (Exception e) { MessageBox.Show(this, e.Message, "无法修改自启", MessageBoxButtons.OK, MessageBoxIcon.Warning); RefreshDisplay(); }
        };
        host.Changed += RefreshDisplay;
        timer.Tick += async (_, _) => await host.TickAsync();
        SystemEvents.PowerModeChanged += PowerChanged;
        _ = Handle;
        control = new LocalControl(Dispatch, controlPipeName);
        control.RequestsDrained += () =>
        {
            if (exitQueued && !IsDisposed) BeginInvoke(new Action(RequestExit));
        };
        _ = control.RunAsync();
        timer.Start();
        BeginInvoke(new Action(async () => await host.TickAsync()));
        RefreshDisplay();
    }

    protected override void SetVisibleCore(bool value)
    {
        if (firstVisibility) { firstVisibility = false; if (startHidden) value = false; }
        base.SetVisibleCore(value);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        mascot?.SetAnimationEnabled(Visible && WindowState != FormWindowState.Minimized);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        mascot?.SetAnimationEnabled(Visible && WindowState != FormWindowState.Minimized);
    }

    private Task<object> Dispatch(string command)
    {
        var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginInvoke(new Action(async () =>
        {
            try
            {
                object result;
                switch (command)
                {
                    case "clean": result = await host.CleanAsync("manual-cli"); break;
                    case "pause": host.SetPaused(true); result = host.Status(); break;
                    case "resume": host.SetPaused(false); result = host.Status(); break;
                    case "show": ShowWindow(); result = host.Status(); break;
                    case "stop": result = new { Success = true, Message = host.Busy ? "整理完成后退出" : "正在退出" }; exitQueued = true; break;
                    default: result = host.Status(); break;
                }
                completion.TrySetResult(result);
                if (exitQueued && !host.Busy)
                {
                    await Task.Delay(300);
                    RequestExit();
                }
            }
            catch (Exception e) { completion.TrySetResult(new { Success = false, Message = e.Message }); }
        }));
        return completion.Task;
    }

    private async Task ManualClean()
    {
        await host.CleanAsync("manual-button");
        if (host.LastError is not null) MessageBox.Show(this, host.LastError, "整理未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void RefreshDisplay()
    {
        if (IsDisposed) return;
        updating = true;
        var sample = host.Sample;
        mascot.IsCleaning = host.Busy;
        percentage.Text = sample is null ? "—" : $"{sample.UsedPercent:F1}%";
        details.Text = sample is null ? "正在读取内存…" : $"可用 {sample.AvailableBytes / 1073741824d:F2} GiB  /  共 {sample.TotalBytes / 1073741824d:F2} GiB";
        bar.Value = sample is null ? 0 : Math.Clamp((int)sample.UsedPercent, 0, 100);
        status.Text = exitQueued ? "本次整理完成后退出…" : host.StatusText;
        var result = host.LastResult;
        last.Text = host.Busy ? "正在整理，完成后显示本次结果…"
            : result is null ? host.LastAttempt is null ? "尚未整理" : $"上次整理尝试：{host.LastAttempt.Value.LocalDateTime:MM-dd HH:mm:ss}（本次启动暂无结果）"
            : $"{(result.Success ? "整理完成" : "整理失败")} · {result.Time.LocalDateTime:MM-dd HH:mm:ss} · {result.Message}";
        if (!host.Busy && result?.Before is not null && result.After is not null && result.AvailableChangeMiB is not null)
            last.Text += $"\n可用内存：{result.Before.AvailableBytes / 1073741824d:F2} GiB → {result.After.AvailableBytes / 1073741824d:F2} GiB" +
                $"\n观测变化：{result.AvailableChangeMiB.Value:+0.0;-0.0;0.0} MiB";
        pause.Text = trayPause.Text = host.Paused ? "继续自动整理" : "暂停自动整理";
        clean.Enabled = trayClean.Enabled = !host.Busy && !host.ReadOnly;
        startup.Checked = host.StartupEnabled;
        tray.Text = sample is null ? DisplayName : $"{DisplayName} · {sample.UsedPercent:F0}% · {(host.Paused ? "已暂停" : "监测中")}";
        updating = false;
        if (exitQueued && !host.Busy && !control.HasPendingRequests) BeginInvoke(new Action(RequestExit));
    }

    private void PowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode is PowerModes.Resume or PowerModes.Suspend && !IsDisposed)
            BeginInvoke(new Action(host.ResetObservation));
    }

    private void ShowWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    private void OpenRecords()
    {
        if (!host.ReadOnly) Process.Start(new ProcessStartInfo("explorer.exe", AppStorage.BaseDirectory) { UseShellExecute = true });
    }
    private void RequestExit()
    {
        if (host.Busy || control.HasPendingRequests) { exitQueued = true; RefreshDisplay(); return; }
        allowExit = true;
        Close();
    }
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!allowExit && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
        base.OnFormClosing(e);
    }
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        timer.Stop(); timer.Dispose();
        control.Dispose();
        SystemEvents.PowerModeChanged -= PowerChanged;
        host.Dispose();
        tray.Visible = false; tray.Dispose();
        base.OnFormClosed(e);
    }
}
