using System.Diagnostics;

namespace iPhoneMirror;

// 初回のセットアップを 1 ページずつ案内する画面。
//   ようこそ → ESP32 をつなぐ → ESP32 に書き込む → iPhone の設定 → Windows の設定 → 完了
sealed class SetupWizard : Form
{
    public const int PageBoard = 1, PageFlash = 2, PageIPhone = 3, PageWindows = 4;

    public const string HowToText =
        "1. iPhone のコントロールセンター →「画面ミラーリング」→「" + "UxPlay-（PC 名）" + "」を選ぶ\n" +
        "2. PC に映った iPhone の画面を左クリックすると操作開始\n" +
        "   ・マウスを動かす → iPhone のポインタ（グレーの丸）が動く\n" +
        "   ・左クリック → タップ　・ドラッグ → スワイプ　・ホイール → スクロール\n" +
        "   ・Shift を押しながら動かす → ゆっくり動く（小さなボタンに合わせるとき）\n" +
        "   ・中クリック → ポインタの動く向きを 90 度回す（向きがずれたとき）\n" +
        "3. 右クリックで操作終了（PC のマウスに戻る）\n\n" +
        "横画面のアプリを使うときは、iPhone 本体も横向きにしてください。\n" +
        "画面の縦／横が変わると、マウスを自動でつなぎ直します（約 2 秒）。";

    readonly Esp32Link _link;
    readonly Settings _settings;
    readonly Label _title, _step;
    readonly Panel _host;
    readonly Button _back, _next;
    readonly System.Windows.Forms.Timer _timer;
    readonly List<(string Title, Control Panel, Action? Refresh)> _pages = new();
    int _page;

    // 各ページの状態表示
    Label _boardStatus = null!, _flashStatus = null!, _phoneStatus = null!, _netStatus = null!, _fwStatus = null!;
    TextBox _flashLog = null!;
    Button _flashButton = null!;
    bool _flashing;
    string? _netText, _fwText;
    DateTime _lastWinCheck = DateTime.MinValue;

    readonly Action<bool> _setMouse;
    RadioButton _rbMouse = null!, _rbMirrorOnly = null!;

    // ミラーリングだけ使う設定のときは、ESP32 のページ (1〜3) を飛ばす
    int Step(int direction)
    {
        var p = _page + direction;
        while (!_settings.MouseEnabled && p >= PageBoard && p <= PageIPhone) p += direction;
        return p;
    }

    public SetupWizard(Esp32Link link, Settings settings, int startPage, Action<bool> setMouse)
    {
        _link = link;
        _settings = settings;
        _setMouse = setMouse;
        Text = "ぽくミラー - セットアップ";
        Font = new Font("Yu Gothic UI", 10f);
        Icon = TrayContext.AppIcon(new Size(32, 32));
        ClientSize = new Size(680, 500);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        _title = new Label { Font = new Font("Yu Gothic UI", 15f, FontStyle.Bold), Location = new Point(24, 16), AutoSize = true };
        _step = new Label { ForeColor = Color.DimGray, Location = new Point(26, 52), AutoSize = true };
        _host = new Panel { Location = new Point(24, 84), Size = new Size(632, 350) };
        _back = new Button { Text = "← 戻る", Size = new Size(110, 34), Location = new Point(400, 450) };
        _next = new Button { Text = "次へ →", Size = new Size(130, 34), Location = new Point(520, 450) };
        _back.Click += (_, _) => GoTo(Step(-1));
        _next.Click += (_, _) => { if (_page == _pages.Count - 1) Finish(); else GoTo(Step(+1)); };
        Controls.AddRange(new Control[] { _title, _step, _host, _back, _next });

        BuildPages();
        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => _pages[_page].Refresh?.Invoke();
        _timer.Start();
        GoTo(startPage);
    }

    public void GoTo(int page)
    {
        if (_flashing) return;
        _page = Math.Clamp(page, 0, _pages.Count - 1);
        var (title, panel, refresh) = _pages[_page];
        _title.Text = title;
        _step.Text = $"ステップ {_page + 1} / {_pages.Count}";
        _host.Controls.Clear();
        _host.Controls.Add(panel);
        _back.Enabled = _page > 0;
        _next.Text = _page == _pages.Count - 1 ? "完了" : "次へ →";
        refresh?.Invoke();
    }

    void Finish()
    {
        _settings.SetupDone = true;
        _settings.Save();
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        base.OnFormClosed(e);
    }

    // ---- ページ ---------------------------------------------------------------

    static FlowLayoutPanel NewPage() => new()
    {
        Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
    };

    static Label Text_(string text, bool bold = false) => new()
    {
        Text = text, AutoSize = true, MaximumSize = new Size(600, 0), Margin = new Padding(0, 0, 0, 10),
        Font = bold ? new Font("Yu Gothic UI", 10f, FontStyle.Bold) : null!,
    };

    static Label Status() => new()
    {
        AutoSize = true, MaximumSize = new Size(600, 0), Margin = new Padding(0, 4, 0, 12),
        Font = new Font("Yu Gothic UI", 11f, FontStyle.Bold),
    };

    static LinkLabel Link(string text, string url)
    {
        var l = new LinkLabel { Text = text, AutoSize = true, Margin = new Padding(12, 0, 0, 8) };
        l.LinkClicked += (_, _) => OpenUrl(url);
        return l;
    }

    static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show($"開けませんでした: {ex.Message}"); }
    }

    static void SetStatus(Label label, bool ok, string text)
    {
        var t = (ok ? "✔ " : "● ") + text;
        if (label.Text != t) label.Text = t;
        label.ForeColor = ok ? Color.ForestGreen : Color.DarkOrange;
    }

    void BuildPages()
    {
        // 0. ようこそ
        var p0 = NewPage();
        p0.Controls.Add(Text_("iPhone の画面を PC に映します。ESP32 ボードがあれば、PC のマウスで iPhone を操作することもできます（ESP32 が Bluetooth マウスとして iPhone に動きを伝えます）。iPhone にアプリを入れる必要はありません。"));
        p0.Controls.Add(Text_("使い方を選んでください", bold: true));
        _rbMouse = new RadioButton
        {
            Text = "PC のマウスで iPhone を操作する（ESP32 ボードを使う）", AutoSize = true, Checked = _settings.MouseEnabled,
            Margin = new Padding(8, 0, 0, 4),
        };
        p0.Controls.Add(_rbMouse);
        p0.Controls.Add(Text_("　 用意するもの: ESP32 ボード（ESP32-DevKitC、Freenove ESP32 など。「ESP32-S2」は使えません）と、データ通信ができる USB ケーブル"));
        _rbMirrorOnly = new RadioButton
        {
            Text = "画面ミラーリングだけ使う（ESP32 なし。iPhone の画面を PC に映すだけ）", AutoSize = true, Checked = !_settings.MouseEnabled,
            Margin = new Padding(8, 0, 0, 10),
        };
        p0.Controls.Add(_rbMirrorOnly);
        _rbMouse.CheckedChanged += (_, _) => { if (_rbMouse.Checked != _settings.MouseEnabled) _setMouse(_rbMouse.Checked); };
        p0.Controls.Add(Text_("あとからタスクバー右下のアイコンのメニューで切り替えることもできます。どちらの場合も、iPhone は PC と同じ Wi-Fi につないでください。"));
        p0.Controls.Add(Text_("「次へ」を押して、順番に設定していきましょう。最初の 1 回だけです。"));
        _pages.Add(("ようこそ", p0, null));

        // 1. ESP32 をつなぐ
        var p1 = NewPage();
        p1.Controls.Add(Text_("ESP32 ボードを USB ケーブルで PC につないでください。"));
        _boardStatus = Status();
        p1.Controls.Add(_boardStatus);
        p1.Controls.Add(Text_("見つからない場合は、ボードの USB チップ用のドライバが必要です。どちらが必要かは上に表示されます（ボードをつないでいれば自動で判定します）。表示がない場合は、ボードの小さなチップに書かれた文字（CH340 / CP2102 など）を確認してください。"));
        p1.Controls.Add(Link("CH340 / CH341 のドライバ（WCH 社）", "https://www.wch-ic.com/downloads/CH341SER_EXE.html"));
        p1.Controls.Add(Link("CP210x のドライバ（Silicon Labs 社）", "https://www.silabs.com/developer-tools/usb-to-uart-bridge-vcp-drivers"));
        p1.Controls.Add(Text_("ケーブルが充電専用だと見つかりません。別のケーブルも試してみてください。"));
        _pages.Add(("ESP32 ボードをつなぐ", p1, () =>
        {
            if (_link.PortName is null)
            {
                // ドライバがなくても、USB の ID からどちらのチップかは分かる
                var chip = DetectUsbChip();
                SetStatus(_boardStatus, false, chip switch
                {
                    "CH340" => "ボードは見つかりましたが、ドライバが入っていません。下の「CH340 / CH341 のドライバ」を入れてください",
                    "CP210x" => "ボードは見つかりましたが、ドライバが入っていません。下の「CP210x のドライバ」を入れてください",
                    _ => "ESP32 ボードが見つかりません（USB ケーブルを確認してください）",
                });
            }
            else if (_link.OpenError is not null) SetStatus(_boardStatus, false, $"{_link.PortName} が他のソフトで使われています。他のツールを閉じてください");
            else SetStatus(_boardStatus, true, $"ESP32 ボードが見つかりました（{_link.PortName}）");
        }));

        // 2. 書き込む
        var p2 = NewPage();
        p2.Controls.Add(Text_("ESP32 ボードに、Bluetooth マウスとして動くプログラムを書き込みます（1 分ほど）。"));
        _flashStatus = Status();
        p2.Controls.Add(_flashStatus);
        _flashButton = new Button { Text = "ESP32 に書き込む", AutoSize = true, Padding = new Padding(8, 2, 8, 2), Margin = new Padding(0, 0, 0, 8) };
        _flashButton.Click += async (_, _) => await FlashAsync();
        p2.Controls.Add(_flashButton);
        _flashLog = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Size = new Size(600, 130),
            Font = new Font("Consolas", 8.5f), Margin = new Padding(0, 0, 0, 8),
        };
        p2.Controls.Add(_flashLog);
        p2.Controls.Add(Text_("※ 書き込み直すと、iPhone とのペアリング情報も消えます。以前ペアリングしていた場合は、次のページの手順で iPhone 側の登録を解除してからペアリングし直してください。"));
        _pages.Add(("ESP32 に書き込む", p2, () =>
        {
            if (_flashing) return;
            if (_link.FirmwareResponding) SetStatus(_flashStatus, true, "書き込み済みです（このページは飛ばしてかまいません）");
            else if (_link.PortName is null) SetStatus(_flashStatus, false, "ESP32 ボードが見つかりません（前のページを確認してください）");
            else SetStatus(_flashStatus, false, "まだ書き込まれていないようです。「ESP32 に書き込む」を押してください");
            _flashButton.Enabled = _link.PortName is not null;
        }));

        // 3. iPhone の設定
        var p3 = NewPage();
        p3.Controls.Add(Text_("iPhone で次の 2 つを設定してください。", bold: true));
        p3.Controls.Add(Text_("① 設定 → アクセシビリティ → タッチ → AssistiveTouch を「オン」\n" +
                              "　 （同じ画面の「メニューを常に表示」を「オフ」にすると、丸いボタンが邪魔になりません）"));
        p3.Controls.Add(Text_("② 設定 → Bluetooth →「その他のデバイス」の「ESP32 Mouse」をタップしてペアリング"));
        _phoneStatus = Status();
        p3.Controls.Add(_phoneStatus);
        p3.Controls.Add(Text_("「ESP32 Mouse」が出てこないときは、「自分のデバイス」に古い「ESP32 Mouse」が残っていないか確認し、ⓘ →「このデバイスの登録を解除」してから、Bluetooth をいったんオフ → オンにしてください。"));
        _pages.Add(("iPhone の設定", p3, () =>
        {
            if (_link.PhoneConnected == true) SetStatus(_phoneStatus, true, "iPhone とつながりました");
            else if (_link.PortName is null) SetStatus(_phoneStatus, false, "ESP32 ボードが見つかりません");
            else SetStatus(_phoneStatus, false, "iPhone とのペアリングを待っています...");
        }));

        // 4. Windows の設定
        var p4 = NewPage();
        p4.Controls.Add(Text_("iPhone から PC が見えるように、Windows を設定します。PC と iPhone は同じ Wi-Fi（同じルーター）につないでください。"));
        p4.Controls.Add(Text_("① ネットワークを「プライベート ネットワーク」にする", bold: true));
        _netStatus = Status();
        p4.Controls.Add(_netStatus);
        var openNet = new Button { Text = "ネットワークの設定を開く", AutoSize = true, Margin = new Padding(0, 0, 0, 12) };
        openNet.Click += (_, _) => OpenUrl("ms-settings:network-status");
        p4.Controls.Add(openNet);
        p4.Controls.Add(Text_("　 開いた画面で、接続中のネットワークの「プロパティ」→「プライベート ネットワーク」を選びます。"));
        p4.Controls.Add(Text_("② ファイアウォールで画面ミラーリングの受信を許可する", bold: true));
        _fwStatus = Status();
        p4.Controls.Add(_fwStatus);
        var allow = new Button { Text = "許可する（管理者の確認が出ます）", AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
        allow.Click += (_, _) => AllowFirewall();
        p4.Controls.Add(allow);
        _pages.Add(("Windows の設定", p4, RefreshWindows));

        // 5. 完了
        var p5 = NewPage();
        p5.Controls.Add(Text_("セットアップは完了です。使い方:", bold: true));
        p5.Controls.Add(Text_(HowToText));
        p5.Controls.Add(Text_("このアプリはタスクバー右下のアイコンとして動いています。アイコンを右クリックすると、状態の確認・セットアップ・終了ができます。"));
        _pages.Add(("完了", p5, null));
    }

    // つながっている USB 機器の ID から、ESP32 ボードの USB チップを判定する (ドライバの有無に関係なく分かる)
    static string? DetectUsbChip()
    {
        try
        {
            using var s = new System.Management.ManagementObjectSearcher(
                "SELECT DeviceID FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB\\\\VID_%'");
            foreach (var o in s.Get())
            {
                var id = o["DeviceID"]?.ToString()?.ToUpperInvariant() ?? "";
                if (id.Contains("VID_1A86")) return "CH340";   // WCH
                if (id.Contains("VID_10C4")) return "CP210x";  // Silicon Labs
            }
        }
        catch { }
        return null;
    }

    // ---- 書き込み -------------------------------------------------------------

    async Task FlashAsync()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "ESP32-firmware");
        var esptool = Path.Combine(dir, "esptool.exe");
        var bin = Path.Combine(dir, "esp32-mouse.bin");
        if (!File.Exists(esptool) || !File.Exists(bin))
        {
            MessageBox.Show($"書き込み用のファイルが見つかりません:\n{dir}", "セットアップ", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        var port = _link.PortName ?? Esp32Link.FindPort();
        if (port is null) { MessageBox.Show("ESP32 ボードが見つかりません。"); return; }

        _flashing = true;
        _flashButton.Enabled = _back.Enabled = _next.Enabled = false;
        _flashLog.Clear();
        SetStatus(_flashStatus, false, "書き込んでいます... USB ケーブルを抜かないでください");
        _link.Suspend();
        int code = -1;
        try
        {
            await Task.Delay(500);
            var psi = new ProcessStartInfo(esptool, $"--chip esp32 --port {port} --baud 460800 write-flash 0x0 \"{bin}\"")
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            void Append(string? s)
            {
                if (string.IsNullOrWhiteSpace(s)) return;
                BeginInvoke(() => { _flashLog.AppendText(s.Trim() + Environment.NewLine); });
            }
            p.OutputDataReceived += (_, e) => Append(e.Data);
            p.ErrorDataReceived += (_, e) => Append(e.Data);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync();
            code = p.ExitCode;
        }
        catch (Exception ex) { _flashLog.AppendText($"エラー: {ex.Message}{Environment.NewLine}"); }
        finally
        {
            _link.Resume();
            _flashing = false;
            _flashButton.Enabled = _next.Enabled = true;
            _back.Enabled = _page > 0;
        }
        Log.Write($"ESP32 への書き込み: 終了コード {code}");
        if (code == 0)
        {
            SetStatus(_flashStatus, true, "書き込みが完了しました。「次へ」で iPhone の設定に進んでください");
        }
        else
        {
            SetStatus(_flashStatus, false, "書き込みに失敗しました。ボードの「BOOT」ボタンを押したまま、もう一度「ESP32 に書き込む」を押してください");
        }
    }

    // ---- Windows の設定 ---------------------------------------------------------

    void RefreshWindows()
    {
        if (_netText is not null) SetStatus(_netStatus, _netText.StartsWith("✔"), _netText.TrimStart('✔', '●', ' '));
        if (_fwText is not null) SetStatus(_fwStatus, _fwText.StartsWith("✔"), _fwText.TrimStart('✔', '●', ' '));
        if (DateTime.Now - _lastWinCheck < TimeSpan.FromSeconds(5)) return;
        _lastWinCheck = DateTime.Now;
        if (_netText is null) SetStatus(_netStatus, false, "確認しています...");
        if (_fwText is null) SetStatus(_fwStatus, false, "確認しています...");
        Task.Run(() =>
        {
            _netText = CheckNetwork();
            _fwText = CheckFirewall();
        });
    }

    static string CheckNetwork()
    {
        try
        {
            using var s = new System.Management.ManagementObjectSearcher(@"root\StandardCimv2",
                "SELECT Name, NetworkCategory FROM MSFT_NetConnectionProfile");
            var profiles = s.Get().Cast<System.Management.ManagementObject>().ToList();
            if (profiles.Count == 0) return "● ネットワークにつながっていません";
            var pub = profiles.Where(p => Convert.ToInt32(p["NetworkCategory"]) == 0).Select(p => p["Name"]?.ToString()).ToList();
            return pub.Count == 0 ? "✔ プライベート ネットワークになっています"
                : $"● 「{string.Join("、", pub)}」がパブリック ネットワークになっています";
        }
        catch (Exception ex) { return $"● 確認できませんでした（{ex.Message}）"; }
    }

    static string CheckFirewall()
    {
        try
        {
            var exe = UxPlayHost.ExePath.Replace("'", "''");
            // 1 行目: プライベート用の Windows ファイアウォールが有効か、2 行目: この受信ソフトを許可するルールの数
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -Command \"(Get-NetFirewallProfile -Profile Private).Enabled; " +
                "(Get-NetFirewallApplicationFilter -Program '" + exe + "' -ErrorAction SilentlyContinue | " +
                "Get-NetFirewallRule | Where-Object { $_.Enabled -eq 'True' -and $_.Action -eq 'Allow' -and $_.Direction -eq 'Inbound' } | Measure-Object).Count\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi)!;
            var lines = p.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            p.WaitForExit(15000);
            if (lines.Length > 0 && lines[0].Equals("False", StringComparison.OrdinalIgnoreCase))
                return "✔ Windows ファイアウォールがオフなので、許可は不要です（別のセキュリティソフトを使っている場合は、そちらで許可が必要なことがあります）";
            return lines.Length > 1 && int.TryParse(lines[1], out var n) && n > 0 ? "✔ 許可されています" : "● まだ許可されていません";
        }
        catch (Exception ex) { return $"● 確認できませんでした（{ex.Message}）"; }
    }

    void AllowFirewall()
    {
        var exe = UxPlayHost.ExePath.Replace("'", "''");
        var cmd = "New-NetFirewallRule -DisplayName 'ぽくミラー (UxPlay)' -Direction Inbound -Action Allow " +
                  "-Profile Private,Domain -Program '" + exe + "'";
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -WindowStyle Hidden -Command \"{cmd}\"")
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            })!;
            p.WaitForExit(30000);
            Log.Write("ファイアウォールの許可を追加しました");
        }
        catch (System.ComponentModel.Win32Exception) { return; }  // 管理者の確認で「いいえ」
        catch (Exception ex) { MessageBox.Show($"許可できませんでした: {ex.Message}"); return; }
        _fwText = null;
        _lastWinCheck = DateTime.MinValue;
        RefreshWindows();
    }
}
