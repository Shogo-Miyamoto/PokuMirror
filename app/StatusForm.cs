namespace iPhoneMirror;

// 今の状態 (ESP32 / iPhone とのマウス接続 / 画面ミラーリング) を表示する小さな画面
sealed class StatusForm : Form
{
    readonly Label _label;

    public StatusForm(Action openSetup)
    {
        Text = "ぽくミラー - 状態";
        Font = new Font("Yu Gothic UI", 10f);
        Icon = TrayContext.AppIcon(new Size(32, 32));
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);

        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        _label = new Label { AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(0, 0, 0, 12) };
        var hint = new Label
        {
            AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = Color.DimGray,
            Text = "ミラー画面を左クリックで操作開始、右クリックで終了。Shift を押しながら動かすとゆっくり動きます。",
            Margin = new Padding(0, 0, 0, 12),
        };
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        var setup = new Button { Text = "セットアップ", AutoSize = true };
        setup.Click += (_, _) => openSetup();
        var close = new Button { Text = "閉じる", AutoSize = true };
        close.Click += (_, _) => Close();
        buttons.Controls.AddRange(new Control[] { setup, close });
        layout.Controls.AddRange(new Control[] { _label, hint, buttons });
        Controls.Add(layout);
        CancelButton = close;
    }

    public void UpdateStatus(string text)
    {
        if (_label.Text != text) _label.Text = text;
    }
}
