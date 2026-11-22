using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace KeyTrace.Ui;

/// <summary>
/// 托盘图标控制器（纯 NotifyIcon，消息循环交给 WPF Application）。
/// 图标颜色实时指示采集状态：绿 = 录制中，灰 = 已暂停；左键/双击唤起桌面主窗口。
/// </summary>
internal sealed class TrayApplication : IDisposable
{
    private const string AppName = "KeyTrace";

    private readonly NotifyIcon _tray;
    private readonly Icon _recordingIcon;
    private readonly Icon _pausedIcon;
    private readonly ToolStripMenuItem _stateItem;
    private readonly ToolStripMenuItem _countItem;
    private readonly ToolStripMenuItem _miniHudItem;
    private readonly ToolStripMenuItem _togglePauseItem;
    private readonly string _dataDirectory;
    private bool _paused;
    private long _eventCount;
    private bool _disposed;

    public TrayApplication(string dataDirectory, bool miniHudChecked)
    {
        _dataDirectory = dataDirectory;

        _recordingIcon = CreateDotIcon(Color.FromArgb(76, 175, 80));
        _pausedIcon = CreateDotIcon(Color.FromArgb(140, 140, 140));

        _stateItem = new ToolStripMenuItem($"{AppName} · 录制中") { Enabled = false };
        _countItem = new ToolStripMenuItem("本次录制：0 事件") { Enabled = false };

        ToolStripMenuItem openMainItem = new("打开 KeyTrace 主窗口", null, (_, _) => OpenWindowRequested?.Invoke());
        openMainItem.Font = new Font(openMainItem.Font, FontStyle.Bold);

        _miniHudItem = new ToolStripMenuItem("显示悬浮状态条", null, (_, _) => ToggleMiniHudRequested?.Invoke())
        {
            Checked = miniHudChecked,
        };

        _togglePauseItem = new ToolStripMenuItem("暂停录制 (Ctrl+Alt+P)", null, (_, _) => TogglePauseRequested?.Invoke());

        ContextMenuStrip menu = new();
        menu.Items.Add(_stateItem);
        menu.Items.Add(_countItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(openMainItem);
        menu.Items.Add(_miniHudItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_togglePauseItem);
        menu.Items.Add(new ToolStripMenuItem("打开网页练习", null, (_, _) => OpenWebPracticeRequested?.Invoke()));
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

        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                OpenWindowRequested?.Invoke();
            }
        };
        _tray.DoubleClick += (_, _) => OpenWindowRequested?.Invoke();
    }

    public event Action? OpenWindowRequested;
    public event Action? ToggleMiniHudRequested;
    public event Action? TogglePauseRequested;
    public event Action? OpenWebPracticeRequested;
    public event Action? ExitRequested;

    public void SetPaused(bool paused)
    {
        _paused = paused;
        _tray.Icon = paused ? _pausedIcon : _recordingIcon;
        _stateItem.Text = paused ? $"{AppName} · 已暂停" : $"{AppName} · 录制中";
        _togglePauseItem.Text = paused ? "继续录制 (Ctrl+Alt+P)" : "暂停录制 (Ctrl+Alt+P)";
        UpdateStatusText();
    }

    public void SetMiniHudVisible(bool visible)
    {
        _miniHudItem.Checked = visible;
    }

    public void SetEventCount(long count)
    {
        if (count == _eventCount)
        {
            return;
        }

        _eventCount = count;
        _countItem.Text = $"本次录制：{count:N0} 事件";
        UpdateStatusText();
    }

    public void Notify(string message) => _tray.ShowBalloonTip(2000, AppName, message, ToolTipIcon.Info);

    public void Shutdown()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tray.Visible = false;
        _tray.Dispose();
        _recordingIcon.Dispose();
        _pausedIcon.Dispose();
    }

    public void Dispose() => Shutdown();

    private void UpdateStatusText()
    {
        string state = _paused ? "已暂停" : "录制中";
        string text = _eventCount > 0
            ? $"{AppName} · {state} ({_eventCount} 事件)"
            : $"{AppName} · {state}";
        _tray.Text = text.Length > 63 ? text[..63] : text;
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
