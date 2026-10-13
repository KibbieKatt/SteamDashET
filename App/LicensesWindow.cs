#nullable enable

using System.Drawing;
using System.Windows.Forms;

namespace SteamDashET;

internal sealed class LicensesWindow : Form
{
    public LicensesWindow()
    {
        Text = "SteamDashET licenses";
        Name = "SteamDashETLicensesWindow";
        AccessibleName = "SteamDashET third-party licenses";
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(800, 600);
        MinimumSize = new Size(520, 360);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(246, 248, 251);
        ForeColor = Color.FromArgb(30, 37, 49);
        Font = new Font("Segoe UI", 10);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20),
            ColumnCount = 1,
            RowCount = 3
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label
        {
            Text = "Third-party licenses",
            AutoSize = true,
            Font = new Font(Font.FontFamily, 14, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 12)
        }, 0, 0);

        var notices = new TextBox
        {
            MaxLength = int.MaxValue,
            Text = ThirdPartyNotices.Text,
            Name = "ThirdPartyLicenseNotices",
            AccessibleName = "Third-party license notices",
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = true,
            BackColor = Color.White,
            ForeColor = ForeColor,
            Margin = new Padding(0, 0, 0, 12)
        };
        layout.Controls.Add(notices, 0, 1);

        var close = new Button
        {
            Text = "Close",
            Name = "CloseLicensesButton",
            AccessibleName = "Close licenses",
            AutoSize = true,
            MinimumSize = new Size(92, 36),
            Padding = new Padding(12, 4, 12, 4),
            Anchor = AnchorStyles.Right,
            Margin = Padding.Empty,
            UseVisualStyleBackColor = true
        };
        close.Click += (_, _) => Close();
        CancelButton = close;
        layout.Controls.Add(close, 0, 2);
        Controls.Add(layout);
        ActiveControl = close;
    }
}
