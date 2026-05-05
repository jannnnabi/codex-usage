using System.Diagnostics;
using System.Windows;

namespace codexpet;

public sealed class HudPositioner
{
    private const double ExpandedPetGap = 1;
    private const double CollapsedPetGap = 14;
    private const double BottomRowGap = 14;
    private const double PetVisualReserve = 92;
    private const double NotificationVisibleLeftInset = 58;
    private const double MinimumBottomInset = 1;
    private const double NotificationClearRatio = 0.62;
    private const double ScreenMargin = 18;
    private const double TrayFallbackBottomReserve = 88;

    public HudPlacement Compute(IntPtr hudHandle, double hudWidthDip, double hudHeightDip, bool collapsed)
    {
        var currentScale = NativeMethods.DpiScaleForWindow(hudHandle);
        var hudRect = GetHudDeviceRect(hudHandle, hudWidthDip, hudHeightDip, currentScale);
        var hudWidth = hudRect.Width;
        var hudHeight = hudRect.Height;

        var windows = EnumerateCodexWindows();
        var petArea = FindPetArea(windows);
        Rect workArea;
        Rect candidate;

        if (petArea is not null)
        {
            workArea = NativeMethods.WorkAreaFromDeviceRect(petArea.DeviceRect);
            candidate = PlaceInsidePetGroup(petArea.Bounds, hudWidth, hudHeight, collapsed, workArea, petArea.DpiScale);
            candidate = ClampToWorkArea(candidate, workArea, petArea.DpiScale);
            return new HudPlacement(new Point(candidate.Left, candidate.Top), true);
        }

        workArea = NativeMethods.WorkAreaFromDeviceRect(ToRect32(hudRect));
        candidate = PlaceFallback(hudWidth, hudHeight, workArea, currentScale);
        candidate = ClampToWorkArea(candidate, workArea, currentScale);
        return new HudPlacement(new Point(candidate.Left, candidate.Top), false);
    }

    private static Rect GetHudDeviceRect(IntPtr hudHandle, double hudWidthDip, double hudHeightDip, double dpiScale)
    {
        if (hudHandle != IntPtr.Zero
            && NativeMethods.GetWindowRect(hudHandle, out var rect)
            && rect.Width > 0
            && rect.Height > 0)
        {
            return NativeMethods.ToRect(rect);
        }

        return new Rect(0, 0, Math.Max(1, hudWidthDip * dpiScale), Math.Max(1, hudHeightDip * dpiScale));
    }

    private static Rect PlaceInsidePetGroup(Rect petGroup, double hudWidth, double hudHeight, bool collapsed, Rect workArea, double dpiScale)
    {
        var bottomInset = Scale(collapsed ? CollapsedPetGap : ExpandedPetGap, dpiScale);
        var preferredTop = petGroup.Bottom - hudHeight - bottomInset;
        var clearNotificationTop = petGroup.Top + petGroup.Height * NotificationClearRatio;
        var lowestTop = petGroup.Bottom - hudHeight - Scale(MinimumBottomInset, dpiScale);
        var top = Math.Min(Math.Max(preferredTop, clearNotificationTop), lowestTop);

        var visibleNotificationLeft = petGroup.Left + Scale(NotificationVisibleLeftInset, dpiScale);
        var desiredRight = petGroup.Right - Scale(PetVisualReserve + BottomRowGap, dpiScale);
        var left = collapsed
            ? petGroup.Right - Scale(PetVisualReserve + CollapsedPetGap, dpiScale) - hudWidth
            : desiredRight - hudWidth;

        if (!collapsed && left < visibleNotificationLeft)
        {
            left = visibleNotificationLeft;
        }

        var insideGroup = new Rect(left, top, hudWidth, hudHeight);
        if (FitsWithinWorkArea(insideGroup, workArea, dpiScale))
        {
            return insideGroup;
        }

        var leftCandidate = new Rect(
            petGroup.Left - hudWidth - Scale(ExpandedPetGap, dpiScale),
            top,
            hudWidth,
            hudHeight);
        if (FitsWithinWorkArea(leftCandidate, workArea, dpiScale))
        {
            return leftCandidate;
        }

        var rightCandidate = new Rect(
            petGroup.Right + Scale(ExpandedPetGap, dpiScale),
            top,
            hudWidth,
            hudHeight);
        if (FitsWithinWorkArea(rightCandidate, workArea, dpiScale))
        {
            return rightCandidate;
        }

        return insideGroup;
    }

    private static bool FitsWithinWorkArea(Rect rect, Rect workArea, double dpiScale)
    {
        var margin = Scale(ScreenMargin, dpiScale);
        return rect.Left >= workArea.Left + margin
            && rect.Top >= workArea.Top + margin
            && rect.Right <= workArea.Right - margin
            && rect.Bottom <= workArea.Bottom - margin;
    }

    private static Rect PlaceFallback(double hudWidth, double hudHeight, Rect workArea, double dpiScale)
    {
        return new Rect(
            workArea.Right - hudWidth - Scale(ScreenMargin, dpiScale),
            workArea.Bottom - hudHeight - Scale(TrayFallbackBottomReserve, dpiScale),
            hudWidth,
            hudHeight);
    }

    private static Rect ClampToWorkArea(Rect rect, Rect workArea, double dpiScale)
    {
        var margin = Scale(ScreenMargin, dpiScale);
        var left = Math.Min(Math.Max(rect.Left, workArea.Left + margin), workArea.Right - rect.Width - margin);
        var top = Math.Min(Math.Max(rect.Top, workArea.Top + margin), workArea.Bottom - rect.Height - margin);
        return new Rect(left, top, rect.Width, rect.Height);
    }

    private static PetArea? FindPetArea(IReadOnlyList<CodexWindow> windows)
    {
        var petLikeWindows = windows
            .Where(static window => IsPetOverlayCandidate(window))
            .OrderByDescending(static window => ScorePetCandidate(window))
            .ToArray();

        if (petLikeWindows.Length == 0)
        {
            return null;
        }

        var anchor = petLikeWindows[0];
        return new PetArea(NativeMethods.ToRect(anchor.DeviceRect), anchor.DeviceRect, anchor.DpiScale);
    }

    private static IReadOnlyList<CodexWindow> EnumerateCodexWindows()
    {
        var windows = new List<CodexWindow>();
        var currentProcessId = Environment.ProcessId;

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd))
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0 || pid == currentProcessId)
            {
                return true;
            }

            Process process;
            try
            {
                process = Process.GetProcessById((int)pid);
            }
            catch
            {
                return true;
            }

            if (!IsCodexProcess(process))
            {
                return true;
            }

            if (!NativeMethods.GetWindowRect(hwnd, out var rect) || rect.Width <= 0 || rect.Height <= 0)
            {
                return true;
            }

            var title = NativeMethods.GetWindowTitle(hwnd);
            var className = NativeMethods.GetWindowClass(hwnd);
            var dpiScale = NativeMethods.DpiScaleForWindow(hwnd);
            var widthDip = rect.Width / dpiScale;
            var heightDip = rect.Height / dpiScale;
            var isMainWindow = string.Equals(title, "Codex", StringComparison.OrdinalIgnoreCase)
                && widthDip > 700
                && heightDip > 500;
            if (isMainWindow)
            {
                return true;
            }

            if (widthDip > 1100 || heightDip > 650)
            {
                return true;
            }

            var exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GwlExStyle);
            windows.Add(new CodexWindow(hwnd, rect, title, className, exStyle, dpiScale));
            return true;
        }, IntPtr.Zero);

        return windows;
    }

    private static bool IsCodexProcess(Process process)
    {
        if (process.ProcessName.Equals("Codex", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!process.ProcessName.Equals("codex", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            return process.MainModule?.FileName.Contains("OpenAI.Codex", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPetOverlayCandidate(CodexWindow window)
    {
        if (!window.Title.Equals("Codex", StringComparison.OrdinalIgnoreCase)
            || !window.ClassName.Equals("Chrome_WidgetWin_1", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var exStyle = window.ExStyle;
        var looksLikeFloatingOverlay = (exStyle & NativeMethods.WsExTopmost) != 0
            && (exStyle & NativeMethods.WsExNoActivate) != 0;
        if (!looksLikeFloatingOverlay)
        {
            return false;
        }

        var widthDip = window.DeviceRect.Width / window.DpiScale;
        var heightDip = window.DeviceRect.Height / window.DpiScale;
        if (widthDip < 220 || heightDip < 190)
        {
            return false;
        }

        if (widthDip > 430 || heightDip > 380)
        {
            return false;
        }

        var ratio = widthDip / Math.Max(1, heightDip);
        return ratio is > 0.95 and < 1.25;
    }

    private static double ScorePetCandidate(CodexWindow window)
    {
        var rect = window.DeviceRect;
        var titleScore = window.Title.Equals("Codex", StringComparison.OrdinalIgnoreCase) ? 25_000 : 0;
        var classScore = window.ClassName.Equals("Chrome_WidgetWin_1", StringComparison.OrdinalIgnoreCase) ? 10_000 : 0;
        return rect.Width * rect.Height + titleScore + classScore;
    }

    private static double Scale(double value, double dpiScale)
    {
        return value * dpiScale;
    }

    private static Rect32 ToRect32(Rect rect)
    {
        return new Rect32
        {
            Left = (int)Math.Round(rect.Left),
            Top = (int)Math.Round(rect.Top),
            Right = (int)Math.Round(rect.Right),
            Bottom = (int)Math.Round(rect.Bottom)
        };
    }

    private sealed record CodexWindow(IntPtr Handle, Rect32 DeviceRect, string Title, string ClassName, int ExStyle, double DpiScale);
    private sealed record PetArea(Rect Bounds, Rect32 DeviceRect, double DpiScale);
}

public sealed record HudPlacement(Point DevicePosition, bool IsPetAnchored);
