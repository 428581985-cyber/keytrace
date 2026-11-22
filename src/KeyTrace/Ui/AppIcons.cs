using System;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KeyTrace.Interop;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingColor = System.Drawing.Color;
using DrawingGraphics = System.Drawing.Graphics;
using DrawingIcon = System.Drawing.Icon;
using DrawingSize = System.Drawing.Size;
using DrawingSolidBrush = System.Drawing.SolidBrush;
using SmoothingMode = System.Drawing.Drawing2D.SmoothingMode;

namespace KeyTrace.Ui;

/// <summary>
/// 从程序集内嵌的多分辨率 .ico 资源加载托盘图标与 WPF 窗口品牌图标，
/// 避免 GDI+ 缩放锯齿及 GetHicon() 造成的非托管句柄泄漏。
/// </summary>
internal static class AppIcons
{
    internal const string ActiveResourceName = "KeyTrace.assets.keytrace.ico";
    internal const string PausedResourceName = "KeyTrace.assets.keytrace-paused.ico";

    /// <summary>为 WinForms 托盘加载匹配系统 DPI 缩放尺寸的图标。</summary>
    public static DrawingIcon LoadTrayIcon(bool paused)
    {
        string resourceName = paused ? PausedResourceName : ActiveResourceName;
        DrawingSize smallSize = System.Windows.Forms.SystemInformation.SmallIconSize;
        DrawingSize targetSize = new(Math.Max(16, smallSize.Width), Math.Max(16, smallSize.Height));

        try
        {
            using Stream? stream = typeof(AppIcons).Assembly.GetManifestResourceStream(resourceName);
            if (stream is not null)
            {
                return new DrawingIcon(stream, targetSize);
            }
        }
        catch (Exception)
        {
            // 嵌入资源读取失败时走下方抗锯齿兜底图标
        }

        return CreateFallbackDotIcon(
            paused ? DrawingColor.FromArgb(140, 140, 140) : DrawingColor.FromArgb(76, 175, 80),
            targetSize);
    }

    /// <summary>为 WPF 窗口及侧边栏品牌区加载高分辨率图标帧（优先 256×256）。</summary>
    public static ImageSource? LoadWindowIcon(bool paused = false)
    {
        string resourceName = paused ? PausedResourceName : ActiveResourceName;
        try
        {
            using Stream? stream = typeof(AppIcons).Assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                return null;
            }

            IconBitmapDecoder decoder = new(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            BitmapFrame? best = decoder.Frames
                .OrderByDescending(f => f.PixelWidth)
                .FirstOrDefault();

            if (best is null)
            {
                return null;
            }

            // OnLoad 已经把像素解码到内存，安全 Freeze 供跨控件复用
            if (!best.IsFrozen && best.CanFreeze)
            {
                best.Freeze();
            }

            return best;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static DrawingIcon CreateFallbackDotIcon(DrawingColor color, DrawingSize size)
    {
        int dim = Math.Max(16, Math.Min(size.Width, size.Height));
        using DrawingBitmap bitmap = new(dim, dim);
        using (DrawingGraphics graphics = DrawingGraphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(DrawingColor.Transparent);
            using DrawingSolidBrush brush = new(color);
            float pad = dim * 0.125f;
            graphics.FillEllipse(brush, pad, pad, dim - pad * 2, dim - pad * 2);
        }

        IntPtr hIcon = bitmap.GetHicon();
        try
        {
            using DrawingIcon borrowed = DrawingIcon.FromHandle(hIcon);
            return (DrawingIcon)borrowed.Clone();
        }
        finally
        {
            _ = NativeMethods.DestroyIcon(hIcon);
        }
    }
}
