namespace iPhoneMirror;

// 動作の記録。%LOCALAPPDATA%\PokuMirror\log.txt に追記する (1MB を超えたら作り直す)。
static class Log
{
    static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public static readonly string Dir = Path.Combine(AppData, "PokuMirror");
    public static string FilePath => Path.Combine(Dir, "log.txt");
    static readonly object Gate = new();

    public static void Start()
    {
        try
        {
            // 旧名 (iPhoneMirror) のときの設定を引き継ぐ
            var old = Path.Combine(AppData, "iPhoneMirror", "settings.txt");
            if (!Directory.Exists(Dir) && File.Exists(old))
            {
                Directory.CreateDirectory(Dir);
                File.Copy(old, Path.Combine(Dir, "settings.txt"));
            }
            Directory.CreateDirectory(Dir);
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 1_000_000) File.Delete(FilePath);
        }
        catch { }
        Write("起動しました");
    }

    public static void Write(string message)
    {
        lock (Gate)
        {
            try { File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}\r\n"); }
            catch { }
        }
    }
}
