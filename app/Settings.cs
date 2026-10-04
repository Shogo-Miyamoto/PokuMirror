using System.Globalization;

namespace iPhoneMirror;

// 設定。%LOCALAPPDATA%\iPhoneMirror\settings.txt に「名前=値」で保存する。
sealed class Settings
{
    public bool SetupDone;
    public bool MouseEnabled = true;  // false: ESP32 なしで、ミラーリングだけ使う
    public double Sensitivity = 1.5;
    // 以前の版が保存したポインタの向き (RotationPortrait / RotationLandscape) は読まずに捨てる。
    // 向きはつなぎ直すたびに 0° に戻すので、保存した値が残っていると逆向きに動いてしまう。

    static string FilePath => Path.Combine(Log.Dir, "settings.txt");

    public static Settings Load()
    {
        var s = new Settings();
        try
        {
            foreach (var line in File.ReadAllLines(FilePath))
            {
                var kv = line.Split('=', 2);
                if (kv.Length != 2) continue;
                switch (kv[0].Trim())
                {
                    case "SetupDone": s.SetupDone = kv[1].Trim() == "1"; break;
                    case "MouseEnabled": s.MouseEnabled = kv[1].Trim() != "0"; break;
                    case "Sensitivity": double.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out s.Sensitivity); break;
                }
            }
        }
        catch { }
        if (s.Sensitivity <= 0) s.Sensitivity = 1.5;
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Log.Dir);
            File.WriteAllLines(FilePath, new[]
            {
                $"SetupDone={(SetupDone ? 1 : 0)}",
                $"MouseEnabled={(MouseEnabled ? 1 : 0)}",
                $"Sensitivity={Sensitivity.ToString(CultureInfo.InvariantCulture)}",
            });
        }
        catch (Exception ex) { Log.Write($"設定を保存できません: {ex.Message}"); }
    }
}
