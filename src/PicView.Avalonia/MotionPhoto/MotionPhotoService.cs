using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using PicView.Core.DebugTools;

namespace PicView.Avalonia.MotionPhoto;

/// <summary>
/// Owns the process-wide <see cref="LibVLC"/> instance used for motion photo playback.
/// Initialization is lazy and failure is remembered, so a missing or broken native libvlc
/// simply degrades motion photos to regular still images instead of breaking the viewer.
/// </summary>
public static class MotionPhotoService
{
    private static readonly object InitLock = new();
    private static LibVLC? _libVlc;
    private static bool _initFailed;

    /// <summary>
    /// Video playback is supported on all desktop platforms: frames are produced by libvlc
    /// software video callbacks and rendered in the Avalonia compositor, so no native window
    /// embedding is needed (which also means Wayland sessions are fully supported).
    /// Playback still requires libvlc to be installed/available.
    /// </summary>
    public static bool IsPlaybackSupported =>
        OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    /// <summary>
    /// Attempts to get (or lazily create) the shared <see cref="LibVLC"/> instance.
    /// </summary>
    /// <returns>False when playback is unavailable; callers should fall back to the still image.</returns>
    public static bool TryGetLibVlc(out LibVLC? libVlc)
    {
        if (_libVlc is not null)
        {
            libVlc = _libVlc;
            return true;
        }

        if (!IsPlaybackSupported || _initFailed)
        {
            libVlc = null;
            return false;
        }

        lock (InitLock)
        {
            if (_libVlc is not null)
            {
                libVlc = _libVlc;
                return true;
            }

            if (_initFailed)
            {
                libVlc = null;
                return false;
            }

            try
            {
                InitializeNativeLibrary();
                _libVlc = new LibVLC(
                    "--no-video-title-show",
                    "--no-stats",
                    "--quiet");
                libVlc = _libVlc;
                return true;
            }
            catch (Exception e)
            {
                DebugHelper.LogDebug(nameof(MotionPhotoService), nameof(TryGetLibVlc), e);
                _initFailed = true;
                libVlc = null;
                return false;
            }
        }
    }

    /// <summary>
    /// On Windows, points libvlc at the native libraries deployed by the VideoLAN.LibVLC.Windows
    /// package (copied to "libvlc\win-x64" or "libvlc\win-arm64" next to the application).
    /// On Linux and macOS, libvlc comes from the system installation (VideoLAN does not ship
    /// NuGet packages for these platforms) and LibVLCSharp's default search is used.
    /// </summary>
    private static void InitializeNativeLibrary()
    {
        if (OperatingSystem.IsWindows())
        {
            var architectureFolder = RuntimeInformation.ProcessArchitecture is Architecture.Arm64
                ? "win-arm64"
                : "win-x64";
            var libVlcDirectory = Path.Combine(AppContext.BaseDirectory, "libvlc", architectureFolder);
            if (Directory.Exists(libVlcDirectory))
            {
                LibVLCSharp.Shared.Core.Initialize(libVlcDirectory);
                return;
            }
        }

        // Default search: libvlc.so.5 (Linux, system-installed) or the libvlc framework (macOS)
        LibVLCSharp.Shared.Core.Initialize();
    }
}
