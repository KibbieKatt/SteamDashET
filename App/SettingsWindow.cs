#nullable enable

using System;
using System.Drawing;
using System.Windows.Forms;

namespace SteamDashET;

internal sealed class SettingsWindow : Form
{
    private static readonly Color MutedColor = Color.FromArgb(94, 102, 116);
    private readonly AppSettings settings;
    private readonly TrackBar smoothingSlider = new();
    private readonly NumericUpDown smoothingValue = new();
    private readonly Label saveStatus = new();
    private bool synchronizing;

    public SettingsWindow(AppSettings settings)
    {
        this.settings = settings;
        Text = "SteamDashET settings";
        Name = "SteamDashETSettingsWindow";
        AccessibleName = "SteamDashET settings";
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(450, 300);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(246, 248, 251);
        ForeColor = Color.FromArgb(30, 37, 49);
        Font = new Font("Segoe UI", 10);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24),
            ColumnCount = 1,
            RowCount = 5
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(new Label
        {
            Text = "Gaze smoothing",
            AutoSize = true,
            Font = new Font(Font.FontFamily, 14, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 8)
        }, 0, 0);
        layout.Controls.Add(new Label
        {
            Text = "Smooth the gaze dot over the selected period.",
            AutoSize = true,
            ForeColor = MutedColor,
            Margin = new Padding(0, 0, 0, 12)
        }, 0, 1);

        var controls = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            Margin = Padding.Empty,
            ColumnCount = 3,
            RowCount = 1
        };
        controls.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        controls.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
        controls.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        smoothingSlider.Name = "SmoothingSlider";
        smoothingSlider.AccessibleName = "Gaze smoothing period";
        smoothingSlider.Minimum = 0;
        smoothingSlider.Maximum = 100;
        smoothingSlider.SmallChange = 1;
        smoothingSlider.LargeChange = 10;
        smoothingSlider.TickStyle = TickStyle.None;
        smoothingSlider.AutoSize = false;
        smoothingSlider.Height = 36;
        smoothingSlider.Dock = DockStyle.Fill;
        smoothingSlider.Margin = new Padding(0, 0, 12, 0);
        controls.Controls.Add(smoothingSlider, 0, 0);

        smoothingValue.Name = "SmoothingSeconds";
        smoothingValue.AccessibleName = "Gaze smoothing period in seconds";
        smoothingValue.Minimum = 0;
        smoothingValue.Maximum = 1;
        smoothingValue.DecimalPlaces = 2;
        smoothingValue.Increment = 0.01m;
        smoothingValue.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        smoothingValue.Margin = Padding.Empty;
        controls.Controls.Add(smoothingValue, 1, 0);
        controls.Controls.Add(new Label
        {
            Text = "s",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = MutedColor,
            Margin = new Padding(8, 0, 0, 0)
        }, 2, 0);
        layout.Controls.Add(controls, 0, 2);

        saveStatus.Name = "SettingsSaveStatus";
        saveStatus.AccessibleName = "Settings save status";
        saveStatus.Dock = DockStyle.Fill;
        saveStatus.Margin = Padding.Empty;
        saveStatus.ForeColor = Color.FromArgb(162, 45, 45);
        saveStatus.UseMnemonic = false;
        layout.Controls.Add(saveStatus, 0, 3);

        var close = new Button
        {
            Text = "Close",
            Name = "CloseSettingsButton",
            AccessibleName = "Close settings",
            AutoSize = true,
            MinimumSize = new Size(92, 36),
            Padding = new Padding(12, 4, 12, 4),
            Anchor = AnchorStyles.Right,
            Margin = Padding.Empty,
            UseVisualStyleBackColor = true
        };
        close.Click += (_, _) => Close();
        CancelButton = close;
        layout.Controls.Add(close, 0, 4);
        Controls.Add(layout);

        SynchronizeControls();
        smoothingSlider.ValueChanged += (_, _) => ApplyValue(smoothingSlider.Value / 100.0);
        smoothingValue.ValueChanged += (_, _) => ApplyValue((double)smoothingValue.Value);
    }

    private void ApplyValue(double seconds)
    {
        if (synchronizing) return;
        settings.SmoothingSeconds = seconds;
        SynchronizeControls();
        try
        {
            settings.Save();
            saveStatus.Text = string.Empty;
        }
        catch (Exception exception)
        {
            saveStatus.Text = $"Applied for this session. Could not save settings: {exception.Message}";
        }
    }

    private void SynchronizeControls()
    {
        synchronizing = true;
        try
        {
            smoothingSlider.Value = (int)Math.Round(settings.SmoothingSeconds * 100);
            smoothingValue.Value = (decimal)settings.SmoothingSeconds;
        }
        finally
        {
            synchronizing = false;
        }
    }
}
