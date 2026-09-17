namespace CodexEnvAdapter;

sealed class MainForm : Form
{
    static readonly string[] CommonTimezones =
    [
        "America/Los_Angeles",
        "America/New_York",
        "America/Chicago",
        "America/Denver",
        "Europe/London",
        "Europe/Berlin",
        "Europe/Paris",
        "Asia/Tokyo",
        "Asia/Singapore",
        "Asia/Hong_Kong",
        "UTC"
    ];

    readonly Label _ipValue = MakeValue();
    readonly Label _locationValue = MakeValue();
    readonly Label _orgValue = MakeValue();
    readonly Label _nodeTzValue = MakeValue();
    readonly Label _systemTzValue = MakeValue();
    readonly Label _proxyValue = MakeValue();
    readonly Label _appValue = MakeValue();
    readonly RadioButton _autoTz = new() { Text = "自动使用节点时区", AutoSize = true, Checked = true };
    readonly RadioButton _manualTz = new() { Text = "手动选择", AutoSize = true };
    readonly ComboBox _timezoneBox = new()
    {
        DropDownStyle = ComboBoxStyle.DropDown,
        Width = 280
    };
    readonly CheckBox _restartBox = new()
    {
        Text = "启动前结束已运行的 ChatGPT（TZ 只在启动时生效）",
        AutoSize = true,
        Checked = true
    };
    readonly Button _refreshButton = new() { Text = "刷新探测", Width = 120, Height = 36 };
    readonly Button _shortcutButton = new() { Text = "创建桌面快捷方式", Width = 160, Height = 36 };
    readonly Button _launchButton = new() { Text = "保存并启动 ChatGPT", Width = 220, Height = 42 };
    readonly TextBox _logBox = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Height = 150,
        Dock = DockStyle.Fill
    };

    NodeInfo? _node;
    ChatGptInstall? _app;
    bool _busy;
    readonly CancellationTokenSource _cts = new();

    public MainForm()
    {
        Text = "Codex环境适配启动器";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(580, 720);
        Size = new Size(620, 780);
        Font = new Font("Microsoft YaHei UI", 9F);

        _timezoneBox.Items.AddRange(CommonTimezones);
        _autoTz.CheckedChanged += (_, _) => _timezoneBox.Enabled = _manualTz.Checked;
        _manualTz.CheckedChanged += (_, _) => _timezoneBox.Enabled = _manualTz.Checked;
        _timezoneBox.Enabled = false;

        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _launchButton.Click += async (_, _) => await LaunchAsync();
        _shortcutButton.Click += (_, _) => CreateShortcut();
        FormClosing += (_, _) =>
        {
            try { _cts.Cancel(); } catch { }
        };

        Controls.Add(BuildLayout());
        Load += async (_, _) =>
        {
            LoadSavedSettings();
            await RefreshAsync();
        };
    }

    Control BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 1,
            RowCount = 6
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var intro = new Label
        {
            AutoSize = true,
            Text = "只给 ChatGPT / Codex 注入 TZ，不改系统时区。社区共识：多 IP、多设备同时登录也可能降智，请固定一个出口、一台主力设备。",
            MaximumSize = new Size(540, 0)
        };

        root.Controls.Add(intro, 0, 0);
        root.Controls.Add(MakeGroup("当前出口",
            Row("出口 IP", _ipValue),
            Row("节点位置", _locationValue),
            Row("节点 ISP", _orgValue),
            Row("节点时区", _nodeTzValue)), 0, 1);
        root.Controls.Add(MakeGroup("本机",
            Row("系统时区", _systemTzValue),
            Row("用户代理", _proxyValue),
            Row("Codex 应用", _appValue)), 0, 2);

        var tzPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Dock = DockStyle.Fill
        };
        tzPanel.Controls.Add(_autoTz);
        tzPanel.Controls.Add(_manualTz);
        tzPanel.Controls.Add(_timezoneBox);
        tzPanel.Controls.Add(_restartBox);
        root.Controls.Add(MakeGroup("注入时区", tzPanel), 0, 3);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 8, 0, 8)
        };
        _launchButton.Font = new Font(Font, FontStyle.Bold);
        buttons.Controls.Add(_launchButton);
        buttons.Controls.Add(_refreshButton);
        buttons.Controls.Add(_shortcutButton);
        root.Controls.Add(buttons, 0, 4);
        root.Controls.Add(MakeGroup("日志", _logBox), 0, 5);
        return root;
    }

    static GroupBox MakeGroup(string title, params Control[] children)
    {
        var box = new GroupBox
        {
            Text = title,
            AutoSize = true,
            Dock = DockStyle.Top,
            Padding = new Padding(10, 8, 10, 10)
        };
        var inner = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ColumnCount = 1
        };
        foreach (var child in children)
        {
            child.Dock = DockStyle.Top;
            inner.Controls.Add(child);
        }

        box.Controls.Add(inner);
        return box;
    }

    static TableLayoutPanel Row(string label, Control value)
    {
        var row = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 2,
            Margin = new Padding(0, 2, 0, 2)
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        value.Dock = DockStyle.Fill;
        row.Controls.Add(value, 1, 0);
        return row;
    }

    static Label MakeValue() => new()
    {
        AutoSize = true,
        Text = "…",
        Anchor = AnchorStyles.Left
    };

    void LoadSavedSettings()
    {
        var settings = SettingsStore.Load();
        _autoTz.Checked = settings.AutoTimezone;
        _manualTz.Checked = !settings.AutoTimezone;
        _restartBox.Checked = settings.RestartChatGpt;
        if (!string.IsNullOrWhiteSpace(settings.Timezone) && !_timezoneBox.Items.Contains(settings.Timezone))
        {
            _timezoneBox.Items.Insert(0, settings.Timezone);
        }

        if (!string.IsNullOrWhiteSpace(settings.Timezone))
        {
            _timezoneBox.Text = settings.Timezone;
        }
    }

    async Task RefreshAsync()
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true);
        try
        {
            Log("正在探测当前出口节点…");
            _systemTzValue.Text = WindowsEnv.GetSystemTimezoneDisplay() + "（不修改）";
            var proxy = WindowsEnv.GetProxyStatus();
            _proxyValue.Text = proxy.UserProxyEnable
                ? "已开启  " + (string.IsNullOrWhiteSpace(proxy.UserProxyServer) ? "(未填写地址)" : proxy.UserProxyServer)
                : "未开启";

            try
            {
                _app = await Task.Run(ChatGptLauncher.FindInstall, _cts.Token);
                if (IsDisposed) return;
                _appValue.Text = $"已安装 {_app.Version}，正在运行 {_app.RunningCount} 个进程";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (IsDisposed) return;
                _app = null;
                _appValue.Text = ex.Message;
            }

            _node = await NodeProbe.DetectAsync(_cts.Token);
            if (IsDisposed) return;
            _ipValue.Text = _node.PublicIp;
            _locationValue.Text = _node.LocationText;
            _orgValue.Text = string.IsNullOrWhiteSpace(_node.Org) ? "(未知)" : _node.Org;
            _nodeTzValue.Text = _node.Timezone;
            if (_autoTz.Checked)
            {
                _timezoneBox.Text = _node.Timezone;
            }

            Log($"出口 {_node.PublicIp} / {_node.LocationText} / TZ={_node.Timezone}");
            if (!proxy.UserProxyEnable)
            {
                Log("警告：当前看不到用户代理。若 GPT 需要走节点，请先打开 Clash / 系统代理。");
            }
        }
        catch (OperationCanceledException)
        {
            // Form is closing.
        }
        catch (Exception ex)
        {
            if (IsDisposed) return;
            Log("探测失败：" + ex.Message);
            MessageBox.Show(this, ex.Message, "探测失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            if (!IsDisposed)
            {
                SetBusy(false);
            }
        }
    }

    async Task LaunchAsync()
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true);
        try
        {
            if (_node is null)
            {
                _node = await NodeProbe.DetectAsync(_cts.Token);
                if (IsDisposed) return;
            }

            var timezone = (_autoTz.Checked ? _node.Timezone : _timezoneBox.Text).Trim();
            if (string.IsNullOrWhiteSpace(timezone))
            {
                throw new InvalidOperationException("请填写 IANA 时区，例如 America/Los_Angeles。");
            }

            var app = _app ?? await Task.Run(ChatGptLauncher.FindInstall, _cts.Token);
            if (IsDisposed) return;
            _app = app;

            var settings = new AppSettings
            {
                Timezone = timezone,
                AutoTimezone = _autoTz.Checked,
                RestartChatGpt = _restartBox.Checked,
                PublicIp = _node.PublicIp,
                City = _node.City,
                Region = _node.Region,
                Country = _node.Country,
                Org = _node.Org,
                Source = _node.Source,
                SystemTz = WindowsEnv.GetSystemTimezoneId()
            };
            SettingsStore.Save(settings);

            var running = ChatGptLauncher.GetRunningCount();
            if (running > 0)
            {
                if (!_restartBox.Checked)
                {
                    throw new InvalidOperationException("ChatGPT 正在运行。TZ 只在进程启动时生效，请先退出或勾选重启。");
                }

                Log("正在结束已运行的 ChatGPT…");
                await Task.Run(() => ChatGptLauncher.StopRunning(TimeSpan.FromSeconds(15)), _cts.Token);
                if (IsDisposed) return;
            }

            Log($"正在用 TZ={timezone} 启动 ChatGPT…");
            var proc = ChatGptLauncher.StartWithTimezone(app.Exe, timezone);
            await Task.Delay(1500, _cts.Token);
            if (IsDisposed) return;

            proc.Refresh();
            if (proc.HasExited)
            {
                throw new InvalidOperationException("ChatGPT 单实例仍在运行，带 TZ 的新进程没有留下来。请勾选重启后再试。");
            }

            if (ChatGptLauncher.GetRunningCount() == 0)
            {
                throw new InvalidOperationException("ChatGPT 启动后没有保持运行。");
            }

            Log($"已启动 ChatGPT（PID {proc.Id}）。Windows 系统时区未改动。");
        }
        catch (OperationCanceledException)
        {
            // Form is closing.
        }
        catch (Exception ex)
        {
            if (IsDisposed) return;
            Log("启动失败：" + ex.Message);
            MessageBox.Show(this, ex.Message, "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (!IsDisposed)
            {
                SetBusy(false);
            }
        }
    }

    void CreateShortcut()
    {
        try
        {
            var target = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(target) || !File.Exists(target))
            {
                throw new InvalidOperationException("找不到当前程序路径，无法创建快捷方式。");
            }

            ChatGptLauncher.CreateDesktopShortcut(target);
            Log("已创建桌面快捷方式：Codex环境适配启动器.lnk");
            MessageBox.Show(this, "桌面快捷方式已创建。", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log("创建快捷方式失败：" + ex.Message);
            MessageBox.Show(this, ex.Message, "快捷方式失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        _refreshButton.Enabled = !busy;
        _launchButton.Enabled = !busy;
        _shortcutButton.Enabled = !busy;
        UseWaitCursor = busy;
    }

    void Log(string message)
    {
        var line = DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine;
        _logBox.AppendText(line);
    }
}
