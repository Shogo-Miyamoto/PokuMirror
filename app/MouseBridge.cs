using System.Diagnostics;
using System.Runtime.InteropServices;

namespace iPhoneMirror;

// PC のマウスを iPhone のマウスとして使う。
//   ミラー画面 (UxPlay の映像部分) を左クリック → 操作開始。PC のマウスの動きを ESP32 経由で iPhone に送る
//   操作中: 左クリック = タップ、ドラッグ = スワイプ、ホイール = スクロール、Shift を押しながら = ゆっくり
//   操作中に右クリック → 終了、中クリック → ポインタの動く向きを 90 度回す
// iPhone はマウスがつながった時点の画面の向きでポインタの向きと範囲を決めるので、ミラー画面の
// 縦横が変わったら ESP32 をつなぎ直し、つながったらポインタを画面の真ん中へ移動する。
sealed class MouseBridge : IDisposable
{
    readonly Esp32Link _link;
    readonly Settings _settings;
    readonly SynchronizationContext _ui;
    readonly NativeMethods.LowLevelMouseProc _proc;
    readonly IntPtr _hook;
    volatile bool _capturing, _busy, _disposed;
    bool _swallowNextUp;
    NativeMethods.POINT _frozen, _restore;
    IntPtr _mirrorHwnd;
    volatile int _rotation;
    bool? _connectedLandscape;  // マウスがつながったときのミラー画面の向き (不明なら null)

    public bool Capturing => _capturing;
    public event Action<string>? Notice;          // 利用者に知らせたいこと (UI スレッドで呼ばれる)
    public event Action<bool>? CapturingChanged;  // 操作中になった / 終わった (UI スレッドで呼ばれる)

    public MouseBridge(Esp32Link link, Settings settings)
    {
        _link = link;
        _settings = settings;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _proc = HookCallback;
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
        _link.PhoneConnectionChanged += OnPhoneConnection;
        new Thread(WatchOrientation) { IsBackground = true, Name = "orientation" }.Start();
    }

    void Post(Action a) => _ui.Post(_ => { try { a(); } catch (Exception ex) { Log.Write($"エラー: {ex}"); } }, null);

    // ---- 画面の向き ---------------------------------------------------------

    bool IsLandscape()
    {
        var h = _mirrorHwnd;
        return h != IntPtr.Zero && NativeMethods.GetClientRect(h, out var rc) && rc.Right > rc.Bottom;
    }

    void WatchOrientation()
    {
        bool? previous = null;
        while (!_disposed)
        {
            Thread.Sleep(500);
            try
            {
                if (_mirrorHwnd == IntPtr.Zero || !NativeMethods.IsWindow(_mirrorHwnd) || !NativeMethods.IsWindowVisible(_mirrorHwnd))
                    _mirrorHwnd = UxPlayHost.MirrorWindow();
                if (_mirrorHwnd == IntPtr.Zero || _link.PhoneConnected != true) { previous = null; continue; }
                var now = IsLandscape();
                // 回転中の一瞬の変化で何度もつなぎ直さないよう、同じ形が 2 回続いたら判断する
                if (previous == now && _connectedLandscape != now && !_busy)
                {
                    _busy = true;
                    Log.Write($"画面が{(now ? "横" : "縦")}になったのでマウスをつなぎ直します");
                    _connectedLandscape = null;
                    if (!_link.ResetBoard()) _busy = false;  // つながると OnPhoneConnection で続きを行う
                }
                previous = now;
            }
            catch (Exception ex) { Log.Write($"画面の確認でエラー: {ex.Message}"); }
        }
    }

    void OnPhoneConnection(bool connected)
    {
        if (!connected) return;
        // つながった直後、iPhone はポインタを左上の角に置く。今の向きを覚えて、真ん中へ移動する
        new Thread(() =>
        {
            try
            {
                _busy = true;
                Thread.Sleep(300);
                if (_mirrorHwnd == IntPtr.Zero) _mirrorHwnd = UxPlayHost.MirrorWindow();
                if (_mirrorHwnd == IntPtr.Zero) { _connectedLandscape = null; return; }
                var landscape = IsLandscape();
                _connectedLandscape = landscape;
                _rotation = landscape ? _settings.RotationLandscape : _settings.RotationPortrait;
                CenterPointer(landscape);
            }
            catch (Exception ex) { Log.Write($"エラー: {ex}"); }
            finally { _link.ClearMoves(); _busy = false; }
        }) { IsBackground = true }.Start();
    }

    // 大きな刻み (127) で角に寄せてから、測定した移動量 (127 刻みのとき、ミラー画像 498x1080 換算で
    // 1 単位 ≒ 0.56 ピクセル) で画面の半分だけ戻す。加速の具合で少しずれることがある。
    void CenterPointer(bool landscape)
    {
        const double PixelsPerUnit = 0.56;
        double w = landscape ? 1080 : 498, h = landscape ? 498 : 1080;
        var (cx, cy) = Inverse(-1, -1, _rotation);  // 角へ寄せる向き (-,-) が画面のどの角に当たるか
        var (ux, uy) = Rotate(-Math.Sign(cx) * w / 2 / PixelsPerUnit, -Math.Sign(cy) * h / 2 / PixelsPerUnit, _rotation);
        _link.Push("p 127 15");
        _link.Push("m -2200 -2200");
        _link.Push($"m {(int)ux} {(int)uy}");
        Thread.Sleep(800);  // ESP32 が動かし終えるまで待つ (約 0.5 秒)
    }

    // 画面上で動かしたい向き (sx, sy) を、iPhone に送る移動量に変換する
    static (double, double) Rotate(double sx, double sy, int rotation) => rotation switch
    {
        1 => (-sy, sx),
        2 => (-sx, -sy),
        3 => (sy, -sx),
        _ => (sx, sy),
    };

    // Rotate の逆: iPhone に送った移動量が画面上でどちらへ動くか
    static (double, double) Inverse(double ux, double uy, int rotation) => rotation switch
    {
        1 => (uy, -ux),
        2 => (-ux, -uy),
        3 => (-uy, ux),
        _ => (ux, uy),
    };

    // 「マウスを上へ動かす」とポインタがどちらへ動くかを、実際に 3 回ちょんちょんと動かして見せる
    void ShowUpDirection()
    {
        var (ux, uy) = Rotate(0, -60, _rotation);
        new Thread(() =>
        {
            for (int i = 0; i < 3; i++)
            {
                _link.Push($"M {(int)ux} {(int)uy}");
                Thread.Sleep(220);
                _link.Push($"M {(int)-ux} {(int)-uy}");
                Thread.Sleep(220);
            }
        }) { IsBackground = true }.Start();
    }

    // ---- マウスのフック -------------------------------------------------------

    // ミラー画面と重ならず、上下左右に余裕がある (端で動きが止まらない) 場所
    static NativeMethods.POINT ParkingSpot(Rectangle mirror)
    {
        var avoid = Rectangle.Inflate(mirror, 250, 250);
        foreach (var screen in Screen.AllScreens.OrderByDescending(s => s.Primary))
        {
            var b = Rectangle.Inflate(screen.Bounds, -250, -250);
            if (b.Width <= 0 || b.Height <= 0) continue;
            foreach (var p in new[] { new Point(b.Left + b.Width / 2, b.Top + b.Height / 2), new Point(b.Left, b.Top), new Point(b.Right, b.Top),
                                      new Point(b.Left, b.Bottom), new Point(b.Right, b.Bottom) })
                if (!avoid.Contains(p)) return new NativeMethods.POINT { X = p.X, Y = p.Y };
        }
        return new NativeMethods.POINT { X = mirror.Left + mirror.Width / 2, Y = mirror.Top + mirror.Height / 2 };
    }

    bool IsOverMirror(NativeMethods.POINT pt, out Rectangle mirror)
    {
        mirror = Rectangle.Empty;
        var hwnd = NativeMethods.GetAncestor(NativeMethods.WindowFromPoint(pt), NativeMethods.GA_ROOT);
        if (hwnd == IntPtr.Zero) return false;
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        try { if (!Process.GetProcessById((int)pid).ProcessName.Equals("uxplay", StringComparison.OrdinalIgnoreCase)) return false; }
        catch { return false; }
        // タイトルバーや枠 (ウィンドウの移動・サイズ変更) は対象外。映像部分だけで開始する
        var p = pt;
        NativeMethods.ScreenToClient(hwnd, ref p);
        NativeMethods.GetClientRect(hwnd, out var rc);
        mirror = new Rectangle(pt.X - p.X, pt.Y - p.Y, rc.Right, rc.Bottom);
        _mirrorHwnd = hwnd;
        return p.X >= 0 && p.Y >= 0 && p.X < rc.Right && p.Y < rc.Bottom;
    }

    public void StopCapture()
    {
        if (!_capturing) return;
        _capturing = false;
        _link.Push("u");
        NativeMethods.SetCursorPos(_restore.X, _restore.Y);
        Post(NativeMethods.RestoreCursor);
        Post(() => CapturingChanged?.Invoke(false));
    }

    IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        var info = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
        var msg = (int)wParam;

        if (!_capturing)
        {
            if (!_settings.MouseEnabled) return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);  // ミラーリングだけ使う設定
            if (msg == NativeMethods.WM_LBUTTONDOWN && IsOverMirror(info.pt, out var mirror))
            {
                // マウス (ESP32) が iPhone とつながっていなければ、操作を始めずに知らせる
                if (_link.PhoneConnected != true)
                {
                    var why = _link.PortName is null ? "ESP32 ボードが見つかりません。USB ケーブルを確認してください。"
                        : "マウス (ESP32 Mouse) が iPhone とつながっていません。iPhone の Bluetooth 設定を確認してください。";
                    Post(() => Notice?.Invoke(why));
                    return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
                }
                _capturing = true;
                _restore = info.pt;
                // PC のカーソルがミラー画面に重なって見づらいので、画面の外へ逃がしておく
                _frozen = ParkingSpot(mirror);
                NativeMethods.SetCursorPos(_frozen.X, _frozen.Y);
                Post(NativeMethods.HideCursor);  // 操作中は PC のカーソルを消す (右クリックで戻る)
                _swallowNextUp = true;
                Post(() => CapturingChanged?.Invoke(true));
                return (IntPtr)1;
            }
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        // 操作中は PC 側にマウス操作を渡さない (カーソルもその場に止まる)
        switch (msg)
        {
            case NativeMethods.WM_MOUSEMOVE:
            {
                if (_busy) break;  // つなぎ直し中は送らない
                // Shift を押している間は 1/3 の速さにして、小さなボタンに合わせやすくする
                var scale = _settings.Sensitivity * ((NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0 ? 0.33 : 1.0);
                var (ux, uy) = Rotate((info.pt.X - _frozen.X) * scale, (info.pt.Y - _frozen.Y) * scale, _rotation);
                _link.AddMove(ux, uy);
                break;
            }
            case NativeMethods.WM_MBUTTONDOWN:
            {
                _rotation = (_rotation + 1) % 4;
                if (IsLandscape()) _settings.RotationLandscape = _rotation; else _settings.RotationPortrait = _rotation;
                _settings.Save();
                ShowUpDirection();
                Post(() => Notice?.Invoke("ポインタの動く向きを 90 度回しました。ポインタが上へ動けば合っています。"));
                break;
            }
            case NativeMethods.WM_LBUTTONDOWN: _link.Push("d"); break;
            case NativeMethods.WM_LBUTTONUP:
                if (_swallowNextUp) _swallowNextUp = false; else _link.Push("u");
                break;
            case NativeMethods.WM_MOUSEWHEEL:
                _link.Push($"s {((short)(info.mouseData >> 16) > 0 ? 1 : -1)}");
                break;
            case NativeMethods.WM_RBUTTONDOWN:
                StopCapture();
                break;
        }
        return (IntPtr)1;
    }

    public void Dispose()
    {
        _disposed = true;
        StopCapture();
        NativeMethods.UnhookWindowsHookEx(_hook);
    }
}
