#nullable enable

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SteamDashET;

internal sealed class ControlWindow : Form
{
    private static readonly Color MutedColor = Color.FromArgb(94, 102, 116);
    private static readonly Color ReadyColor = Color.FromArgb(24, 112, 78);
    private static readonly Color GazeColor = Color.FromArgb(255, 70, 195);

    private readonly AppSettings settings = AppSettings.Load();
    private readonly VrController controller;
    private readonly Label eyeStatus = CreateStatusLabel("Waiting to connect");
    private readonly Label dashboardStatus = CreateStatusLabel("Waiting to connect");
    private readonly Label detail = new();
    private readonly Label dotStatus = new();
    private readonly DotIndicator dotIndicator = new();
    private readonly Button toggle = new();
    private readonly Button settingsButton = new();
    private readonly ToolTip toolTip = new();
    private readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 200 };
    private SettingsWindow? settingsWindow;
    private Task transitionTask = Task.CompletedTask;
    private bool transitioning;
    private bool closing;
    private bool allowClose;
    private string transitionText = "Starting…";
    private string? operationError;

    public ControlWindow()
    {
        controller = new VrController(settings);
        Text = "SteamDashET";
        Name = "SteamDashETControlWindow";
        AccessibleName = "SteamDashET control window";
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(530, 350);
        MinimumSize = new Size(490, 375);
        StartPosition = FormStartPosition.CenterScreen;
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

        var header = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            ColumnCount = 2,
            RowCount = 1
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(new Label
        {
            Text = "SteamDashET",
            AutoSize = true,
            Font = new Font(Font.FontFamily, 18, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 3)
        }, 0, 0);
        settingsButton.Text = "\u2699";
        settingsButton.Name = "SettingsButton";
        settingsButton.AccessibleName = "Settings";
        settingsButton.Font = new Font("Segoe UI Symbol", 14);
        settingsButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        settingsButton.Size = new Size(38, 36);
        settingsButton.Margin = new Padding(12, 0, 0, 0);
        settingsButton.UseVisualStyleBackColor = true;
        settingsButton.Click += (_, _) => OpenSettings();
        toolTip.SetToolTip(settingsButton, "Settings");
        header.Controls.Add(settingsButton, 1, 0);
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(new Label
        {
            Text = "Eye tracking for your SteamVR dashboard",
            AutoSize = true,
            ForeColor = MutedColor,
            Margin = new Padding(0, 0, 0, 20)
        }, 0, 1);

        var statuses = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            BackColor = Color.White,
            Padding = new Padding(16, 12, 16, 12),
            Margin = Padding.Empty,
            ColumnCount = 2,
            RowCount = 2
        };
        statuses.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 124));
        statuses.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statuses.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        statuses.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        statuses.Controls.Add(CreateCaption("Eye tracking"), 0, 0);
        statuses.Controls.Add(eyeStatus, 1, 0);
        statuses.Controls.Add(CreateCaption("Dashboard"), 0, 1);
        statuses.Controls.Add(dashboardStatus, 1, 1);
        layout.Controls.Add(statuses, 0, 2);

        detail.Dock = DockStyle.Fill;
        detail.Margin = new Padding(0, 16, 0, 12);
        detail.ForeColor = MutedColor;
        detail.Text = "Connecting to SteamVR…";
        detail.AccessibleName = "Connection details";
        detail.UseMnemonic = false;
        layout.Controls.Add(detail, 0, 3);

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            Margin = Padding.Empty,
            ColumnCount = 2,
            RowCount = 1
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var legend = new FlowLayoutPanel
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            WrapContents = false,
            Margin = Padding.Empty
        };
        dotIndicator.Margin = new Padding(0, 4, 8, 0);
        dotStatus.AutoSize = true;
        dotStatus.Margin = Padding.Empty;
        dotStatus.ForeColor = MutedColor;
        dotStatus.Text = "Gaze dot hidden";
        legend.Controls.Add(dotIndicator);
        legend.Controls.Add(dotStatus);
        footer.Controls.Add(legend, 0, 0);

        toggle.Text = "Start";
        toggle.Name = "StartStopButton";
        toggle.AccessibleName = "Start or stop gaze tracking";
        toggle.AutoSize = true;
        toggle.MinimumSize = new Size(108, 38);
        toggle.Padding = new Padding(12, 4, 12, 4);
        toggle.Margin = new Padding(12, 0, 0, 0);
        toggle.UseVisualStyleBackColor = true;
        toggle.Click += (_, _) => BeginTransition(!controller.Status.IsRunning);
        footer.Controls.Add(toggle, 1, 0);
        layout.Controls.Add(footer, 0, 4);
        Controls.Add(layout);

        refreshTimer.Tick += (_, _) => RefreshStatus();
        Shown += (_, _) =>
        {
            if (closing) return;
            refreshTimer.Start();
            BeginTransition(start: true);
        };
        FormClosing += HandleClosing;
        RefreshStatus();
    }

    private static Label CreateCaption(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = new Padding(0, 5, 12, 5)
    };

    private static Label CreateStatusLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = MutedColor,
        Margin = new Padding(0, 5, 0, 5),
        UseMnemonic = false
    };

    private void BeginTransition(bool start)
    {
        if (closing || transitioning) return;
        transitionTask = ChangeRunningStateAsync(start);
    }

    private void OpenSettings()
    {
        if (closing) return;
        if (settingsWindow is { IsDisposed: false })
        {
            settingsWindow.Activate();
            return;
        }

        settingsWindow = new SettingsWindow(settings);
        settingsWindow.FormClosed += (_, _) => settingsWindow = null;
        settingsWindow.Show(this);
    }

    private async Task ChangeRunningStateAsync(bool start)
    {
        transitioning = true;
        transitionText = start ? "Starting…" : "Stopping…";
        operationError = null;
        RefreshStatus();
        try
        {
            if (start) await controller.StartAsync();
            else await controller.StopAsync();
        }
        catch (Exception exception)
        {
            operationError = $"Could not {(start ? "start" : "stop")}: {exception.Message}";
        }
        finally
        {
            transitioning = false;
            if (!IsDisposed) RefreshStatus();
        }
    }

    private void RefreshStatus()
    {
        VrStatus status = controller.Status;
        eyeStatus.Text = status.EyeTracking;
        eyeStatus.ForeColor = status.EyeTrackingReady ? ReadyColor : MutedColor;
        dashboardStatus.Text = status.Dashboard;
        dashboardStatus.ForeColor = status.DashboardOpen ? ReadyColor : MutedColor;
        detail.Text = closing ? "Stopping gaze tracking and removing the dashboard dot…"
            : operationError ?? status.Detail;
        dotStatus.Text = status.DotVisible ? "Gaze dot visible" : "Gaze dot hidden";
        dotIndicator.DotColor = status.DotVisible ? GazeColor : Color.FromArgb(175, 183, 196);
        toggle.Enabled = !transitioning && !closing;
        settingsButton.Enabled = !closing;
        toggle.Text = closing ? "Stopping…" : transitioning ? transitionText
            : status.IsRunning ? "Stop" : "Start";
    }

    private async void HandleClosing(object? sender, FormClosingEventArgs e)
    {
        if (allowClose) return;
        e.Cancel = true;
        if (closing) return;
        closing = true;
        settingsWindow?.Close();
        refreshTimer.Stop();
        RefreshStatus();
        try
        {
            // Finish an in-flight start before stopping, so it cannot reopen VR after cleanup.
            await transitionTask;
            await controller.StopAsync();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.WriteLine($"SteamDashET shutdown: {exception}");
        }
        finally
        {
            allowClose = true;
            // Defer even when StopAsync completed synchronously inside FormClosing.
            BeginInvoke(new Action(Close));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            refreshTimer.Dispose();
            toolTip.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class DotIndicator : Control
    {
        private Color dotColor = Color.FromArgb(175, 183, 196);

        public DotIndicator()
        {
            Size = new Size(13, 13);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer, true);
            TabStop = false;
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Color DotColor
        {
            get => dotColor;
            set
            {
                if (dotColor == value) return;
                dotColor = value;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(dotColor);
            e.Graphics.FillEllipse(brush, 1, 1, Width - 2, Height - 2);
        }
    }
}
