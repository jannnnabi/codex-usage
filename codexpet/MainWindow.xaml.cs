using Microsoft.Win32;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace codexpet;

public partial class MainWindow : Window
{
    private static readonly TimeSpan RateLimitRefreshInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PositionRefreshInterval = TimeSpan.FromMilliseconds(120);
    private const double ExpandedHudWidth = 192;
    private const double ExpandedHudHeight = 92;
    private const double CollapsedHudSize = 76;

    private readonly CancellationTokenSource _shutdown = new();
    private readonly CodexAppServerClient _codexClient = new();
    private readonly HudPositioner _positioner = new();
    private readonly DispatcherTimer _positionTimer;
    private readonly DispatcherTimer _clockTimer;
    private readonly bool _isKorean;

    private RateLimitSnapshot? _snapshot;
    private bool _isCollapsed;
    private bool _isDark;
    private bool _isDraggingFallback;
    private bool _isPetAnchored;
    private bool _refreshRunning;
    private IntPtr _windowHandle;
    private Point? _manualFallbackPositionDevice;

    public MainWindow()
    {
        InitializeComponent();

        _isKorean = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("ko", StringComparison.OrdinalIgnoreCase);
        Root.ContextMenu = CreateContextMenu();
        Root.MouseRightButtonUp += Root_OnMouseRightButtonUp;
        _positionTimer = new DispatcherTimer { Interval = PositionRefreshInterval };
        _positionTimer.Tick += (_, _) => UpdatePosition();
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _clockTimer.Tick += (_, _) => UpdateLimitUi();

        Loaded += OnLoaded;
        Closed += OnClosed;
        SizeChanged += (_, _) => QueueProgressBarUpdate();
        PrimaryTrack.SizeChanged += (_, _) => QueueProgressBarUpdate();
        SecondaryTrack.SizeChanged += (_, _) => QueueProgressBarUpdate();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        NativeMethods.MakeToolWindowNoActivate(_windowHandle);
        ApplyTheme();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        UpdateLimitUi();
        UpdatePosition();
        _positionTimer.Start();
        _clockTimer.Start();

        _ = RefreshLoopAsync(_shutdown.Token);
        await RefreshRateLimitsAsync();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _shutdown.Cancel();
        _positionTimer.Stop();
        _clockTimer.Stop();
        _codexClient.Dispose();
        _shutdown.Dispose();
    }

    private void Root_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            SetCollapsed(!_isCollapsed);
            e.Handled = true;
            return;
        }

        if (!_isPetAnchored && e.LeftButton == MouseButtonState.Pressed)
        {
            DragFallbackWindow();
            e.Handled = true;
        }
    }

    private ContextMenu CreateContextMenu()
    {
        var menu = new ContextMenu();
        var exitItem = new MenuItem { Header = _isKorean ? "종료" : "Exit" };
        exitItem.Click += (_, _) => Close();
        menu.Items.Add(exitItem);
        return menu;
    }

    private void Root_OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (Root.ContextMenu is not { } menu)
        {
            return;
        }

        menu.PlacementTarget = Root;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void DragFallbackWindow()
    {
        _isDraggingFallback = true;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            _isDraggingFallback = false;
            if (_windowHandle != IntPtr.Zero
                && NativeMethods.GetWindowRect(_windowHandle, out var rect)
                && rect.Width > 0
                && rect.Height > 0)
            {
                _manualFallbackPositionDevice = new Point(rect.Left, rect.Top);
            }
        }
    }

    private async Task RefreshLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(RateLimitRefreshInterval, cancellationToken);
                await RefreshRateLimitsAsync();
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RefreshRateLimitsAsync()
    {
        if (_refreshRunning)
        {
            return;
        }

        _refreshRunning = true;
        try
        {
            var snapshot = await _codexClient.ReadRateLimitsAsync(_shutdown.Token);
            await Dispatcher.InvokeAsync(() =>
            {
                _snapshot = snapshot;
                UpdateLimitUi();
                UpdatePosition();
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            await Dispatcher.InvokeAsync(UpdateLimitUi);
        }
        finally
        {
            _refreshRunning = false;
        }
    }

    private void SetCollapsed(bool collapsed)
    {
        _isCollapsed = collapsed;
        ExpandedCard.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        CollapsedPill.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        Width = collapsed ? CollapsedHudSize : ExpandedHudWidth;
        Height = collapsed ? CollapsedHudSize : ExpandedHudHeight;
        UpdateLimitUi();
        UpdatePosition();
    }

    private void UpdateLimitUi()
    {
        var primary = LimitRowView.FromWindow(_snapshot?.Primary, true, _isKorean);
        var secondary = LimitRowView.FromWindow(_snapshot?.Secondary, false, _isKorean);

        ApplyRow(primary, PrimaryDurationText, PrimaryResetText, PrimaryPercentText, PrimaryTrackFill, PrimaryIconBack);
        ApplyRow(secondary, SecondaryDurationText, SecondaryResetText, SecondaryPercentText, SecondaryTrackFill, SecondaryIconBack);

        CollapsedGauge.Percent = primary.Percent ?? 0;
        CollapsedGauge.AccentBrush = primary.AccentBrush;
        CollapsedGauge.TrackBrush = GetBrush(_isDark ? "#474A52" : "#E9E9EA");
        CollapsedPercentText.Text = primary.PercentText;
        CollapsedPercentText.Foreground = primary.AccentBrush;

        QueueProgressBarUpdate();
    }

    private static void ApplyRow(
        LimitRowView row,
        TextBlock durationText,
        TextBlock resetText,
        TextBlock percentText,
        Border barFill,
        Border iconBack)
    {
        durationText.Text = row.DurationText;
        resetText.Text = row.ResetText;
        percentText.Text = row.PercentText;
        percentText.Foreground = row.AccentBrush;
        barFill.Background = row.AccentBrush;
        barFill.Tag = row.Percent ?? 0;
        iconBack.Background = row.IconBackgroundBrush;
    }

    private void UpdateProgressBars()
    {
        SetFillWidth(PrimaryTrack, PrimaryTrackFill);
        SetFillWidth(SecondaryTrack, SecondaryTrackFill);
    }

    private void QueueProgressBarUpdate()
    {
        Dispatcher.BeginInvoke(UpdateProgressBars, DispatcherPriority.Render);
    }

    private static void SetFillWidth(FrameworkElement track, FrameworkElement fill)
    {
        var percent = fill.Tag is int value ? Math.Clamp(value, 0, 100) : 0;
        fill.Width = Math.Max(0, track.ActualWidth * percent / 100.0);
    }

    private void UpdatePosition()
    {
        if (_isDraggingFallback || _windowHandle == IntPtr.Zero)
        {
            return;
        }

        var placement = _positioner.Compute(_windowHandle, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height, _isCollapsed);
        _isPetAnchored = placement.IsPetAnchored;
        if (placement.IsPetAnchored)
        {
            _manualFallbackPositionDevice = null;
        }
        else if (_manualFallbackPositionDevice is not null)
        {
            return;
        }

        NativeMethods.SetWindowPos(
            _windowHandle,
            IntPtr.Zero,
            (int)Math.Round(placement.DevicePosition.X),
            (int)Math.Round(placement.DevicePosition.Y),
            0,
            0,
            NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
        {
            ApplyTheme();
            UpdateLimitUi();
        }
    }

    private void ApplyTheme()
    {
        _isDark = ThemeHelper.IsDarkMode();
        var card = GetBrush(_isDark ? "#F01B1C20" : "#F6FFFFFF");
        var text = GetBrush(_isDark ? "#F4F4F5" : "#0D0D0D");
        var muted = GetBrush(_isDark ? "#A7AAB3" : "#858893");
        var dot = GetBrush(_isDark ? "#858894" : "#8A8D96");
        var track = GetBrush(_isDark ? "#474A52" : "#E9E9EA");

        ExpandedCard.Background = card;
        CollapsedPill.Background = card;
        PrimaryDurationText.Foreground = text;
        SecondaryDurationText.Foreground = text;
        PrimaryResetText.Foreground = muted;
        SecondaryResetText.Foreground = muted;
        PrimaryDotText.Foreground = dot;
        SecondaryDotText.Foreground = dot;
        PrimaryTrackBack.Background = track;
        SecondaryTrackBack.Background = track;
    }

    private static SolidColorBrush GetBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private sealed record LimitRowView(
        string DurationText,
        string ResetText,
        string PercentText,
        int? Percent,
        Brush AccentBrush,
        Brush IconBackgroundBrush)
    {
        public static LimitRowView FromWindow(RateLimitWindowSnapshot? window, bool primary, bool isKorean)
        {
            var duration = FormatDuration(window?.WindowDurationMins, primary, isKorean);
            var reset = FormatReset(window?.ResetsAt, primary, isKorean);

            if (window is null)
            {
                var neutral = GetBrush("#8A8D96");
                return new LimitRowView(duration, reset, "--%", null, neutral, GetBrush("#1A8A8D96"));
            }

            var remaining = Math.Clamp(100 - window.UsedPercent, 0, 100);
            var accent = AccentFor(remaining);
            var icon = IconBackgroundFor(remaining);
            return new LimitRowView(duration, reset, $"{remaining}%", remaining, accent, icon);
        }

        private static string FormatDuration(long? minutes, bool primary, bool isKorean)
        {
            if (minutes is null or <= 0)
            {
                return primary ? (isKorean ? "5시간" : "5h") : (isKorean ? "1주" : "1w");
            }

            if (minutes.Value >= 60 * 24 * 6)
            {
                var weeks = Math.Max(1, (int)Math.Round(minutes.Value / (60.0 * 24 * 7)));
                return isKorean ? $"{weeks}주" : $"{weeks}w";
            }

            if (minutes.Value >= 60)
            {
                var hours = Math.Max(1, (int)Math.Round(minutes.Value / 60.0));
                return isKorean ? $"{hours}시간" : $"{hours}h";
            }

            return isKorean ? $"{minutes.Value}분" : $"{minutes.Value}m";
        }

        private static string FormatReset(DateTimeOffset? resetsAt, bool primary, bool isKorean)
        {
            if (resetsAt is null)
            {
                return primary ? "--:--" : "--";
            }

            var local = resetsAt.Value.ToLocalTime();
            if (primary)
            {
                return isKorean ? local.ToString("tt h:mm", CultureInfo.CurrentUICulture) : local.ToString("h:mm tt", CultureInfo.CurrentUICulture);
            }

            return isKorean ? local.ToString("M월 d일", CultureInfo.CurrentUICulture) : local.ToString("MMM d", CultureInfo.CurrentUICulture);
        }

        private static Brush AccentFor(int percent)
        {
            if (percent >= 70)
            {
                return GetBrush("#18B72A");
            }

            if (percent >= 30)
            {
                return GetBrush("#F6B200");
            }

            return GetBrush("#EF3B2D");
        }

        private static Brush IconBackgroundFor(int percent)
        {
            if (percent >= 70)
            {
                return GetBrush("#DDF7E0");
            }

            if (percent >= 30)
            {
                return GetBrush("#FFF2D8");
            }

            return GetBrush("#FFE3E0");
        }
    }
}
