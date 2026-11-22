using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using KeyTrace.Interop;
using WpfControl = System.Windows.Controls.Control;

namespace KeyTrace.Ui;

/// <summary>
/// 与 offline Dashboard 一脉相承的暗色设计令牌与轻量自定义 WPF 控件样式。
/// </summary>
internal static class DarkTheme
{
    public static readonly FontFamily UiFont = new("Microsoft YaHei UI, Segoe UI, sans-serif");
    public static readonly FontFamily MonoFont = new("Consolas, Cascadia Mono, monospace");

    public static readonly SolidColorBrush BgBrush = Freeze(0x16, 0x16, 0x16);
    public static readonly SolidColorBrush SidebarBrush = Freeze(0x1B, 0x1B, 0x1B);
    public static readonly SolidColorBrush PanelBrush = Freeze(0x1F, 0x1F, 0x1F);
    public static readonly SolidColorBrush ElevatedBrush = Freeze(0x26, 0x26, 0x26);
    public static readonly SolidColorBrush HoverBrush = Freeze(0x2E, 0x2E, 0x2E);
    public static readonly SolidColorBrush PressedBrush = Freeze(0x36, 0x36, 0x36);
    public static readonly SolidColorBrush BorderBrush = Freeze(0x2E, 0x2E, 0x2E);
    public static readonly SolidColorBrush BorderStrongBrush = Freeze(0x42, 0x42, 0x42);

    public static readonly SolidColorBrush TextBrush = Freeze(0xE8, 0xE6, 0xE3);
    public static readonly SolidColorBrush DimBrush = Freeze(0x9A, 0x96, 0x8F);
    public static readonly SolidColorBrush FaintBrush = Freeze(0x6B, 0x67, 0x5F);

    public static readonly SolidColorBrush GreenBrush = Freeze(0x5B, 0xA3, 0x6F);
    public static readonly SolidColorBrush GreenBgBrush = Freeze(0x1D, 0x2E, 0x22);
    public static readonly SolidColorBrush RedBrush = Freeze(0xD4, 0x50, 0x3A);
    public static readonly SolidColorBrush RedBgBrush = Freeze(0x33, 0x1E, 0x1A);
    public static readonly SolidColorBrush AmberBrush = Freeze(0xC9, 0x96, 0x3F);
    public static readonly SolidColorBrush AmberBgBrush = Freeze(0x2C, 0x22, 0x14);
    public static readonly SolidColorBrush BlueBrush = Freeze(0x4A, 0x7F, 0xB5);
    public static readonly SolidColorBrush BlueBgBrush = Freeze(0x1B, 0x2B, 0x3A);

    public static readonly SolidColorBrush HeatNoneBrush = Freeze(0x2A, 0x2A, 0x2A);
    public static readonly SolidColorBrush HeatMildBrush = Freeze(0x4A, 0x3B, 0x39);
    public static readonly SolidColorBrush HeatWarnBrush = Freeze(0x7A, 0x3A, 0x2E);
    public static readonly SolidColorBrush HeatWeakBrush = Freeze(0xA8, 0x40, 0x2F);
    public static readonly SolidColorBrush HeatSevereBrush = Freeze(0xD4, 0x50, 0x3A);
    public static readonly SolidColorBrush HeatInactiveBrush = Freeze(0x21, 0x21, 0x21);

    private static SolidColorBrush Freeze(byte r, byte g, byte b, byte a = 0xFF)
    {
        SolidColorBrush brush = new(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    public static void ApplyDarkTitleBar(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            int enabled = 1;
            _ = NativeMethods.DwmSetWindowAttribute(
                hwnd,
                NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE,
                ref enabled,
                Marshal.SizeOf<int>());
        };
    }

    public static void ApplyWindowResources(Window window)
    {
        window.FontFamily = UiFont;
        window.FontSize = 13;
        window.Foreground = TextBrush;
        window.Background = BgBrush;

        ResourceDictionary res = window.Resources;
        res[typeof(ToolTip)] = CreateToolTipStyle();
        res[typeof(TextBox)] = CreateTextBoxStyle();
        res[typeof(CheckBox)] = CreateCheckBoxStyle();
        res[typeof(ComboBox)] = CreateComboBoxStyle();
        res[typeof(ComboBoxItem)] = CreateComboBoxItemStyle();
        res[typeof(ScrollBar)] = CreateScrollBarStyle();
    }

    public static Button CreateButton(string text, bool primary = false, bool compact = false)
    {
        Button btn = new()
        {
            Content = text,
            Cursor = System.Windows.Input.Cursors.Hand,
            Padding = compact ? new Thickness(10, 5, 10, 5) : new Thickness(14, 7, 14, 7),
            FontSize = 12,
            FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = TextBrush,
            Background = primary ? BlueBrush : ElevatedBrush,
            BorderBrush = primary ? BlueBrush : BorderStrongBrush,
            BorderThickness = new Thickness(1),
            Style = CreateRoundedButtonStyle(
                normalBg: primary ? BlueBrush : ElevatedBrush,
                hoverBg: primary ? Freeze(0x58, 0x8F, 0xC7) : HoverBrush,
                pressedBg: primary ? Freeze(0x3D, 0x6B, 0x99) : PressedBrush,
                border: primary ? BlueBrush : BorderStrongBrush,
                cornerRadius: 6),
        };
        return btn;
    }

    public static Button CreatePillButton(string text, bool active)
    {
        Button btn = new()
        {
            Content = text,
            Cursor = System.Windows.Input.Cursors.Hand,
            Padding = new Thickness(12, 5, 12, 5),
            FontSize = 12,
            FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = active ? TextBrush : DimBrush,
            Style = CreateRoundedButtonStyle(
                normalBg: active ? BlueBgBrush : ElevatedBrush,
                hoverBg: active ? BlueBgBrush : HoverBrush,
                pressedBg: PressedBrush,
                border: active ? BlueBrush : BorderBrush,
                cornerRadius: 5),
        };
        return btn;
    }

    public static void SetPillActive(Button btn, bool active)
    {
        btn.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        btn.Foreground = active ? TextBrush : DimBrush;
        btn.Style = CreateRoundedButtonStyle(
            normalBg: active ? BlueBgBrush : ElevatedBrush,
            hoverBg: active ? BlueBgBrush : HoverBrush,
            pressedBg: PressedBrush,
            border: active ? BlueBrush : BorderBrush,
            cornerRadius: 5);
    }

    public static Border CreateCard(UIElement child, Thickness? padding = null) => new()
    {
        Background = PanelBrush,
        BorderBrush = BorderBrush,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = padding ?? new Thickness(16),
        Child = child,
    };

    public static TextBlock SectionHeader(string text) => new()
    {
        Text = text,
        FontSize = 14,
        FontWeight = FontWeights.SemiBold,
        Foreground = TextBrush,
        Margin = new Thickness(0, 0, 0, 10),
    };

    public static Style CreateRoundedButtonStyle(
        Brush normalBg,
        Brush hoverBg,
        Brush pressedBg,
        Brush border,
        double cornerRadius)
    {
        Style style = new(typeof(Button));
        style.Setters.Add(new Setter(WpfControl.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(WpfControl.BorderBrushProperty, border));
        style.Setters.Add(new Setter(WpfControl.BackgroundProperty, normalBg));
        style.Setters.Add(new Setter(WpfControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
        style.Setters.Add(new Setter(WpfControl.VerticalContentAlignmentProperty, VerticalAlignment.Center));

        FrameworkElementFactory root = new(typeof(Border));
        root.Name = "bd";
        root.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(WpfControl.BackgroundProperty));
        root.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(WpfControl.BorderBrushProperty));
        root.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(WpfControl.BorderThicknessProperty));
        root.SetValue(Border.CornerRadiusProperty, new CornerRadius(cornerRadius));
        root.SetValue(Border.PaddingProperty, new TemplateBindingExtension(WpfControl.PaddingProperty));

        FrameworkElementFactory presenter = new(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        root.AppendChild(presenter);

        ControlTemplate template = new(typeof(Button)) { VisualTree = root };

        Trigger hover = new() { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(WpfControl.BackgroundProperty, hoverBg, "bd"));
        template.Triggers.Add(hover);

        Trigger pressed = new() { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(WpfControl.BackgroundProperty, pressedBg, "bd"));
        template.Triggers.Add(pressed);

        Trigger disabled = new() { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
        template.Triggers.Add(disabled);

        style.Setters.Add(new Setter(WpfControl.TemplateProperty, template));
        return style;
    }

    private static Style CreateToolTipStyle()
    {
        Style style = new(typeof(ToolTip));
        style.Setters.Add(new Setter(WpfControl.BackgroundProperty, ElevatedBrush));
        style.Setters.Add(new Setter(WpfControl.ForegroundProperty, TextBrush));
        style.Setters.Add(new Setter(WpfControl.BorderBrushProperty, BorderStrongBrush));
        style.Setters.Add(new Setter(WpfControl.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(WpfControl.PaddingProperty, new Thickness(10, 6, 10, 6)));
        style.Setters.Add(new Setter(WpfControl.FontSizeProperty, 12.0));
        style.Setters.Add(new Setter(WpfControl.FontFamilyProperty, UiFont));
        return style;
    }

    private static Style CreateTextBoxStyle()
    {
        Style style = new(typeof(TextBox));
        style.Setters.Add(new Setter(WpfControl.BackgroundProperty, ElevatedBrush));
        style.Setters.Add(new Setter(WpfControl.ForegroundProperty, TextBrush));
        style.Setters.Add(new Setter(TextBoxBase.CaretBrushProperty, TextBrush));
        style.Setters.Add(new Setter(TextBoxBase.SelectionBrushProperty, BlueBrush));
        style.Setters.Add(new Setter(WpfControl.BorderBrushProperty, BorderStrongBrush));
        style.Setters.Add(new Setter(WpfControl.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(WpfControl.PaddingProperty, new Thickness(10, 6, 10, 6)));
        style.Setters.Add(new Setter(WpfControl.FontSizeProperty, 12.5));
        style.Setters.Add(new Setter(WpfControl.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        return style;
    }

    private static Style CreateCheckBoxStyle()
    {
        Style style = new(typeof(CheckBox));
        style.Setters.Add(new Setter(WpfControl.ForegroundProperty, TextBrush));
        style.Setters.Add(new Setter(WpfControl.FontSizeProperty, 12.5));
        style.Setters.Add(new Setter(FrameworkElement.CursorProperty, System.Windows.Input.Cursors.Hand));

        FrameworkElementFactory stack = new(typeof(StackPanel));
        stack.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);

        FrameworkElementFactory box = new(typeof(Border));
        box.Name = "box";
        box.SetValue(FrameworkElement.WidthProperty, 16.0);
        box.SetValue(FrameworkElement.HeightProperty, 16.0);
        box.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        box.SetValue(Border.BorderThicknessProperty, new Thickness(1.2));
        box.SetValue(Border.BorderBrushProperty, BorderStrongBrush);
        box.SetValue(Border.BackgroundProperty, ElevatedBrush);
        box.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        box.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));

        FrameworkElementFactory mark = new(typeof(TextBlock));
        mark.Name = "mark";
        mark.SetValue(TextBlock.TextProperty, "✓");
        mark.SetValue(TextBlock.FontSizeProperty, 11.0);
        mark.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
        mark.SetValue(TextBlock.ForegroundProperty, TextBrush);
        mark.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        mark.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        mark.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        box.AppendChild(mark);

        FrameworkElementFactory content = new(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        stack.AppendChild(box);
        stack.AppendChild(content);

        ControlTemplate template = new(typeof(CheckBox)) { VisualTree = stack };

        Trigger checkedTrig = new() { Property = ToggleButton.IsCheckedProperty, Value = true };
        checkedTrig.Setters.Add(new Setter(Border.BackgroundProperty, BlueBrush, "box"));
        checkedTrig.Setters.Add(new Setter(Border.BorderBrushProperty, BlueBrush, "box"));
        checkedTrig.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "mark"));
        template.Triggers.Add(checkedTrig);

        Trigger hoverTrig = new() { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrig.Setters.Add(new Setter(Border.BorderBrushProperty, DimBrush, "box"));
        template.Triggers.Add(hoverTrig);

        style.Setters.Add(new Setter(WpfControl.TemplateProperty, template));
        return style;
    }

    private static Style CreateComboBoxStyle()
    {
        Style style = new(typeof(ComboBox));
        style.Setters.Add(new Setter(WpfControl.BackgroundProperty, ElevatedBrush));
        style.Setters.Add(new Setter(WpfControl.ForegroundProperty, TextBrush));
        style.Setters.Add(new Setter(WpfControl.BorderBrushProperty, BorderStrongBrush));
        style.Setters.Add(new Setter(WpfControl.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(WpfControl.PaddingProperty, new Thickness(10, 6, 26, 6)));
        style.Setters.Add(new Setter(WpfControl.FontSizeProperty, 12.5));
        style.Setters.Add(new Setter(FrameworkElement.CursorProperty, System.Windows.Input.Cursors.Hand));

        // 简化的暗色 ComboBox 模板：主按钮 + 暗色弹出下拉菜单
        FrameworkElementFactory root = new(typeof(Grid));

        FrameworkElementFactory toggle = new(typeof(ToggleButton));
        toggle.Name = "ToggleButton";
        toggle.SetValue(FrameworkElement.FocusableProperty, false);
        toggle.SetValue(ToggleButton.ClickModeProperty, ClickMode.Press);
        toggle.SetBinding(
            ToggleButton.IsCheckedProperty,
            new System.Windows.Data.Binding("IsDropDownOpen")
            {
                Mode = System.Windows.Data.BindingMode.TwoWay,
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent),
            });

        FrameworkElementFactory toggleBd = new(typeof(Border));
        toggleBd.Name = "tbd";
        toggleBd.SetValue(Border.BackgroundProperty, ElevatedBrush);
        toggleBd.SetValue(Border.BorderBrushProperty, BorderStrongBrush);
        toggleBd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        toggleBd.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));

        FrameworkElementFactory toggleGrid = new(typeof(Grid));
        FrameworkElementFactory col1 = new(typeof(ColumnDefinition));
        FrameworkElementFactory col2 = new(typeof(ColumnDefinition));
        col2.SetValue(ColumnDefinition.WidthProperty, new GridLength(24));
        toggleGrid.AppendChild(col1);
        toggleGrid.AppendChild(col2);

        FrameworkElementFactory arrow = new(typeof(TextBlock));
        arrow.SetValue(Grid.ColumnProperty, 1);
        arrow.SetValue(TextBlock.TextProperty, "▾");
        arrow.SetValue(TextBlock.ForegroundProperty, DimBrush);
        arrow.SetValue(TextBlock.FontSizeProperty, 12.0);
        arrow.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        arrow.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        toggleGrid.AppendChild(arrow);
        toggleBd.AppendChild(toggleGrid);

        ControlTemplate toggleTpl = new(typeof(ToggleButton)) { VisualTree = toggleBd };
        Trigger toggleHover = new() { Property = UIElement.IsMouseOverProperty, Value = true };
        toggleHover.Setters.Add(new Setter(Border.BackgroundProperty, HoverBrush, "tbd"));
        toggleTpl.Triggers.Add(toggleHover);
        toggle.SetValue(WpfControl.TemplateProperty, toggleTpl);

        FrameworkElementFactory contentSite = new(typeof(ContentPresenter));
        contentSite.Name = "ContentSite";
        contentSite.SetValue(UIElement.IsHitTestVisibleProperty, false);
        contentSite.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ComboBox.SelectionBoxItemProperty));
        contentSite.SetValue(ContentPresenter.ContentTemplateProperty, new TemplateBindingExtension(ComboBox.SelectionBoxItemTemplateProperty));
        contentSite.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 6, 26, 6));
        contentSite.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        contentSite.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);

        FrameworkElementFactory popup = new(typeof(Popup));
        popup.Name = "Popup";
        popup.SetValue(Popup.PlacementProperty, PlacementMode.Bottom);
        popup.SetValue(Popup.AllowsTransparencyProperty, true);
        popup.SetValue(UIElement.FocusableProperty, false);
        popup.SetValue(Popup.PopupAnimationProperty, PopupAnimation.Slide);
        popup.SetValue(Popup.IsOpenProperty, new TemplateBindingExtension(ComboBox.IsDropDownOpenProperty));

        FrameworkElementFactory dropBorder = new(typeof(Border));
        dropBorder.SetValue(Border.BackgroundProperty, ElevatedBrush);
        dropBorder.SetValue(Border.BorderBrushProperty, BorderStrongBrush);
        dropBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        dropBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        dropBorder.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 3, 0, 0));
        dropBorder.SetBinding(
            FrameworkElement.MinWidthProperty,
            new System.Windows.Data.Binding("ActualWidth")
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent),
            });

        FrameworkElementFactory scroll = new(typeof(ScrollViewer));
        scroll.SetValue(FrameworkElement.MaxHeightProperty, 240.0);
        FrameworkElementFactory items = new(typeof(ItemsPresenter));
        scroll.AppendChild(items);
        dropBorder.AppendChild(scroll);
        popup.AppendChild(dropBorder);

        root.AppendChild(toggle);
        root.AppendChild(contentSite);
        root.AppendChild(popup);

        style.Setters.Add(new Setter(WpfControl.TemplateProperty, new ControlTemplate(typeof(ComboBox)) { VisualTree = root }));
        return style;
    }

    private static Style CreateComboBoxItemStyle()
    {
        Style style = new(typeof(ComboBoxItem));
        style.Setters.Add(new Setter(WpfControl.ForegroundProperty, TextBrush));
        style.Setters.Add(new Setter(WpfControl.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(WpfControl.PaddingProperty, new Thickness(10, 7, 10, 7)));
        style.Setters.Add(new Setter(WpfControl.FontSizeProperty, 12.5));

        FrameworkElementFactory bd = new(typeof(Border));
        bd.Name = "ibd";
        bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(WpfControl.BackgroundProperty));
        bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(WpfControl.PaddingProperty));
        FrameworkElementFactory cp = new(typeof(ContentPresenter));
        bd.AppendChild(cp);

        ControlTemplate tpl = new(typeof(ComboBoxItem)) { VisualTree = bd };
        Trigger hover = new() { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, HoverBrush, "ibd"));
        tpl.Triggers.Add(hover);

        Trigger sel = new() { Property = ComboBoxItem.IsSelectedProperty, Value = true };
        sel.Setters.Add(new Setter(Border.BackgroundProperty, BlueBgBrush, "ibd"));
        tpl.Triggers.Add(sel);

        style.Setters.Add(new Setter(WpfControl.TemplateProperty, tpl));
        return style;
    }

    private static Style CreateScrollBarStyle()
    {
        const string xaml = """
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                   xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                   TargetType="ScrollBar">
              <Setter Property="Width" Value="10"/>
              <Setter Property="MinWidth" Value="10"/>
              <Setter Property="Template">
                <Setter.Value>
                  <ControlTemplate TargetType="ScrollBar">
                    <Border Background="#161616">
                      <Track x:Name="PART_Track" IsDirectionReversed="True">
                        <Track.DecreaseRepeatButton>
                          <RepeatButton Command="ScrollBar.PageUpCommand">
                            <RepeatButton.Template>
                              <ControlTemplate TargetType="RepeatButton">
                                <Border Background="Transparent"/>
                              </ControlTemplate>
                            </RepeatButton.Template>
                          </RepeatButton>
                        </Track.DecreaseRepeatButton>
                        <Track.Thumb>
                          <Thumb>
                            <Thumb.Template>
                              <ControlTemplate TargetType="Thumb">
                                <Border x:Name="b" Background="#3A3A3A" CornerRadius="3" Margin="2"/>
                                <ControlTemplate.Triggers>
                                  <Trigger Property="IsMouseOver" Value="True">
                                    <Setter TargetName="b" Property="Background" Value="#555555"/>
                                  </Trigger>
                                </ControlTemplate.Triggers>
                              </ControlTemplate>
                            </Thumb.Template>
                          </Thumb>
                        </Track.Thumb>
                        <Track.IncreaseRepeatButton>
                          <RepeatButton Command="ScrollBar.PageDownCommand">
                            <RepeatButton.Template>
                              <ControlTemplate TargetType="RepeatButton">
                                <Border Background="Transparent"/>
                              </ControlTemplate>
                            </RepeatButton.Template>
                          </RepeatButton>
                        </Track.IncreaseRepeatButton>
                      </Track>
                    </Border>
                  </ControlTemplate>
                </Setter.Value>
              </Setter>
            </Style>
            """;
        return (Style)System.Windows.Markup.XamlReader.Parse(xaml);
    }
}
