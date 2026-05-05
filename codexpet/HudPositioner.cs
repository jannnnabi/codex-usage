using System.Diagnostics;
using System.Windows;

namespace codexpet;

public sealed class HudPositioner
{
    private const double ExpandedPetGap = 10;
    private const double CollapsedPetGap = 14;
    private const double BottomRowGap = 14;
    private const double PetVisualReserve = 92;
    private const double NotificationVisibleLeftInset = 58;
    private const double MinimumBottomInset = 8;
    private const double NotificationClearRatio = 0.62;
    private const double ScreenMargin = 18;
    private const double TrayFallbackBottomReserve = 88;

    public Point Compute(double hudWidth, double hudHeight, bool collapsed, double dpiScaleX, double dpiScaleY)
    {
        var windows = EnumerateCodexWindows(dpiScaleX, dpiScaleY);
        var petArea = FindPetArea(windows);
        Rect workArea;
        Rect candidate;

        if (petArea is not null)
        {
            workArea = NativeMethods.WorkAreaFromRect(petArea.DeviceRect, dpiScaleX, dpiScaleY);
            candidate = PlaceInsidePetGroup(petArea.Bounds, hudWidth, hudHeight, collapsed, workArea);
        }
        else
        {
            workArea = SystemParameters.WorkArea;
            candidate = PlaceFallback(hudWidth, hudHeight, workArea);
        }

        candidate = ClampToWorkArea(candidate, workArea);
        return new Point(candidate.Left, candidate.Top);
    }

    private static Rect PlaceInsidePetGroup(Rect petGroup, double hudWidth, double hudHeight, bool collapsed, Rect workArea)
    {
        var bottomInset = collapsed ? CollapsedPetGap : ExpandedPetGap;
        var preferredTop = petGroup.Bottom - hudHeight - bottomInset;
        var clearNotificationTop = petGroup.Top + petGroup.Height * NotificationClearRatio;
        var lowestTop = petGroup.Bottom - hudHeight - MinimumBottomInset;
        var top = Math.Min(Math.Max(preferredTop, clearNotificationTop), lowestTop);

        var visibleNotificationLeft = petGroup.Left + NotificationVisibleLeftInset;
        var desiredRight = petGroup.Right - PetVisualReserve - BottomRowGap;
        var left = collapsed
            ? petGroup.Right - PetVisualReserve - CollapsedPetGap - hudWidth
            : desiredRight - hudWidth;

        if (!collapsed && left < petGroup.Left)
        {
            left = petGroup.Left;
        }

        if (!collapsed && left < visibleNotificationLeft)
        {
            left = visibleNotificationLeft;
        }

        var insideGroup = new Rect(left, top, hudWidth, hudHeight);
        if (FitsWithinWorkArea(insideGroup, workArea))
        {
            return insideGroup;
        }

        var leftCandidate = new Rect(
            petGroup.Left - hudWidth - ExpandedPetGap,
            top,
            hudWidth,
            hudHeight);
        if (FitsWithinWorkArea(leftCandidate, workArea))
        {
            return leftCandidate;
        }

        var rightCandidate = new Rect(
            petGroup.Right + ExpandedPetGap,
            top,
            hudWidth,
            hudHeight);
        if (FitsWithinWorkArea(rightCandidate, workArea))
        {
            return rightCandidate;
        }

        return insideGroup;
    }

    private static bool FitsWithinWorkArea(Rect rect, Rect workArea)
    {
        return rect.Left >= workArea.Left + ScreenMargin
            && rect.Top >= workArea.Top + ScreenMargin
            && rect.Right <= workArea.Right - ScreenMargin
            && rect.Bottom <= workArea.Bottom - ScreenMargin;
    }

    private static Rect PlaceFallback(double hudWidth, double hudHeight, Rect workArea)
    {
        return new Rect(
            workArea.Right - hudWidth - ScreenMargin,
            workArea.Bottom - hudHeight - TrayFallbackBottomReserve,
            hudWidth,
            hudHeight);
    }

    private static Rect ClampToWorkArea(Rect rect, Rect workArea)
    {
        var left = Math.Min(Math.Max(rect.Left, workArea.Left + ScreenMargin), workArea.Right - rect.Width - ScreenMargin);
        var top = Math.Min(Math.Max(rect.Top, workArea.Top + ScreenMargin), workArea.Bottom - rect.Height - ScreenMargin);
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
        return new PetArea(anchor.Bounds, anchor.DeviceRect);
    }

    private static IReadOnlyList<CodexWindow> EnumerateCodexWindows(double dpiScaleX, double dpiScaleY)
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

            var bounds = NativeMethods.ToDipRect(rect, dpiScaleX, dpiScaleY);
            var title = NativeMethods.GetWindowTitle(hwnd);
            var className = NativeMethods.GetWindowClass(hwnd);
            var isMainWindow = string.Equals(title, "Codex", StringComparison.OrdinalIgnoreCase)
                && bounds.Width > 700
                && bounds.Height > 500;
            if (isMainWindow)
            {
                return true;
            }

            if (bounds.Width > 1100 || bounds.Height > 650)
            {
                return true;
            }

            var exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GwlExStyle);
            windows.Add(new CodexWindow(hwnd, bounds, rect, title, className, exStyle));
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

        var rect = window.Bounds;
        if (rect.Width < 300 || rect.Height < 250)
        {
            return false;
        }

        if (rect.Width > 430 || rect.Height > 380)
        {
            return false;
        }

        var ratio = rect.Width / Math.Max(1, rect.Height);
        return ratio is > 0.95 and < 1.25;
    }

    private static double ScorePetCandidate(CodexWindow window)
    {
        var rect = window.Bounds;
        var titleScore = window.Title.Equals("Codex", StringComparison.OrdinalIgnoreCase) ? 25_000 : 0;
        var classScore = window.ClassName.Equals("Chrome_WidgetWin_1", StringComparison.OrdinalIgnoreCase) ? 10_000 : 0;
        return rect.Width * rect.Height + titleScore + classScore;
    }

    private sealed record CodexWindow(IntPtr Handle, Rect Bounds, Rect32 DeviceRect, string Title, string ClassName, int ExStyle);
    private sealed record PetArea(Rect Bounds, Rect32 DeviceRect);
}
