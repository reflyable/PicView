using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PicView.Core.DebugTools;

namespace PicView.Avalonia.MotionPhoto;

/// <summary>
/// Renders motion photo video frames supplied by libvlc software video callbacks
/// (MediaPlayer.SetVideoCallbacks). Frames arrive as BGRA32 ("RV32") bytes and are drawn
/// letterboxed into the control. This works on every display stack, including Wayland
/// (where native child-window embedding is impossible with libvlc 3.x), and lets the
/// video participate in the normal Avalonia compositor (zoom, rotation, overlays).
/// </summary>
public sealed class MotionPhotoVideoSurface : Control
{
    private WriteableBitmap? _frameBitmap;

    /// <summary>
    /// Ensures the frame bitmap matches the given video size. Must be called on the UI thread.
    /// </summary>
    public void EnsureBitmap(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (_frameBitmap is { PixelSize.Width: var w, PixelSize.Height: var h } && w == width && h == height)
        {
            return;
        }

        _frameBitmap?.Dispose();
        _frameBitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);
    }

    /// <summary>
    /// Copies a BGRA32 frame into the bitmap and invalidates the visual. UI thread only.
    /// </summary>
    public void UpdateFrame(byte[] bgra, int width, int height)
    {
        try
        {
            EnsureBitmap(width, height);
            var bitmap = _frameBitmap;
            if (bitmap is null)
            {
                return;
            }

            using var framebuffer = bitmap.Lock();
            var rowBytes = framebuffer.RowBytes;
            var srcRowBytes = width * 4;
            if (rowBytes == srcRowBytes && bgra.Length >= rowBytes * height)
            {
                System.Runtime.InteropServices.Marshal.Copy(bgra, 0, framebuffer.Address, rowBytes * height);
            }
            else
            {
                // Copy row by row to handle potential framebuffer padding
                for (var y = 0; y < height; y++)
                {
                    var srcOffset = y * srcRowBytes;
                    if (srcOffset + srcRowBytes > bgra.Length)
                    {
                        break;
                    }

                    System.Runtime.InteropServices.Marshal.Copy(
                        bgra, srcOffset, framebuffer.Address + y * rowBytes, srcRowBytes);
                }
            }

            InvalidateVisual();
        }
        catch (Exception e)
        {
            DebugHelper.LogDebug(nameof(MotionPhotoVideoSurface), nameof(UpdateFrame), e);
        }
    }

    /// <summary>
    /// Drops the current frame so the surface renders nothing.
    /// </summary>
    public void Clear()
    {
        _frameBitmap?.Dispose();
        _frameBitmap = null;
        InvalidateVisual();
    }

    public sealed override void Render(DrawingContext context)
    {
        base.Render(context);

        var bitmap = _frameBitmap;
        if (bitmap is null)
        {
            return;
        }

        var viewPort = new Rect(Bounds.Size);
        var sourceSize = bitmap.Size;
        var scale = Stretch.Uniform.CalculateScaling(Bounds.Size, sourceSize);
        var scaledSize = sourceSize * scale;
        var destRect = viewPort
            .CenterRect(new Rect(scaledSize))
            .Intersect(viewPort);
        var sourceRect = new Rect(sourceSize)
            .CenterRect(new Rect(destRect.Size / scale));

        context.DrawImage(bitmap, sourceRect, destRect);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Clear();
        base.OnDetachedFromVisualTree(e);
    }
}
