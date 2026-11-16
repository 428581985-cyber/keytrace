using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace KeyTrace.Ui;

/// <summary>
/// 托盘图标。它不是装饰：常驻采集最危险的情况是"用户忘了它在录"，
/// 图标颜色就是"现在有没有在记"的唯一可见指示，绿 = 录制中，灰 = 已暂停。
/// </summary>
internal sealed class TrayApplication : ApplicationContext
{
    private const string AppName = "KeyTrace";

    private readonly NotifyIcon _tray;
    private readonly Icon _recordingIcon;
    private readonly Icon _pausedIcon;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly string _dataDirectory;

    public TrayApplication(string dataDirectory)
    {
        _dataDirectory = dataDirectory;

        _recordingIcon = CreateDotIcon(Color.FromArgb(76, 175, 80));
        _pausedIcon = CreateDotIcon(Color.FromArgb(140, 140, 140));

        _toggleItem = new ToolStripMenuItem("暂停录制", null, (_, _) => ToggleRequested?.Invoke());

        ContextMenuStrip menu = new();
        menu.Items.Add(new ToolStripMenuItem(AppName) { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new ToolStripMenuItem("打开数据目录", null, (_, _) => OpenDataFolder()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => ExitRequested?.Invoke()));

        _tray = new NotifyIcon
        {
            Icon = _recordingIcon,
            Text = $"{AppName} · 录制中",
            Visible = true,
            ContextMenuStrip = menu,
        };

        // 双击也能切换，比在菜单里找更快
        _tray.DoubleClick += (_, _) => ToggleRequested?.Invoke();
    }

    public event Action? ToggleRequested;

    public event Action? ExitRequested;

    public void SetPaused(bool paused)
    {
        _tray.Icon = paused ? _pausedIcon : _recordingIcon;
        _tray.Text = paused ? $"{AppName} · 已暂停" : $"{AppName} · 录制中";
        _toggleItem.Text = paused ? "继续录制" : "暂停录制";
    }

    public void Notify(string message) => _tray.ShowBalloonTip(2000, AppName, message, ToolTipIcon.Info);

    public void Shutdown()
    {
        _tray.Visible = false;
        _tray.Dispose();
        _recordingIcon.Dispose();
        _pausedIcon.Dispose();
    }

    private void OpenDataFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = _dataDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Notify($"打不开目录：{ex.Message}");
        }
    }

    /// <summary>临时画一个圆点当图标，省掉一个二进制 .ico 资源——源码里能看懂的都留在源码里。</summary>
    private static Icon CreateDotIcon(Color color)
    {
        using Bitmap bitmap = new(16, 16);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            using SolidBrush brush = new(color);
            graphics.FillEllipse(brush, 2, 2, 12, 12);
        }

        IntPtr handle = bitmap.GetHicon();
        return Icon.FromHandle(handle);
    }
}
