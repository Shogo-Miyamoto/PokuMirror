using System.Collections.Concurrent;
using System.IO.Ports;
using System.Text.RegularExpressions;

namespace iPhoneMirror;

// USB でつないだ ESP32 (Bluetooth マウスのプログラム入り) とのやり取り。
// - ESP32 を自動で探して開き、抜かれたら探し直す
// - 2 秒ごとに状態を問い合わせ、iPhone とつながっているかを PhoneConnected に反映する
// - マウスの移動は溜めて "M dx dy" で送る (返事は待たない。ESP32 側で接続間隔ごとにまとめて送る)
sealed class Esp32Link : IDisposable
{
    readonly ConcurrentQueue<string> _events = new();
    readonly object _gate = new();     // _events と移動量の順番を守るためのロック
    readonly object _portGate = new();
    double _pendX, _pendY;
    SerialPort? _port;
    volatile bool _suspended, _disposed;
    readonly AutoResetEvent _reconnected = new(false);

    public volatile bool Enabled = true;  // false: ミラーリングだけ使うとき。ESP32 を探さない
    public string? PortName { get; private set; }
    public string? OpenError { get; private set; }  // ポートはあるが開けないとき (他のソフトが使用中など)
    string? _lastOpenError;
    public bool IsOpen => _port?.IsOpen == true;
    public bool FirmwareResponding { get; private set; }  // マウスのプログラムから返事が来ている
    public bool? PhoneConnected { get; private set; }     // iPhone と Bluetooth でつながっているか

    // iPhone とつながった / 切れたとき (別スレッドから呼ばれる)
    public event Action<bool>? PhoneConnectionChanged;

    public Esp32Link()
    {
        new Thread(ManagerLoop) { IsBackground = true, Name = "esp32-manager" }.Start();
        new Thread(WriterLoop) { IsBackground = true, Name = "esp32-writer" }.Start();
        new Thread(ReaderLoop) { IsBackground = true, Name = "esp32-reader" }.Start();
    }

    // ESP32 ボードの USB シリアル (CH340 / CP210x など) の COM ポートを探す
    public static string? FindPort()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");
            foreach (var o in searcher.Get())
            {
                var name = o["Name"]?.ToString() ?? "";
                if (!Regex.IsMatch(name, "CH34|CP210|USB.?Serial|UART|USB-SERIAL|JTAG", RegexOptions.IgnoreCase)) continue;
                var m = Regex.Match(name, @"\((COM\d+)\)");
                if (m.Success) return m.Groups[1].Value;
            }
        }
        catch { }
        return null;
    }

    // ---- 送信 -------------------------------------------------------------

    public void AddMove(double dx, double dy)
    {
        lock (_gate) { _pendX += dx; _pendY += dy; }
    }

    // ボタン操作などの前に、それまでの移動を確定させてから積む
    public void Push(string cmd)
    {
        lock (_gate)
        {
            int dx = (int)_pendX, dy = (int)_pendY;
            if (dx != 0 || dy != 0) { _events.Enqueue($"M {dx} {dy}"); _pendX -= dx; _pendY -= dy; }
            _events.Enqueue(cmd);
        }
    }

    public void ClearMoves()
    {
        lock (_gate) { _pendX = _pendY = 0; }
    }

    void WriterLoop()
    {
        while (!_disposed)
        {
            string? cmd;
            lock (_gate)
            {
                if (!_events.TryDequeue(out cmd))
                {
                    int dx = (int)_pendX, dy = (int)_pendY;
                    if (dx != 0 || dy != 0) { cmd = $"M {dx} {dy}"; _pendX -= dx; _pendY -= dy; }
                }
            }
            if (cmd is null) { Thread.Sleep(5); continue; }
            var port = _port;
            if (port is null || !port.IsOpen) continue;  // つながっていないときは捨てる
            try { port.WriteLine(cmd); }
            catch (Exception ex) { Log.Write($"ESP32 への送信エラー: {ex.Message}"); ClosePort(); }
        }
    }

    // ---- 受信 -------------------------------------------------------------

    void ReaderLoop()
    {
        while (!_disposed)
        {
            var port = _port;
            if (port is null || !port.IsOpen) { Thread.Sleep(200); continue; }
            string line;
            try { line = port.ReadLine().Trim(); }
            catch (TimeoutException) { continue; }
            catch (Exception) { Thread.Sleep(200); continue; }
            if (line.Length == 0) continue;
            // 2 秒ごとの "ok" 以外 (ready / event ...) は、つなぎ直しの様子を後から追えるよう記録する
            if (!line.StartsWith("ok")) Log.Write($"ESP32: {line}");

            if (line.StartsWith("ok") || line == "ready") FirmwareResponding = true;
            if (line.StartsWith("ok")) SetPhone(line.Contains("connected"));
            else if (line.StartsWith("event connected")) SetPhone(true);
            else if (line.StartsWith("event disconnected")) SetPhone(false);
            else if (line.StartsWith("event interval")) _reconnected.Set();
        }
    }

    void SetPhone(bool connected)
    {
        if (PhoneConnected == connected) return;
        PhoneConnected = connected;
        Log.Write(connected ? "iPhone とつながりました" : "iPhone との接続が切れました");
        try { PhoneConnectionChanged?.Invoke(connected); } catch (Exception ex) { Log.Write($"エラー: {ex}"); }
    }

    // ---- ポートの管理 -------------------------------------------------------

    void ManagerLoop()
    {
        var lastPoll = DateTime.MinValue;
        while (!_disposed)
        {
            Thread.Sleep(500);
            if (!Enabled) { if (IsOpen) ClosePort(); PortName = null; continue; }
            if (_suspended) continue;
            if (!IsOpen)
            {
                TryOpen();
                continue;
            }
            if (DateTime.Now - lastPoll > TimeSpan.FromSeconds(2))
            {
                lastPoll = DateTime.Now;
                Push("?");
            }
        }
    }

    void TryOpen()
    {
        var name = FindPort();
        if (name is null) { PortName = null; OpenError = null; FirmwareResponding = false; SetPhoneUnknown(); return; }
        lock (_portGate)
        {
            try
            {
                // DTR/RTS を動かすと ESP32 が再起動してしまうので、どちらも off のまま開く
                var port = new SerialPort(name, 115200) { DtrEnable = false, RtsEnable = false, NewLine = "\n", ReadTimeout = 500 };
                port.Open();
                _port = port;
                PortName = name;
                OpenError = null; _lastOpenError = null;
                Log.Write($"ESP32 ({name}) を開きました");
                Push("p 127 15");
                Push("?");
            }
            catch (Exception ex)
            {
                PortName = name;
                OpenError = ex.Message;
                if (_lastOpenError != ex.Message) Log.Write($"ESP32 ({name}) を開けません: {ex.Message}");
                _lastOpenError = ex.Message;
                Thread.Sleep(1500);
            }
        }
    }

    void SetPhoneUnknown()
    {
        if (PhoneConnected is null) return;
        PhoneConnected = null;
        try { PhoneConnectionChanged?.Invoke(false); } catch { }
    }

    void ClosePort()
    {
        lock (_portGate)
        {
            try { _port?.Close(); } catch { }
            _port = null;
            FirmwareResponding = false;
        }
        SetPhoneUnknown();
    }

    // 書き込みのあいだ COM ポートを手放す
    public void Suspend() { _suspended = true; ClosePort(); }
    public void Resume() { _suspended = false; }

    // ESP32 を再起動して iPhone につなぎ直す (約 2 秒)。iPhone はマウスがつながった時点の
    // 画面の向きでポインタの向きと動ける範囲を決めるので、縦横が変わったらつなぎ直す。
    public bool ResetBoard()
    {
        var port = _port;
        if (port is null || !port.IsOpen) return false;
        _reconnected.Reset();
        SetPhone(false);  // つながり直したときに PhoneConnectionChanged(true) が必ず届くように
        try { port.RtsEnable = true; Thread.Sleep(100); port.RtsEnable = false; }
        catch (Exception ex) { Log.Write($"ESP32 の再起動に失敗: {ex.Message}"); return false; }
        var ok = _reconnected.WaitOne(8000);
        if (!ok) Log.Write("つなぎ直しに時間がかかっています");
        return ok;
    }

    public void Dispose()
    {
        _disposed = true;
        ClosePort();
    }
}
