using System.Diagnostics;

namespace iPhoneMirror;

// 画面ミラーリング受信ソフト UxPlay を、黒い画面を出さずに裏で動かす。
// アプリと同じフォルダの UxPlay-Portable を使う。止まったら起動し直す。
sealed class UxPlayHost : IDisposable
{
    public static string Root => Path.Combine(AppContext.BaseDirectory, "UxPlay-Portable");
    public static string ExePath => Path.Combine(Root, "bin", "uxplay.exe");
    public static bool Available => File.Exists(ExePath);
    public static string ServerName => $"UxPlay-{Environment.MachineName}";

    Process? _uxplay, _resizer;
    volatile bool _stopping;
    readonly System.Threading.Timer _watchdog;

    public UxPlayHost()
    {
        _watchdog = new System.Threading.Timer(_ => Watch(), null, 3000, 3000);
    }

    public bool Running => _uxplay is { HasExited: false } || OtherInstanceRunning();

    static bool OtherInstanceRunning() => Process.GetProcessesByName("uxplay").Length > 0;

    public void Start()
    {
        if (!Available) { Log.Write($"UxPlay が見つかりません: {ExePath}"); return; }
        // 別の方法で起動済みの UxPlay があればそれを使う (同時に 2 つは動かせない)
        if (_uxplay is { HasExited: false } || OtherInstanceRunning()) { StartResizer(); return; }

        var psi = new ProcessStartInfo(ExePath, $"-n \"{ServerName}\" -nh -fps 60")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        psi.Environment["PATH"] = $"{Path.Combine(Root, "bin")};{sys};{win};{Path.Combine(sys, "WindowsPowerShell", "v1.0")}";
        psi.Environment["GST_PLUGIN_SYSTEM_PATH"] = Path.Combine(Root, "lib", "gstreamer-1.0");
        psi.Environment["GST_PLUGIN_PATH"] = "";
        psi.Environment["GST_PLUGIN_SCANNER"] = Path.Combine(Root, "libexec", "gstreamer-1.0", "gst-plugin-scanner.exe");
        psi.Environment["GST_REGISTRY"] = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UxPlay-Portable", "registry.bin");

        try
        {
            _uxplay = Process.Start(psi)!;
            _uxplay.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log.Write($"UxPlay: {e.Data}"); };
            _uxplay.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log.Write($"UxPlay: {e.Data}"); };
            _uxplay.BeginOutputReadLine();
            _uxplay.BeginErrorReadLine();
            Log.Write("UxPlay を起動しました");
        }
        catch (Exception ex) { Log.Write($"UxPlay を起動できません: {ex.Message}"); }
        StartResizer();
    }

    // iPhone を回したときにミラーのウィンドウの形を合わせる補助 (PowerShell) を、窓なしで動かす
    void StartResizer()
    {
        if (_resizer is { HasExited: false }) return;
        var script = Path.Combine(Root, "uxplay-autoresize.ps1");
        if (!File.Exists(script)) return;
        try
        {
            _resizer = Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{script}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex) { Log.Write($"ウィンドウ調整を起動できません: {ex.Message}"); }
    }

    void Watch()
    {
        if (_stopping || _uxplay is null) return;
        if (_uxplay.HasExited)
        {
            Log.Write($"UxPlay が終了しました (コード {_uxplay.ExitCode})。起動し直します");
            _uxplay = null;
            Start();
        }
    }

    // ミラーリング中なら、映像のウィンドウ (見えているもの) を返す
    public static IntPtr MirrorWindow()
    {
        foreach (var p in Process.GetProcessesByName("uxplay"))
        {
            try
            {
                var h = p.MainWindowHandle;
                if (h != IntPtr.Zero && NativeMethods.IsWindowVisible(h)) return h;
            }
            catch { }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _stopping = true;
        _watchdog.Dispose();
        foreach (var p in new[] { _resizer, _uxplay })
        {
            try { if (p is { HasExited: false }) p.Kill(true); } catch { }
        }
    }
}
