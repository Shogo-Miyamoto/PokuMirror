using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace iPhoneMirror;

// 通知領域 (タスクバー右下) のアイコンとして常駐し、全体をまとめる。
sealed class TrayContext : ApplicationContext
{
    readonly Settings _settings = Settings.Load();
    readonly Esp32Link _link = new();
    readonly UxPlayHost _uxplay = new();
    readonly MouseBridge _bridge;
    readonly NotifyIcon _tray;
    readonly ToolStripMenuItem _statusItem;
    readonly System.Windows.Forms.Timer _timer;
    SetupWizard? _wizard;
    StatusForm? _statusForm;
    Action? _balloonClick;

    // 警告を何度も出さないための記録
    bool _wasMirroring, _warnedUnpaired, _warnedNoBoard, _toldHowToStop;
    DateTime? _unpairedSince;

    public TrayContext(bool forceSetup)
    {
        var menu = new ContextMenuStrip();  // ここで UI スレッドの SynchronizationContext が用意される
        _statusItem = new ToolStripMenuItem("状態を確認中...") { Enabled = false };
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("状態を表示", null, (_, _) => ShowStatus());
        menu.Items.Add("セットアップ", null, (_, _) => ShowWizard(0));
        _mouseItem = new ToolStripMenuItem("PC のマウスで操作する（ESP32 を使う）") { Checked = _settings.MouseEnabled };
        _mouseItem.Click += (_, _) => SetMouseEnabled(!_settings.MouseEnabled);
        menu.Items.Add(_mouseItem);
        menu.Items.Add(BuildSensitivityMenu());
        menu.Items.Add("使い方", null, (_, _) => ShowHowTo());
        menu.Items.Add("記録 (ログ) を開く", null, (_, _) => OpenLog());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => ExitApp());

        _tray = new NotifyIcon
        {
            Icon = MakeIcon(),
            Text = "ぽくミラー",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowStatus();
        _tray.BalloonTipClicked += (_, _) => { var a = _balloonClick; _balloonClick = null; a?.Invoke(); };

        _link.Enabled = _settings.MouseEnabled;
        _bridge = new MouseBridge(_link, _settings);
        _bridge.Notice += message => Balloon(message, ToolTipIcon.Info);
        _bridge.CapturingChanged += capturing =>
        {
            if (capturing && !_toldHowToStop)
            {
                _toldHowToStop = true;
                Balloon("iPhone を操作中です。右クリックで PC のマウスに戻ります。Shift を押しながら動かすとゆっくり動きます。", ToolTipIcon.Info);
            }
        };

        _uxplay.Start();

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        if (!_settings.SetupDone || forceSetup) ShowWizard(0);
        else Balloon($"起動しました。iPhone の「画面ミラーリング」で「{UxPlayHost.ServerName}」を選んでください。", ToolTipIcon.Info);
    }

    readonly ToolStripMenuItem _mouseItem;

    // ESP32 を使うか (使わないときは、普通のミラーリングアプリとして動く)
    public void SetMouseEnabled(bool enabled)
    {
        if (!enabled) _bridge.StopCapture();
        _settings.MouseEnabled = enabled;
        _settings.Save();
        _link.Enabled = enabled;
        _mouseItem.Checked = enabled;
        Log.Write(enabled ? "マウス操作をオンにしました" : "マウス操作をオフにしました (ミラーリングのみ)");
    }

    ToolStripMenuItem BuildSensitivityMenu()
    {
        var root = new ToolStripMenuItem("マウスの速さ");
        foreach (var (label, value) in new[] { ("遅い", 1.0), ("ふつう", 1.5), ("速い", 2.0), ("とても速い", 3.0) })
        {
            var item = new ToolStripMenuItem(label) { Checked = Math.Abs(_settings.Sensitivity - value) < 0.01 };
            item.Click += (_, _) =>
            {
                _settings.Sensitivity = value;
                _settings.Save();
                foreach (ToolStripMenuItem i in root.DropDownItems) i.Checked = i == item;
            };
            root.DropDownItems.Add(item);
        }
        return root;
    }

    // ---- 状態の確認と警告 -------------------------------------------------------

    public string StatusText()
    {
        var board = _link.PortName is null ? "見つかりません (USB ケーブル / ドライバを確認)"
            : _link.OpenError is not null ? $"{_link.PortName} を開けません (他のソフトが使用中かもしれません)"
            : !_link.IsOpen ? $"{_link.PortName} に接続しています..."
            : _link.FirmwareResponding ? $"接続中 ({_link.PortName})" : $"{_link.PortName} から返事がありません (書き込みが必要かもしれません)";
        var phone = _link.PhoneConnected switch
        {
            true => "つながっています",
            false => "つながっていません (iPhone の Bluetooth 設定を確認)",
            _ => "不明",
        };
        var mirror = !UxPlayHost.Available ? "受信ソフトが見つかりません"
            : UxPlayHost.MirrorWindow() != IntPtr.Zero ? "受信中"
            : _uxplay.Running ? $"待機中 (iPhone の画面ミラーリングで「{UxPlayHost.ServerName}」を選ぶ)" : "受信ソフトを起動しています...";
        if (!_settings.MouseEnabled)
            return $"画面ミラーリング: {mirror}\nPC のマウスで操作: オフ（ミラーリングのみ。アイコンのメニューでオンにできます）";
        return $"ESP32 ボード: {board}\niPhone とのマウス接続: {phone}\n画面ミラーリング: {mirror}";
    }

    void Tick()
    {
        var mirroring = UxPlayHost.MirrorWindow() != IntPtr.Zero;
        var ready = _link.IsOpen && _link.PhoneConnected == true;
        _statusItem.Text = !_settings.MouseEnabled ? (mirroring ? "ミラーリング中（マウス操作はオフ）" : "待機中（iPhone で画面ミラーリングを開始）")
            : !_link.IsOpen ? "ESP32 ボードが見つかりません"
            : _link.PhoneConnected != true ? "マウスが iPhone とつながっていません"
            : mirroring ? (_bridge.Capturing ? "iPhone を操作中" : "準備OK (ミラー画面をクリックで操作)")
            : "準備OK (iPhone で画面ミラーリングを開始)";
        var tip = $"ぽくミラー - {_statusItem.Text}";
        _tray.Text = tip.Length > 63 ? tip[..63] : tip;
        _statusForm?.UpdateStatus(StatusText());

        if (mirroring && !_wasMirroring) { _warnedUnpaired = false; _warnedNoBoard = false; }
        _wasMirroring = mirroring;
        if (!mirroring || !_settings.MouseEnabled || _wizard is { Visible: true }) { _unpairedSince = null; return; }

        // ミラーリング中なのに、マウスが使えない状態なら知らせる
        if (_link.PortName is null)
        {
            if (!_warnedNoBoard)
            {
                _warnedNoBoard = true;
                Balloon("ESP32 ボードが見つからないため、PC のマウスで操作できません。USB ケーブルを確認してください。（クリックでセットアップ）",
                    ToolTipIcon.Warning, () => ShowWizard(SetupWizard.PageBoard));
            }
            return;
        }
        if (ready) { _unpairedSince = null; return; }
        _unpairedSince ??= DateTime.Now;
        if (!_warnedUnpaired && DateTime.Now - _unpairedSince > TimeSpan.FromSeconds(6))
        {
            _warnedUnpaired = true;
            Balloon("マウス（ESP32 Mouse）が iPhone とペアリングされていません。iPhone の「設定 → Bluetooth」で「ESP32 Mouse」を接続してください。（クリックで手順）",
                ToolTipIcon.Warning, () => ShowWizard(SetupWizard.PageIPhone));
        }
    }

    void Balloon(string text, ToolTipIcon icon, Action? onClick = null)
    {
        _balloonClick = onClick;
        _tray.ShowBalloonTip(8000, "ぽくミラー", text, icon);
        Log.Write($"通知: {text}");
    }

    // ---- 画面 ---------------------------------------------------------------

    void ShowWizard(int page)
    {
        if (_wizard is { IsDisposed: false })
        {
            _wizard.GoTo(page);
            _wizard.Activate();
            return;
        }
        _wizard = new SetupWizard(_link, _settings, page, SetMouseEnabled);
        _wizard.FormClosed += (_, _) => _wizard = null;
        _wizard.Show();
    }

    void ShowStatus()
    {
        if (_statusForm is { IsDisposed: false }) { _statusForm.Activate(); return; }
        _statusForm = new StatusForm(() => ShowWizard(0));
        _statusForm.FormClosed += (_, _) => _statusForm = null;
        _statusForm.UpdateStatus(StatusText());
        _statusForm.Show();
    }

    static void ShowHowTo() => MessageBox.Show(SetupWizard.HowToText, "使い方", MessageBoxButtons.OK, MessageBoxIcon.Information);

    static void OpenLog()
    {
        try { Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show($"開けませんでした: {ex.Message}"); }
    }

    void ExitApp()
    {
        _timer.Stop();
        _bridge.Dispose();
        _link.Dispose();
        _uxplay.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        Log.Write("終了しました");
        ExitThread();
    }

    // アプリのアイコン (白い丸に「ぽ」。tools\make-icon.ps1 で作った app.ico を埋め込んである)
    public static Icon AppIcon(Size size)
    {
        using var s = typeof(TrayContext).Assembly.GetManifestResourceStream("iPhoneMirror.app.ico");
        return s is null ? SystemIcons.Application : new Icon(s, size);
    }

    static Icon MakeIcon() => AppIcon(SystemInformation.SmallIconSize);
}
