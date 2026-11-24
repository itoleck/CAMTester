using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CAMTester.Models;
using CAMTester.Services;
using Microsoft.Win32;

namespace CAMTester;

public partial class MainWindow : Window
{
    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private readonly CameraDiscoveryService _discoveryService = new();
    private readonly CameraPreviewService _previewService = new();

    private readonly List<CameraDeviceInfo> _allCameras = [];
    private readonly ObservableCollection<CameraDeviceInfo> _visibleCameras = [];

    private CancellationTokenSource? _switchCts;
    private CancellationTokenSource? _refreshDebounceCts;
    private CancellationTokenSource? _toastCts;

    private CameraDeviceInfo? _selectedCamera;
    private WriteableBitmap? _previewBitmap;
    private int _renderScheduled;
    private bool _suppressSelectionEvents;
    private bool _suppressFormatEvents;
    private bool _isClosing;
    private bool _cleanupCompleted;

    public MainWindow()
    {
        InitializeComponent();

        CameraListBox.ItemsSource = _visibleCameras;

        _previewService.FrameReady += OnFrameReady;
        _previewService.StreamError += OnStreamError;
        _discoveryService.DevicesChanged += OnDevicesChanged;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnWindowClosing;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Enable Windows 10/11 dark title bar to match the dark UI theme.
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            var useDark = 1;
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int));
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _discoveryService.StartWatching();
        await RefreshCamerasAsync(autoPreserveSelection: false);
    }

    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_cleanupCompleted)
        {
            return;
        }

        _isClosing = true;
        _switchCts?.Cancel();
        _refreshDebounceCts?.Cancel();
        _toastCts?.Cancel();

        _discoveryService.DevicesChanged -= OnDevicesChanged;
        _previewService.FrameReady -= OnFrameReady;
        _previewService.StreamError -= OnStreamError;
        _discoveryService.Dispose();

        // If no camera stream is active, release synchronously and let the window close immediately.
        if (!_previewService.IsRunning)
        {
            _cleanupCompleted = true;
            _ = _previewService.DisposeAsync();
            return;
        }

        // Otherwise cancel this close briefly so the camera hardware is released cleanly before exit.
        e.Cancel = true;
        try
        {
            await _previewService.DisposeAsync();
        }
        catch
        {
            // Ensure window always closes even if driver disposal throws.
        }

        _cleanupCompleted = true;
        _ = Dispatcher.BeginInvoke(Close);
    }

    private async Task RefreshCamerasAsync(bool autoPreserveSelection)
    {
        RefreshButton.IsEnabled = false;
        try
        {
            var previousId = autoPreserveSelection ? _selectedCamera?.Id : null;
            var discovered = await _discoveryService.GetCamerasAsync();

            _allCameras.Clear();
            _allCameras.AddRange(discovered);

            ApplyFilter(previousId);

            // If the currently streaming camera was unplugged, stop preview gracefully.
            if (_selectedCamera is not null &&
                !_allCameras.Any(c => string.Equals(c.Id, _selectedCamera.Id, StringComparison.OrdinalIgnoreCase)))
            {
                await StopPreviewAsync(clearSelection: true);
                ShowToast("Active camera was disconnected.");
            }
        }
        catch (Exception ex)
        {
            CameraCountText.Text = "Failed to enumerate cameras";
            ShowToast($"Camera scan error: {ex.Message}");
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void ApplyFilter(string? preserveCameraId = null)
    {
        var query = SearchBox.Text.Trim();
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

        var filtered = string.IsNullOrEmpty(query)
            ? _allCameras
            : _allCameras.Where(c =>
                c.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                c.Subtitle.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                c.DeviceId.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        var targetId = preserveCameraId ?? _selectedCamera?.Id;

        _suppressSelectionEvents = true;
        try
        {
            _visibleCameras.Clear();
            foreach (var cam in filtered)
            {
                _visibleCameras.Add(cam);
            }

            if (!string.IsNullOrEmpty(targetId))
            {
                var match = _visibleCameras.FirstOrDefault(c =>
                    string.Equals(c.Id, targetId, StringComparison.OrdinalIgnoreCase));
                CameraListBox.SelectedItem = match;
            }
        }
        finally
        {
            _suppressSelectionEvents = false;
        }

        UpdateSidebarEmptyState();
    }

    private void UpdateSidebarEmptyState()
    {
        var total = _allCameras.Count;
        CameraCountText.Text = total switch
        {
            0 => "No camera inputs detected",
            1 => "1 camera input available",
            _ => $"{total} camera inputs available"
        };

        if (_visibleCameras.Count == 0)
        {
            NoCamerasPanel.Visibility = Visibility.Visible;
            if (_allCameras.Count == 0)
            {
                NoCamerasTitle.Text = "No cameras found";
                NoCamerasSubtitle.Text = "Connect a USB webcam or virtual camera, then click Refresh.";
            }
            else
            {
                NoCamerasTitle.Text = "No matching cameras";
                NoCamerasSubtitle.Text = "Try clearing the filter box to see all detected camera inputs.";
            }
        }
        else
        {
            NoCamerasPanel.Visibility = Visibility.Collapsed;
        }
    }

    private async void CameraListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionEvents)
        {
            return;
        }

        if (CameraListBox.SelectedItem is CameraDeviceInfo camera)
        {
            await StartPreviewForCameraAsync(camera);
        }
    }

    private async void CameraListBox_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Only treat clicks directly on a camera card (not blank space in the ListBox).
        var hit = e.OriginalSource as DependencyObject;
        var clickedItem = FindAncestor<ListBoxItem>(hit);
        if (clickedItem?.DataContext is not CameraDeviceInfo camera)
        {
            return;
        }

        // Allow clicking an already-selected camera card to restart/retry it if it was stopped or errored.
        if (_selectedCamera is not null &&
            string.Equals(camera.Id, _selectedCamera.Id, StringComparison.OrdinalIgnoreCase) &&
            !_previewService.IsRunning &&
            LoadingOverlay.Visibility != Visibility.Visible)
        {
            await StartPreviewForCameraAsync(camera);
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private async Task StartPreviewForCameraAsync(CameraDeviceInfo camera, CameraFormatInfo? requestedFormat = null)
    {
        _switchCts?.Cancel();
        _switchCts?.Dispose();
        _switchCts = new CancellationTokenSource();
        var ct = _switchCts.Token;

        _selectedCamera = camera;
        SelectedCameraTitle.Text = camera.DisplayName;
        CopyIdButton.IsEnabled = true;

        SetUiStateLoading($"Opening {camera.DisplayName}...");

        try
        {
            var session = await _previewService.StartAsync(camera, requestedFormat, ct);
            if (ct.IsCancellationRequested || _isClosing)
            {
                return;
            }

            PopulateFormats(session.SupportedFormats, session.ActiveFormat, session.IsSharedReadOnly);
            SetUiStateLive(session);
        }
        catch (OperationCanceledException)
        {
            // User quickly switched to another camera; ignore cancelled startup.
        }
        catch (UnauthorizedAccessException)
        {
            if (!ct.IsCancellationRequested && !_isClosing)
            {
                SetUiStateError(
                    "Camera access was denied by Windows Privacy settings. Enable camera access for desktop apps in Settings > Privacy & security > Camera.",
                    showPrivacyLink: true);
            }
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested && !_isClosing)
            {
                SetUiStateError(ex.Message, showPrivacyLink: false);
            }
        }
    }

    private void PopulateFormats(
        IReadOnlyList<CameraFormatInfo> formats,
        CameraFormatInfo? activeFormat,
        bool isSharedReadOnly)
    {
        _suppressFormatEvents = true;
        try
        {
            FormatComboBox.ItemsSource = formats;
            if (activeFormat is not null)
            {
                var matching = formats.FirstOrDefault(f =>
                    f.Width == activeFormat.Width &&
                    f.Height == activeFormat.Height &&
                    Math.Abs(f.FrameRate - activeFormat.FrameRate) < 0.2 &&
                    string.Equals(f.Subtype, activeFormat.Subtype, StringComparison.OrdinalIgnoreCase));

                FormatComboBox.SelectedItem = matching ?? activeFormat;
            }
            else if (formats.Count > 0)
            {
                FormatComboBox.SelectedIndex = 0;
            }

            FormatComboBox.IsEnabled = formats.Count > 1 && !isSharedReadOnly;
        }
        finally
        {
            _suppressFormatEvents = false;
        }
    }

    private void OnFrameReady(object? sender, FrameUpdate update)
    {
        if (_isClosing)
        {
            return;
        }

        // Coalesce UI renders so the dispatcher queue never backs up if frames arrive faster than paint.
        if (Interlocked.Exchange(ref _renderScheduled, 1) == 1)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            try
            {
                if (_isClosing || !_previewService.IsRunning)
                {
                    return;
                }

                var updated = _previewService.WithLatestFrame((pixels, width, height, stride) =>
                {
                    if (_previewBitmap is null ||
                        _previewBitmap.PixelWidth != width ||
                        _previewBitmap.PixelHeight != height)
                    {
                        _previewBitmap = new WriteableBitmap(
                            width,
                            height,
                            96,
                            96,
                            PixelFormats.Bgr32,
                            null);
                        PreviewImage.Source = _previewBitmap;
                    }

                    _previewBitmap.WritePixels(
                        new Int32Rect(0, 0, width, height),
                        pixels,
                        stride,
                        0);
                });

                if (updated)
                {
                    if (PreviewImage.Visibility != Visibility.Visible)
                    {
                        PreviewImage.Visibility = Visibility.Visible;
                        LoadingOverlay.Visibility = Visibility.Collapsed;
                        IdleOverlay.Visibility = Visibility.Collapsed;
                        ErrorOverlay.Visibility = Visibility.Collapsed;
                    }

                    TelemetryResolution.Text = $"{update.Width} × {update.Height}";
                    TelemetryFps.Text = update.Fps > 0.1 ? $"{update.Fps:0.0} fps" : "…";
                    TelemetryFormat.Text = CameraFormatInfo.CleanSubtype(update.Subtype);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _renderScheduled, 0);
            }
        });
    }

    private void OnStreamError(object? sender, string message)
    {
        if (_isClosing)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (!_isClosing)
            {
                SetUiStateError(message, showPrivacyLink: false);
            }
        });
    }

    private void OnDevicesChanged(object? sender, EventArgs e)
    {
        if (_isClosing)
        {
            return;
        }

        // Debounce bursts of PnP device events when plugging/unplugging multi-endpoint USB cameras.
        _refreshDebounceCts?.Cancel();
        _refreshDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _refreshDebounceCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(450, cts.Token);
                await Dispatcher.InvokeAsync(async () =>
                {
                    if (!_isClosing)
                    {
                        await RefreshCamerasAsync(autoPreserveSelection: true);
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private async Task StopPreviewAsync(bool clearSelection)
    {
        _switchCts?.Cancel();
        await _previewService.StopAsync();

        _previewBitmap = null;
        PreviewImage.Source = null;
        PreviewImage.Visibility = Visibility.Collapsed;
        LoadingOverlay.Visibility = Visibility.Collapsed;
        ErrorOverlay.Visibility = Visibility.Collapsed;
        IdleOverlay.Visibility = Visibility.Visible;

        SharedModeBadge.Visibility = Visibility.Collapsed;
        StopButton.IsEnabled = false;
        SnapshotButton.IsEnabled = false;
        FormatComboBox.IsEnabled = false;
        FormatComboBox.ItemsSource = null;

        TelemetryResolution.Text = "—";
        TelemetryFps.Text = "—";
        TelemetryFormat.Text = "—";
        TelemetryMode.Text = "—";

        SetStatusBadge("IDLE", "#6B7280", "#9CA3AF");

        if (clearSelection)
        {
            _suppressSelectionEvents = true;
            CameraListBox.SelectedItem = null;
            _suppressSelectionEvents = false;
            _selectedCamera = null;
            SelectedCameraTitle.Text = "No camera selected";
            CopyIdButton.IsEnabled = false;
        }
    }

    private void SetUiStateLoading(string message)
    {
        PreviewImage.Visibility = Visibility.Collapsed;
        IdleOverlay.Visibility = Visibility.Collapsed;
        ErrorOverlay.Visibility = Visibility.Collapsed;
        LoadingOverlay.Visibility = Visibility.Visible;
        LoadingText.Text = message;

        SharedModeBadge.Visibility = Visibility.Collapsed;
        StopButton.IsEnabled = false;
        SnapshotButton.IsEnabled = false;
        FormatComboBox.IsEnabled = false;

        SetStatusBadge("CONNECTING", "#F59E0B", "#FCD34D");
    }

    private void SetUiStateLive(CameraSessionInfo session)
    {
        StopButton.IsEnabled = true;
        SnapshotButton.IsEnabled = true;
        SharedModeBadge.Visibility = session.IsSharedReadOnly ? Visibility.Visible : Visibility.Collapsed;

        TelemetryMode.Text = session.Camera.Backend == CameraBackend.DirectShow
            ? "DirectShow"
            : session.IsSharedReadOnly
                ? "Shared (Read-Only)"
                : "Exclusive";
        if (session.ActiveFormat is not null)
        {
            TelemetryResolution.Text = session.ActiveFormat.CompactLabel;
            TelemetryFormat.Text = CameraFormatInfo.CleanSubtype(session.ActiveFormat.Subtype);
        }

        SetStatusBadge("LIVE", "#22C55E", "#86EFAC");
    }

    private void SetUiStateError(string message, bool showPrivacyLink)
    {
        PreviewImage.Visibility = Visibility.Collapsed;
        IdleOverlay.Visibility = Visibility.Collapsed;
        LoadingOverlay.Visibility = Visibility.Collapsed;
        ErrorOverlay.Visibility = Visibility.Visible;

        ErrorMessageText.Text = message;
        PrivacySettingsButton.Visibility = showPrivacyLink ? Visibility.Visible : Visibility.Collapsed;

        SharedModeBadge.Visibility = Visibility.Collapsed;
        StopButton.IsEnabled = false;
        SnapshotButton.IsEnabled = false;
        FormatComboBox.IsEnabled = false;

        TelemetryResolution.Text = "—";
        TelemetryFps.Text = "—";
        TelemetryFormat.Text = "—";
        TelemetryMode.Text = "—";

        SetStatusBadge("ERROR", "#EF4444", "#FCA5A5");
    }

    private void SetStatusBadge(string text, string dotHex, string textHex)
    {
        StatusBadgeText.Text = text;
        StatusDot.Fill = (Brush)new BrushConverter().ConvertFromString(dotHex)!;
        StatusBadgeText.Foreground = (Brush)new BrushConverter().ConvertFromString(textHex)!;
    }

    private async void FormatComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFormatEvents || _selectedCamera is null)
        {
            return;
        }

        if (FormatComboBox.SelectedItem is CameraFormatInfo chosenFormat)
        {
            await StartPreviewForCameraAsync(_selectedCamera, chosenFormat);
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshCamerasAsync(autoPreserveSelection: true);
        ShowToast($"Found {_allCameras.Count} camera input{(_allCameras.Count == 1 ? "" : "s")}.");
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void MirrorToggle_Click(object sender, RoutedEventArgs e)
    {
        PreviewMirrorTransform.ScaleX = MirrorToggle.IsChecked == true ? -1 : 1;
    }

    private void SnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        if (_previewBitmap is null || _selectedCamera is null)
        {
            return;
        }

        // Clone current frame immediately so the saved image matches the moment clicked.
        var snapshot = _previewBitmap.Clone();
        snapshot.Freeze();

        var safeCameraName = string.Concat(
            _selectedCamera.DisplayName.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));

        var dialog = new SaveFileDialog
        {
            Title = "Save Camera Snapshot",
            Filter = "PNG Image (*.png)|*.png",
            DefaultExt = ".png",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            FileName = $"{safeCameraName}_{DateTime.Now:yyyyMMdd_HHmmss}.png"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(snapshot));
            using var stream = File.Create(dialog.FileName);
            encoder.Save(stream);
            ShowToast($"Snapshot saved: {Path.GetFileName(dialog.FileName)}");
        }
        catch (Exception ex)
        {
            ShowToast($"Failed to save snapshot: {ex.Message}");
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        await StopPreviewAsync(clearSelection: false);
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCamera is not null)
        {
            await StartPreviewForCameraAsync(_selectedCamera);
        }
    }

    private void PrivacySettingsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:privacy-webcam") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowToast($"Unable to open Settings: {ex.Message}");
        }
    }

    private void CopyIdButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCamera is null)
        {
            return;
        }

        try
        {
            Clipboard.SetText(_selectedCamera.DeviceId);
            ShowToast("Device ID copied to clipboard.");
        }
        catch
        {
            ShowToast("Unable to access clipboard.");
        }
    }

    private void ShowToast(string message)
    {
        _toastCts?.Cancel();
        _toastCts?.Dispose();
        var cts = new CancellationTokenSource();
        _toastCts = cts;

        ToastText.Text = message;
        ToastBanner.Visibility = Visibility.Visible;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(2600, cts.Token);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!cts.IsCancellationRequested)
                    {
                        ToastBanner.Visibility = Visibility.Collapsed;
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }
        });
    }
}
