using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KeyTrace.Analysis;

namespace KeyTrace.Ui;

internal enum HeatmapDisplayMode
{
    Shuangpin,
    English,
    Symbols,
}

/// <summary>
/// 物理键盘热力图控件（ANSI 60%，5 行精确对齐）。
/// 复用 <see cref="Dashboard"/> 的热力着色阶梯与物理比例，
/// 支持小鹤双拼键帽标注、纯英文视图及编程符号视图。
/// </summary>
internal sealed class KeyboardHeatmapControl : UserControl
{
    private readonly record struct KeyDef(string Label, int Vk, double WidthUnits);

    private const double UnitWidth = 50;
    private const double GapWidth = 6;
    private const double KeyHeight = 50;

    private static readonly KeyDef[][] Rows =
    [
        [
            new("`", 192, 1), new("1", 49, 1), new("2", 50, 1), new("3", 51, 1),
            new("4", 52, 1), new("5", 53, 1), new("6", 54, 1), new("7", 55, 1),
            new("8", 56, 1), new("9", 57, 1), new("0", 48, 1), new("-", 189, 1),
            new("=", 187, 1), new("Backspace", 8, 2.1),
        ],
        [
            new("Tab", 9, 1.6), new("Q", 81, 1), new("W", 87, 1), new("E", 69, 1),
            new("R", 82, 1), new("T", 84, 1), new("Y", 89, 1), new("U", 85, 1),
            new("I", 73, 1), new("O", 79, 1), new("P", 80, 1), new("[", 219, 1),
            new("]", 221, 1), new("\\", 220, 1.5),
        ],
        [
            new("Caps", 20, 1.9), new("A", 65, 1), new("S", 83, 1), new("D", 68, 1),
            new("F", 70, 1), new("G", 71, 1), new("H", 72, 1), new("J", 74, 1),
            new("K", 75, 1), new("L", 76, 1), new(";", 186, 1), new("'", 222, 1),
            new("Enter", 13, 2.2),
        ],
        [
            new("Shift", 16, 2.55), new("Z", 90, 1), new("X", 88, 1), new("C", 67, 1),
            new("V", 86, 1), new("B", 66, 1), new("N", 78, 1), new("M", 77, 1),
            new(",", 188, 1), new(".", 190, 1), new("/", 191, 1), new("RShift", 160, 2.55),
        ],
        [
            new("Ctrl", 17, 1.3), new("Win", 91, 1.2), new("Alt", 18, 1.2),
            new("Space", 32, 6.4), new("RAlt", 164, 1.2), new("RWin", 92, 1.2),
            new("Menu", 93, 1.2), new("RCtrl", 162, 1.4),
        ],
    ];

    private static readonly Dictionary<int, string> XiaoheLabels = new()
    {
        [81] = "iu",
        [87] = "ei",
        [69] = "e",
        [82] = "uan",
        [84] = "ue",
        [89] = "un",
        [85] = "sh/u",
        [73] = "ch/i",
        [79] = "uo/o",
        [80] = "ie",
        [65] = "a",
        [83] = "ong",
        [68] = "ai",
        [70] = "en",
        [71] = "eng",
        [72] = "ang",
        [74] = "an",
        [75] = "ing/uai",
        [76] = "iang",
        [90] = "ou",
        [88] = "ia/ua",
        [67] = "ao",
        [86] = "zh/ui",
        [66] = "in",
        [78] = "iao",
        [77] = "ian",
    };

    private readonly StackPanel _rowsPanel;
    private IReadOnlyDictionary<int, KeyCellMetric> _metrics = new Dictionary<int, KeyCellMetric>();
    private HeatmapDisplayMode _mode = HeatmapDisplayMode.Shuangpin;
    private int? _selectedVk;

    public KeyboardHeatmapControl()
    {
        _rowsPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
        };

        Viewbox box = new()
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            Child = _rowsPanel,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        Border frame = new()
        {
            Background = DarkTheme.PanelBrush,
            BorderBrush = DarkTheme.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18),
            Child = box,
        };

        Content = frame;
        Rebuild();
    }

    public event Action<KeyCellMetric?, char?>? KeySelected;

    public HeatmapDisplayMode DisplayMode
    {
        get => _mode;
        set
        {
            if (_mode == value)
            {
                return;
            }

            _mode = value;
            Rebuild();
        }
    }

    public int? SelectedVk
    {
        get => _selectedVk;
        set
        {
            _selectedVk = value;
            Rebuild();
        }
    }

    public void UpdateData(IReadOnlyDictionary<int, KeyCellMetric> metrics)
    {
        _metrics = metrics;
        Rebuild();
    }

    private void Rebuild()
    {
        _rowsPanel.Children.Clear();

        for (int r = 0; r < Rows.Length; r++)
        {
            KeyDef[] row = Rows[r];
            StackPanel rowPanel = new()
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, r == Rows.Length - 1 ? 0 : GapWidth),
            };

            for (int c = 0; c < row.Length; c++)
            {
                KeyDef key = row[c];
                double width = UnitWidth * key.WidthUnits + (key.WidthUnits - 1) * GapWidth;
                _metrics.TryGetValue(key.Vk, out KeyCellMetric? metric);

                bool isLetter = key.Vk is >= 65 and <= 90;
                bool isSymbolOrDigit = KeyCatalog.Role(key.Vk) is KeyRole.Digit or KeyRole.Symbol;
                bool dimmed = _mode == HeatmapDisplayMode.Symbols
                    ? !isSymbolOrDigit && (metric is null || metric.Presses == 0)
                    : !isLetter && !isSymbolOrDigit && metric is null;

                bool selected = _selectedVk == key.Vk;
                Brush bg = dimmed || metric is null
                    ? DarkTheme.HeatInactiveBrush
                    : ToneOf(metric, _mode);

                Border cap = new()
                {
                    Width = width,
                    Height = KeyHeight,
                    Margin = new Thickness(0, 0, c == row.Length - 1 ? 0 : GapWidth, 0),
                    Background = bg,
                    BorderBrush = selected ? DarkTheme.BlueBrush : DarkTheme.BorderStrongBrush,
                    BorderThickness = new Thickness(selected ? 2 : 1),
                    CornerRadius = new CornerRadius(6),
                    Cursor = isLetter ? Cursors.Hand : Cursors.Arrow,
                };

                StackPanel inner = new()
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };

                TextBlock title = new()
                {
                    Text = key.Label,
                    FontSize = key.Label.Length > 2 ? 12 : 13.5,
                    FontWeight = isLetter ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = dimmed ? DarkTheme.FaintBrush : DarkTheme.TextBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                inner.Children.Add(title);

                string? subText = BuildSubLabel(key, metric);
                if (!string.IsNullOrEmpty(subText))
                {
                    TextBlock sub = new()
                    {
                        Text = subText,
                        FontSize = 11,
                        FontFamily = DarkTheme.MonoFont,
                        Foreground = dimmed ? DarkTheme.FaintBrush : DarkTheme.DimBrush,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 1, 0, 0),
                    };
                    inner.Children.Add(sub);
                }

                cap.Child = inner;

                if (metric is not null)
                {
                    cap.ToolTip = BuildTooltip(key, metric);
                }

                if (isLetter)
                {
                    int capturedVk = key.Vk;
                    char letter = char.ToLowerInvariant((char)key.Vk);
                    cap.MouseLeftButtonUp += (_, _) =>
                    {
                        _selectedVk = capturedVk;
                        Rebuild();
                        KeySelected?.Invoke(metric, letter);
                    };
                }

                rowPanel.Children.Add(cap);
            }

            _rowsPanel.Children.Add(rowPanel);
        }
    }

    private string? BuildSubLabel(KeyDef key, KeyCellMetric? metric)
    {
        if (_mode == HeatmapDisplayMode.Shuangpin && XiaoheLabels.TryGetValue(key.Vk, out string? sp))
        {
            return sp;
        }

        if (metric is not null && metric.Presses > 0)
        {
            if (_mode == HeatmapDisplayMode.Symbols && KeyCatalog.Role(key.Vk) is KeyRole.Digit or KeyRole.Symbol)
            {
                return metric.Typos > 0
                    ? string.Create(CultureInfo.InvariantCulture, $"{metric.TypoRate * 100:0.#}%")
                    : string.Create(CultureInfo.InvariantCulture, $"{metric.Presses}");
            }

            if (KeyCatalog.Role(key.Vk) == KeyRole.Letter)
            {
                return metric.Typos > 0
                    ? string.Create(CultureInfo.InvariantCulture, $"{metric.TypoRate * 100:0.#}%")
                    : string.Create(CultureInfo.InvariantCulture, $"{metric.Presses}");
            }
        }

        return null;
    }

    private static string BuildTooltip(KeyDef key, KeyCellMetric metric)
    {
        string header = XiaoheLabels.TryGetValue(key.Vk, out string? sp)
            ? $"{key.Label}（小鹤：{sp}）"
            : key.Label;
        string scoreText = metric.WeakScore is double s
            ? s.ToString("0.00", CultureInfo.InvariantCulture)
            : "样本不足或零错误";
        string wpmText = metric.Wpm > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{metric.MedianIkiMs:0} ms（等效 {metric.Wpm:0.0} wpm）")
            : "无有效速度样本";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{header}\n"
            + $"击键：{metric.Presses:N0} 次 · 确认纠错：{metric.Typos:N0} 次（{metric.TypoRate * 100:0.0}%）\n"
            + $"中位间隔：{wpmText}\n"
            + $"弱键分：{scoreText}");
    }

    private static Brush ToneOf(KeyCellMetric metric, HeatmapDisplayMode mode)
    {
        if (mode == HeatmapDisplayMode.Symbols && metric.Role is not (KeyRole.Digit or KeyRole.Symbol))
        {
            return DarkTheme.HeatInactiveBrush;
        }

        double? score = metric.WeakScore;
        if (score is null || score <= 0)
        {
            if (metric.Typos > 0 && metric.Presses > 0)
            {
                return DarkTheme.HeatMildBrush;
            }

            return DarkTheme.HeatNoneBrush;
        }

        if (score < 0.15) return DarkTheme.HeatMildBrush;
        if (score < 0.30) return DarkTheme.HeatWarnBrush;
        if (score < 0.50) return DarkTheme.HeatWeakBrush;
        return DarkTheme.HeatSevereBrush;
    }
}
