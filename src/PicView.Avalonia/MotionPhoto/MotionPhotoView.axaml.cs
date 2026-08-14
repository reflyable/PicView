using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using PicView.Core.DebugTools;
using PicView.Core.ImageDecoding;
using PicView.Core.Models;
using PicView.Core.MotionPhoto;
using PicView.Core.ViewModels;

namespace PicView.Avalonia.MotionPhoto;

/// <summary>
/// Overlay that plays the embedded video of a motion photo on top of the still cover image.
/// Behavior: show the cover with a badge, play the video once when triggered, then freeze
/// back onto the cover (the badge remains so it can be replayed). Any failure degrades to
/// showing only the still image.
/// <para>
/// Video frames are produced by libvlc software video callbacks (BGRA32) and rendered by
/// <see cref="MotionPhotoVideoSurface"/>, which works on every display stack including
/// Wayland (libvlc 3.x has no public wl_surface embedding API) and lets the video follow
/// the normal Avalonia compositor.
/// </para>
/// </summary>
public partial class MotionPhotoView : UserControl, IDisposable
{
    private const int BytesPerPixel = 4;

    private static readonly byte[] Rv32Chroma = [(byte)'R', (byte)'V', (byte)'3', (byte)'2'];

    private readonly MediaPlayer.LibVLCVideoFormatCb _videoFormatCb;
    private readonly MediaPlayer.LibVLCVideoCleanupCb _videoCleanupCb;
    private readonly MediaPlayer.LibVLCVideoLockCb _videoLockCb;
    private readonly MediaPlayer.LibVLCVideoUnlockCb _videoUnlockCb;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _videoDisplayCb;

    private readonly Lock _frameLock = new();
    private IntPtr _frameBuffer;
    private byte[]? _managedFrame;
    private int _frameBufferSize;
    private int _videoWidth;
    private int _videoHeight;

    private Stream? _videoStream;
    private StreamMediaInput? _mediaInput;
    private Media? _media;
    private MediaPlayer? _mediaPlayer;
    private bool _isSessionBusy;
    private bool _isDisposed;

    /// <summary>Raised on the UI thread when video playback starts (zoom/pan should be locked).</summary>
    public event EventHandler? PlaybackStarted;

    /// <summary>Raised on the UI thread when video playback stops (zoom/pan can be unlocked).</summary>
    public event EventHandler? PlaybackStopped;

    /// <summary>Whether video is currently playing or paused.</summary>
    public bool IsPlaying { get; private set; }

    /// <summary>Whether the current image is a playable motion photo (badge or video is shown).</summary>
    public bool IsMotionPhotoActive => IsVisible;

    public MotionPhotoView()
    {
        InitializeComponent();
        PlayBadge.Click += OnPlayBadgeClicked;

        // Keep explicit references so the delegates are never garbage-collected while
        // libvlc may still invoke them (the MediaPlayer also stores them, but be explicit).
        _videoFormatCb = VideoFormatCallback;
        _videoCleanupCb = VideoCleanupCallback;
        _videoLockCb = LockVideoCallback;
        _videoUnlockCb = UnlockVideoCallback;
        _videoDisplayCb = DisplayVideoCallback;
    }

    /// <summary>
    /// Called whenever a new image is displayed. Stops any running playback and prepares
    /// (or hides) the motion photo overlay for the new model.
    /// </summary>
    public void OnImageChanged(TabViewModel tabViewModel)
    {
        Stop();

        var model = tabViewModel.Model;
        if (tabViewModel.SingleImageType is not SingleImageType.None)
        {
            IsVisible = false;
            return;
        }

        if (model.ImageType is ImageType.MotionPhoto &&
            model.MotionPhoto is not null &&
            MotionPhotoService.IsPlaybackSupported &&
            MotionPhotoService.TryGetLibVlc(out _))
        {
            IsVisible = true;
            PlayBadge.IsVisible = true;
            if (Settings.UIProperties.AutoPlayMotionPhotos)
            {
                _ = PlayAsync();
            }
        }
        else
        {
            IsVisible = false;
        }
    }

    /// <summary>
    /// Toggles between play and pause when playback is running, otherwise starts playback.
    /// Used by the Space keyboard shortcut.
    /// </summary>
    public void TogglePlayPause()
    {
        if (_mediaPlayer is not null && IsPlaying)
        {
            _mediaPlayer.Pause();
            return;
        }

        if (IsVisible && !IsPlaying)
        {
            _ = PlayAsync();
        }
    }

    /// <summary>
    /// Stops playback and returns to the cover image. Returns true when playback was active.
    /// Used by the Escape keyboard shortcut.
    /// </summary>
    public bool StopIfPlaying()
    {
        if (!IsPlaying)
        {
            return false;
        }

        Stop();
        PlayBadge.IsVisible = true;
        return true;
    }

    /// <summary>
    /// Starts motion photo playback: extracts the video on demand, hands it to libvlc
    /// as an in-memory stream and plays it once.
    /// </summary>
    public async Task PlayAsync()
    {
        if (_isSessionBusy || IsPlaying || _isDisposed)
        {
            return;
        }

        if (DataContext is not TabViewModel tabViewModel)
        {
            return;
        }

        var model = tabViewModel.Model;
        if (model.ImageType is not ImageType.MotionPhoto || model.MotionPhoto is null || model.FileInfo is null)
        {
            return;
        }

        if (!MotionPhotoService.TryGetLibVlc(out var libVlc))
        {
            IsVisible = false;
            return;
        }

        _isSessionBusy = true;
        Stream? stream = null;
        try
        {
            stream = await MotionPhotoExtractor.ExtractAsync(
                model.FileInfo, model.MotionPhoto, tabViewModel.GetTabCancellation().Token).ConfigureAwait(true);
        }
        catch (Exception e)
        {
            DebugHelper.LogDebug(nameof(MotionPhotoView), nameof(PlayAsync), e);
        }

        if (stream is null)
        {
            _isSessionBusy = false;
            // Extraction failed: degrade to the still image
            IsVisible = false;
            return;
        }

        try
        {
            _videoStream = stream;
            _mediaInput = new StreamMediaInput(stream);
            _media = new Media(libVlc, _mediaInput);
            _mediaPlayer = new MediaPlayer(_media)
            {
                Mute = Settings.UIProperties.MuteMotionPhotos,
            };
            _mediaPlayer.SetVideoFormatCallbacks(_videoFormatCb, _videoCleanupCb);
            _mediaPlayer.SetVideoCallbacks(_videoLockCb, _videoUnlockCb, _videoDisplayCb);
            _mediaPlayer.EndReached += OnEndReached;
            _mediaPlayer.EncounteredError += OnEncounteredError;

            VideoSurface.IsVisible = true;
            if (!_mediaPlayer.Play())
            {
                CleanupSession();
                VideoSurface.IsVisible = false;
                _isSessionBusy = false;
                return;
            }

            PlayBadge.IsVisible = false;
            IsPlaying = true;
            PlaybackStarted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception e)
        {
            DebugHelper.LogDebug(nameof(MotionPhotoView), nameof(PlayAsync), e);
            CleanupSession();
            VideoSurface.IsVisible = false;
            IsVisible = false;
        }
        finally
        {
            _isSessionBusy = false;
        }
    }

    /// <summary>
    /// Stops playback and releases all playback resources, returning to the cover image.
    /// </summary>
    public void Stop()
    {
        var wasPlaying = IsPlaying;
        CleanupSession();
        VideoSurface.IsVisible = false;
        IsPlaying = false;
        if (wasPlaying)
        {
            PlaybackStopped?.Invoke(this, EventArgs.Empty);
        }
    }

    private async void OnPlayBadgeClicked(object? sender, RoutedEventArgs e) => await PlayAsync();

    private void OnEndReached(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(FreezeBackToCover);

    private void OnEncounteredError(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(FreezeBackToCover);

    private void FreezeBackToCover()
    {
        if (!IsPlaying)
        {
            return;
        }

        Stop();
        // Keep the badge visible so the clip can be replayed
        PlayBadge.IsVisible = true;
    }

    #region libvlc software video callbacks

    private uint VideoFormatCallback(ref IntPtr opaque, IntPtr chroma,
        ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        Marshal.Copy(Rv32Chroma, 0, chroma, Rv32Chroma.Length);
        pitches = width * BytesPerPixel;
        lines = height;

        lock (_frameLock)
        {
            FreeFrameBufferLocked();
            _frameBufferSize = (int)(width * height * BytesPerPixel);
            _frameBuffer = Marshal.AllocHGlobal(_frameBufferSize);
            _managedFrame = new byte[_frameBufferSize];
            _videoWidth = (int)width;
            _videoHeight = (int)height;
        }

        Dispatcher.UIThread.Post(() => VideoSurface.EnsureBitmap(_videoWidth, _videoHeight));
        return 1;
    }

    private void VideoCleanupCallback(ref IntPtr opaque)
    {
        lock (_frameLock)
        {
            FreeFrameBufferLocked();
        }
    }

    private IntPtr LockVideoCallback(IntPtr opaque, IntPtr planes)
    {
        Marshal.WriteIntPtr(planes, _frameBuffer);
        return IntPtr.Zero;
    }

    private void UnlockVideoCallback(IntPtr opaque, IntPtr picture, IntPtr planes)
    {
        // Nothing to unlock; the buffer is reused until the cleanup callback.
    }

    private void DisplayVideoCallback(IntPtr opaque, IntPtr picture)
    {
        byte[] frame;
        int width, height;
        lock (_frameLock)
        {
            if (_frameBuffer == IntPtr.Zero || _managedFrame is null)
            {
                return;
            }

            Marshal.Copy(_frameBuffer, _managedFrame, 0, _frameBufferSize);
            frame = _managedFrame;
            width = _videoWidth;
            height = _videoHeight;
        }

        Dispatcher.UIThread.Post(() =>
        {
            lock (_frameLock)
            {
                VideoSurface.UpdateFrame(frame, width, height);
            }
        });
    }

    private void FreeFrameBufferLocked()
    {
        if (_frameBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_frameBuffer);
            _frameBuffer = IntPtr.Zero;
        }

        _managedFrame = null;
    }

    #endregion

    private void CleanupSession()
    {
        if (_mediaPlayer is not null)
        {
            _mediaPlayer.EndReached -= OnEndReached;
            _mediaPlayer.EncounteredError -= OnEncounteredError;
            try
            {
                _mediaPlayer.Stop();
            }
            catch (Exception e)
            {
                DebugHelper.LogDebug(nameof(MotionPhotoView), nameof(CleanupSession), e);
            }

            _mediaPlayer.Dispose();
            _mediaPlayer = null;
        }

        lock (_frameLock)
        {
            FreeFrameBufferLocked();
        }

        _media?.Dispose();
        _media = null;
        _mediaInput?.Dispose();
        _mediaInput = null;
        _videoStream?.Dispose();
        _videoStream = null;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        PlayBadge.Click -= OnPlayBadgeClicked;
        Stop();
        VideoSurface.Clear();
        GC.SuppressFinalize(this);
    }
}
