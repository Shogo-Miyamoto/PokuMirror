using System.Globalization;

namespace iPhoneMirror;

// 設定。%LOCALAPPDATA%\iPhoneMirror\settings.txt に「名前=値」で保存する。
sealed class Settings
{
    public bool SetupDone;
    public bool MouseEnabled = true;  // false: ESP32 なしで、ミラーリングだけ使う
    public double Sensitivity = 1.5;
    public int RotationPortrait;   // 中クリックで合わせたポインタの向き (縦画面)
    public int RotationLandscape;  // 同 (横画面)

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
                    case "RotationPortrait": int.TryParse(kv[1], out s.RotationPortrait); break;
                    case "RotationLandscape": int.TryParse(kv[1], out s.RotationLandscape); break;
                }
            }
        }
        catch { }
        if (s.Sensitivity <= 0) s.Sensitivity = 1.5;
        s.RotationPortrait &= 3; s.RotationLandscape &= 3;
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
                $"RotationPortrait={RotationPortrait}",
                $"RotationLandscape={RotationLandscape}",
            });
        }
        catch (Exception ex) { Log.Write($"設定を保存できません: {ex.Message}"); }
    }
}
