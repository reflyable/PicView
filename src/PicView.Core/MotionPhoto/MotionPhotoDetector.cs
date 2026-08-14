using System.Buffers;
using System.Text;
using PicView.Core.DebugTools;

namespace PicView.Core.MotionPhoto;

/// <summary>
/// Detects whether an image file is a motion photo (Google/Samsung/DJI/OPPO style embedded
/// video, Apple/vivo style sidecar file, or .livp container).
/// <para>
/// XMP metadata is located with plain string searches instead of XML parsing, because vendor
/// namespaces vary widely (GCamera, OpCamera, dji, Samsung...). This mirrors the approach
/// proven in other viewers.
/// </para>
/// </summary>
public static class MotionPhotoDetector
{
    private static readonly byte[] SamsungMarkerBytes = Encoding.ASCII.GetBytes("MotionPhoto_Data");
    private static readonly byte[] JpegXmpHeaderBytes = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/");

    /// <summary>Scan up to 32 MB from the file tail when searching for the Samsung trailer marker.</summary>
    private const int SamsungScanWindowBytes = 32 * 1024 * 1024;

    /// <summary>Scan up to 1 MB from the file start when searching for a JPEG XMP packet.</summary>
    private const int JpegXmpScanWindowBytes = 1024 * 1024;

    /// <summary>
    /// Attempts to detect motion photo data for the given file.
    /// </summary>
    /// <param name="fileInfo">The image file to inspect.</param>
    /// <param name="xmpPacket">
    /// Optional XMP packet text (e.g. from Magick.NET). When null and the file is a JPEG,
    /// a lightweight APP1 byte scan is used as fallback.
    /// </param>
    /// <returns>A <see cref="MotionPhotoInfo"/> describing the video location, or null if not a motion photo.</returns>
    public static MotionPhotoInfo? TryDetect(FileInfo fileInfo, string? xmpPacket)
    {
        try
        {
            if (!fileInfo.Exists || fileInfo.Length is 0)
            {
                return null;
            }

            var extension = fileInfo.Extension;
            if (extension.Equals(".livp", StringComparison.OrdinalIgnoreCase))
            {
                return new MotionPhotoInfo { Source = MotionPhotoSource.LivpContainer };
            }

            if (xmpPacket is null &&
                (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                 extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)))
            {
                xmpPacket = ReadJpegXmpPacket(fileInfo);
            }

            if (xmpPacket is { Length: > 0 })
            {
                var fromXmp = TryDetectFromXmp(fileInfo.Length, xmpPacket);
                if (fromXmp is not null)
                {
                    return fromXmp;
                }
            }

            var samsung = TryDetectSamsungTrailer(fileInfo);
            if (samsung is not null)
            {
                return samsung;
            }

            return TryDetectSidecar(fileInfo);
        }
        catch (Exception e)
        {
            DebugHelper.LogDebug(nameof(MotionPhotoDetector), nameof(TryDetect), e);
            return null;
        }
    }

    /// <summary>
    /// Searches the XMP packet text for motion photo metadata.
    /// Supports the new Container:Directory standard (Item:Semantic=MotionPhoto + Item:Length)
    /// and the legacy MicroVideo standard (MicroVideoOffset).
    /// </summary>
    internal static MotionPhotoInfo? TryDetectFromXmp(long fileLength, string xmp)
    {
        // New standard: Item:Semantic = MotionPhoto, with Item:Length holding the video byte count.
        // Handle both element form (<Item:Semantic>MotionPhoto</Item:Semantic>)
        // and attribute form (Item:Semantic="MotionPhoto").
        var semanticIndex = xmp.IndexOf(">MotionPhoto<", StringComparison.Ordinal);
        if (semanticIndex < 0)
        {
            semanticIndex = xmp.IndexOf("\"MotionPhoto\"", StringComparison.Ordinal);
        }

        if (semanticIndex >= 0)
        {
            // Search forward first; fall back to a small backward window so reordered
            // attributes within the same Directory item are still found.
            var markerIndex = xmp.IndexOf("Item:Length", semanticIndex, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                var backwardStart = Math.Max(0, semanticIndex - 1024);
                markerIndex = xmp.IndexOf("Item:Length", backwardStart, semanticIndex - backwardStart, StringComparison.OrdinalIgnoreCase);
            }

            if (markerIndex >= 0)
            {
                var videoLength = ExtractNumberAfter(xmp, markerIndex + "Item:Length".Length);
                if (videoLength is > 0 && videoLength <= fileLength)
                {
                    return new MotionPhotoInfo
                    {
                        Source = MotionPhotoSource.EmbeddedXmp,
                        VideoOffset = fileLength - videoLength.Value,
                        VideoLength = videoLength.Value,
                    };
                }
            }
        }

        // Legacy standard: GCamera:MicroVideoOffset (bytes from end of file)
        var microVideoIndex = xmp.IndexOf("MicroVideoOffset", StringComparison.OrdinalIgnoreCase);
        if (microVideoIndex >= 0)
        {
            var offset = ExtractNumberAfter(xmp, microVideoIndex + "MicroVideoOffset".Length);
            if (offset is > 0 && offset <= fileLength)
            {
                return new MotionPhotoInfo
                {
                    Source = MotionPhotoSource.EmbeddedXmp,
                    VideoOffset = fileLength - offset.Value,
                    VideoLength = offset.Value,
                };
            }
        }

        return null;
    }

    /// <summary>
    /// Scans the tail of the file for the legacy Samsung "MotionPhoto_Data" trailer marker.
    /// The video starts immediately after the marker.
    /// </summary>
    internal static MotionPhotoInfo? TryDetectSamsungTrailer(FileInfo fileInfo)
    {
        var fileLength = fileInfo.Length;
        var minimumSize = SamsungMarkerBytes.Length + 16;
        if (fileLength < minimumSize)
        {
            return null;
        }

        var windowLength = (int)Math.Min(fileLength, SamsungScanWindowBytes);
        var buffer = ArrayPool<byte>.Shared.Rent(windowLength);
        try
        {
            var bytesRead = ReadFileTail(fileInfo, buffer, windowLength);
            if (bytesRead < SamsungMarkerBytes.Length)
            {
                return null;
            }

            var markerIndex = buffer.AsSpan(0, bytesRead).LastIndexOf(SamsungMarkerBytes);
            if (markerIndex < 0)
            {
                return null;
            }

            var videoStart = fileLength - bytesRead + markerIndex + SamsungMarkerBytes.Length;
            if (videoStart >= fileLength)
            {
                return null;
            }

            return new MotionPhotoInfo
            {
                Source = MotionPhotoSource.SamsungTrailer,
                VideoOffset = videoStart,
                VideoLength = fileLength - videoStart,
            };
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Looks for a same-named sidecar video file (.mov preferred, then .mp4) next to the image.
    /// </summary>
    internal static MotionPhotoInfo? TryDetectSidecar(FileInfo fileInfo)
    {
        var directory = fileInfo.DirectoryName;
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        var baseName = Path.GetFileNameWithoutExtension(fileInfo.FullName);
        foreach (var extension in new[] { ".mov", ".mp4" })
        {
            var sidecar = new FileInfo(Path.Combine(directory, baseName + extension));
            if (sidecar.Exists && sidecar.Length > 0)
            {
                return new MotionPhotoInfo
                {
                    Source = MotionPhotoSource.Sidecar,
                    SidecarFile = sidecar,
                };
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the XMP packet from a JPEG file by locating the APP1 segment that starts with the
    /// XMP namespace header. Only the head of the file is scanned.
    /// </summary>
    internal static string? ReadJpegXmpPacket(FileInfo fileInfo)
    {
        var fileLength = fileInfo.Length;
        var minimumSize = JpegXmpHeaderBytes.Length + 4;
        if (fileLength < minimumSize)
        {
            return null;
        }

        var windowLength = (int)Math.Min(fileLength, JpegXmpScanWindowBytes);
        var buffer = ArrayPool<byte>.Shared.Rent(windowLength);
        try
        {
            var bytesRead = ReadFileHead(fileInfo, buffer, windowLength);
            var span = buffer.AsSpan(0, bytesRead);
            var headerIndex = span.IndexOf(JpegXmpHeaderBytes);
            if (headerIndex < 0)
            {
                return null;
            }

            var packetStart = span.Slice(headerIndex).IndexOf((byte)'<');
            if (packetStart < 0)
            {
                return null;
            }

            return Encoding.UTF8.GetString(span.Slice(headerIndex + packetStart));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int ReadFileTail(FileInfo fileInfo, byte[] buffer, int count)
    {
        using var stream = new FileStream(fileInfo.FullName, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite, 4096, FileOptions.SequentialScan);
        stream.Seek(Math.Max(0, stream.Length - count), SeekOrigin.Begin);
        var totalRead = 0;
        while (totalRead < count)
        {
            var read = stream.Read(buffer.AsSpan(totalRead, count - totalRead));
            if (read is 0)
            {
                break;
            }

            totalRead += read;
        }

        return totalRead;
    }

    private static int ReadFileHead(FileInfo fileInfo, byte[] buffer, int count)
    {
        using var stream = new FileStream(fileInfo.FullName, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite, 4096, FileOptions.SequentialScan);
        var totalRead = 0;
        while (totalRead < count)
        {
            var read = stream.Read(buffer.AsSpan(totalRead, count - totalRead));
            if (read is 0)
            {
                break;
            }

            totalRead += read;
        }

        return totalRead;
    }

    /// <summary>
    /// Extracts the first run of ASCII digits found at or after <paramref name="startIndex"/>,
    /// which allows handling both attribute ("Item:Length="123"") and element
    /// ("&lt;Item:Length&gt;123&lt;/Item:Length&gt;") XMP forms without parsing XML.
    /// </summary>
    private static long? ExtractNumberAfter(string text, int startIndex)
    {
        var index = startIndex;
        while (index < text.Length && !char.IsAsciiDigit(text[index]))
        {
            index++;
        }

        if (index >= text.Length)
        {
            return null;
        }

        long value = 0;
        var digitCount = 0;
        while (index < text.Length && char.IsAsciiDigit(text[index]))
        {
            if (value > long.MaxValue / 10)
            {
                return null;
            }

            value = value * 10 + (text[index] - '0');
            digitCount++;
            index++;
        }

        return digitCount is 0 ? null : value;
    }
}
