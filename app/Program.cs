// ぽくミラー: iPhone の画面を PC に映し (UxPlay)、PC のマウスで操作する (ESP32 を Bluetooth マウスにする)。
// 黒い画面は出さず、タスクバー右下 (通知領域) のアイコンとして常駐する。
namespace iPhoneMirror;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(true, "PokuMirror.SingleInstance", out var first);
        if (!first)
        {
            MessageBox.Show("ぽくミラーはすでに起動しています。\nタスクバー右下のアイコンから操作できます。", "ぽくミラー",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Log.Start();
        NativeMethods.RestoreCursor();  // 前回カーソルを消したまま終わっていても元に戻す
        Application.ThreadException += (_, e) => Log.Write($"エラー: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            NativeMethods.RestoreCursor();
            Log.Write($"エラーで終了: {e.ExceptionObject}");
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => NativeMethods.RestoreCursor();

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext(forceSetup: args.Contains("--setup")));
    }
}
