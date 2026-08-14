using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PicView.Avalonia.CustomControls;
using PicView.Avalonia.ImageTransformations;
using PicView.Avalonia.Input;
using PicView.Avalonia.UI;
using PicView.Core.Config;
using PicView.Core.DebugTools;
using PicView.Core.Extensions;
using PicView.Core.Localization;
using PicView.Core.ViewModels;
using R3;

namespace PicView.Avalonia.Views.UC;

public partial class ImageViewer : UserControl, IDisposable
{
    private RotationTransformer? _imageTransformer;
    private DisposableBag _disposables;
    
    public ImageViewer()
    {
        InitializeComponent();
        ImageControlHelper.TriggerScalingModeUpdate(MainImage, true);
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        InitializeImageTransformer();
        
        AddHandler(PointerWheelChangedEvent, PreviewOnPointerWheelChanged, RoutingStrategies.Tunnel);
        AddHandler(PointerTouchPadGestureMagnifyEvent, TouchMagnifyEvent, RoutingStrategies.Bubble);
        AddHandler(PinchEvent, TouchMagnifyEvent, RoutingStrategies.Bubble);
        _disposables.Add(new HoverFadeButtonHandler(GalleryShortcut, GalleryShortcut.InnerButton));

        // The video overlay uses a native window that ignores render transforms,
        // so zoom/pan is locked for the duration of motion photo playback
        MotionPhotoView.PlaybackStarted += OnMotionPhotoPlaybackStarted;
        MotionPhotoView.PlaybackStopped += OnMotionPhotoPlaybackStopped;
    }

    private void OnMotionPhotoPlaybackStarted(object? sender, EventArgs e) => ZoomPanControl.IsEnabled = false;

    private void OnMotionPhotoPlaybackStopped(object? sender, EventArgs e) => ZoomPanControl.IsEnabled = true;

    /// <summary>
    /// Notifies the motion photo overlay that a new image is displayed,
    /// stopping any running playback and preparing the badge when applicable.
    /// May be called from any thread; UI work is marshalled to the UI thread.
    /// </summary>
    public void UpdateMotionPhoto(TabViewModel tabViewModel)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            MotionPhotoView.OnImageChanged(tabViewModel);
        }
        else
        {
            Dispatcher.UIThread.Post(() => MotionPhotoView.OnImageChanged(tabViewModel));
        }
    }

    /// <summary>Whether the current image is a playable motion photo.</summary>
    public bool IsMotionPhotoActive => MotionPhotoView.IsMotionPhotoActive;

    /// <summary>Stops motion photo playback. Returns true when playback was active.</summary>
    public bool StopMotionPhotoIfPlaying() => MotionPhotoView.StopIfPlaying();

    /// <summary>Starts, pauses or resumes motion photo playback.</summary>
    public void ToggleMotionPhotoPlayPause() => MotionPhotoView.TogglePlayPause();

    public void TriggerScalingModeUpdate(bool invalidate) =>
        ImageControlHelper.TriggerScalingModeUpdate(MainImage, invalidate);

    private void TouchMagnifyEvent(object? sender, PointerDeltaEventArgs e) =>
        ZoomPanControl.ZoomWithPointerWheelCore(e.Delta.Y > 0, e.GetPosition(this));

    public async ValueTask PreviewOnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (GalleryView.IsPointerOver)
        {
            return;
        }

        if (TopLevel.GetTopLevel(this) is not MainWindow mainWindow)
        {
            return;
        }
        
        await MouseShortcuts.HandlePointerWheelChanged(
            e,
            mainWindow.DataContext as MainWindowViewModel,          
            mainWindow,
            ImageScrollViewer,
            async args => await Dispatcher.UIThread.InvokeAsync(() => ZoomIn(args)),
            async args => await Dispatcher.UIThread.InvokeAsync(() => ZoomOut(args)));
    }
        

    private void InitializeImageTransformer()
    {
        if (_imageTransformer is not null)
        {
            return;
        }

        if (Application.Current.DataContext is not CoreViewModel core)
        {
            return;
        }

        // The image is not flipped by default, update translation to reflect that
        core.Translation.IsFlipped.Value = TranslationManager.Translation.Flip;

        _imageTransformer = new RotationTransformer(
            MainTransform,
            MainImage,
            core.MainWindows.ActiveWindow.CurrentValue,
            TopLevel.GetTopLevel(this) as MainWindow);
        ZoomPanControl.Initialize(ZoomPreview);

        Observable.EveryValueChanged(ZoomPanControl, zoom => zoom.Scale)
            .Skip(1)
            .Subscribe(zoomLevel =>
            {
                if (DataContext is not TabViewModel tab)
                {
                    return;
                }
                var adjustedZoomLevel = Convert.ToInt32(tab.InitialZoom.CurrentValue * (zoomLevel * 100));
                tab.ZoomLevel.Value = adjustedZoomLevel;;
                tab.UpdateTabTitle();
                if (Settings.Zoom.IsShowingZoomPercentagePopup)
                {
                    var message = StringExtensions.CombineWithPercentage(adjustedZoomLevel);
                    _ = TooltipHelper.ShowTooltipMessageContinuallyAsync(message, true,
                        TopLevel.GetTopLevel(this) as MainWindow, TimeSpan.FromSeconds(1));
                }

                ZoomPreview.Margin = HoverBar.Opacity > 0 ? new Thickness(0,0,25,HoverBar.Bounds.Height / 2 + 25) : new Thickness(0, 0, 25, 25);
            }, DebugHelper.LogError(nameof(ImageViewer), nameof(InitializeImageTransformer))).AddTo(ref _disposables);
        
        core.MainWindows.ActiveWindow.CurrentValue.IsScrollingEnabled.Subscribe(isScrolling =>
        {
            ImageScrollViewer.VerticalScrollBarVisibility = isScrolling ?
                ScrollBarVisibility.Visible : ScrollBarVisibility.Disabled;
        }, DebugHelper.LogError(nameof(ImageViewer), nameof(InitializeImageTransformer))).AddTo(ref _disposables);
        
        // Correspond to change when index clicked on track
        Observable.FromEvent<EventHandler<int>, int>(
                handler => (sender, index) => handler(index),
                handler => HoverBar.ProgressBar.ClickedOnTrack += handler,
                handler => HoverBar.ProgressBar.ClickedOnTrack -= handler)
            .SubscribeAwait(async (x, _) =>
            {
                if (DataContext is not TabViewModel tab)
                {
                    return;
                }
                await tab.ImageIterator.SkipToIndexAsync(x, tab.GetTabCancellation()).ConfigureAwait(false);
            }, DebugHelper.LogError(nameof(ImageViewer), nameof(InitializeImageTransformer)), AwaitOperation.Drop)
            .AddTo(ref _disposables);
        // Correspond to change when index dragged on track
        // wait for a 25ms pause in changes (debounce), and then emit the last value.
        Observable.FromEvent<EventHandler<int>, int>(
                handler => (sender, index) => handler(index),
                handler => HoverBar.ProgressBar.DraggedOnTrack += handler,
                handler => HoverBar.ProgressBar.DraggedOnTrack -= handler)
            .Debounce(TimeSpan.FromMilliseconds(25)) // Debounce handles rapid events during drag
            .SubscribeAwait(async (x, _) =>
            {
                if (DataContext is not TabViewModel tab)
                {
                    return;
                }
                await tab.ImageIterator.SkipToIndexAsync(x, tab.GetTabCancellation()).ConfigureAwait(false);
            },DebugHelper.LogError(nameof(ImageViewer), nameof(InitializeImageTransformer)), AwaitOperation.Drop)
            .AddTo(ref _disposables);
    }

    #region Zoom
    /// <inheritdoc cref="Zoom.ZoomIn(ViewModels.MainViewModel)"/>
    private void ZoomIn(PointerWheelEventArgs e) =>
        ZoomPanControl.ZoomWithPointerWheel(e);

    /// <inheritdoc cref="Zoom.ZoomOut(ViewModels.MainViewModel)"/>
    private void ZoomOut(PointerWheelEventArgs e) =>
        ZoomPanControl.ZoomWithPointerWheel(e);

    /// <inheritdoc cref="Zoom.ZoomIn(ViewModels.MainViewModel)"/>
    public void ZoomIn() =>
        ZoomPanControl.ZoomIn();

    /// <inheritdoc cref="Zoom.ZoomOut(ViewModels.MainViewModel)"/>
    public void ZoomOut() =>
        ZoomPanControl.ZoomOut();

    /// <inheritdoc cref="Zoom.ResetZoom(bool, ViewModels.MainViewModel)"/>
    public void ResetZoom(bool enableAnimations = true) =>
        ZoomPanControl.ResetZoom(enableAnimations);
    
    public void ResetZoomSlim() =>
        ZoomPanControl.ResetZoomSlim();
    
    #endregion

    #region Image Transformation
    public void Rotate(bool clockWise) => _imageTransformer?.Rotate(clockWise);
    public void Rotate(int angle) => _imageTransformer?.Rotate(angle);
    public void Flip(bool animate) => _imageTransformer?.Flip(animate);
        
    #endregion

    public void Dispose()
    {
        RemoveHandler(PointerWheelChangedEvent, PreviewOnPointerWheelChanged);
        RemoveHandler(PointerTouchPadGestureMagnifyEvent, TouchMagnifyEvent);
        RemoveHandler(PinchEvent, TouchMagnifyEvent);
        MotionPhotoView.PlaybackStarted -= OnMotionPhotoPlaybackStarted;
        MotionPhotoView.PlaybackStopped -= OnMotionPhotoPlaybackStopped;
        MotionPhotoView.Dispose();
        _disposables.Dispose();
        HoverBar.Dispose();
    }
}
