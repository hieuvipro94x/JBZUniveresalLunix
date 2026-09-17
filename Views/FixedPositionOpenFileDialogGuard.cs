using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace JBZUniveresalLunix.Views;

/// <summary>
/// Keeps one owner-bound native OpenFileDialog at a fixed size after layout.
///
/// Goals:
/// - User CANNOT resize or maximize.
/// - User CAN move the dialog by dragging the title bar.
/// - Keep a compact preferred size and shrink it when the owner monitor
///   WorkArea is smaller.
/// - Adapt to Windows language / system font automatically:
///   after Shell lays out the native child controls, measure their REAL bounds.
///   If localized controls (Open/Cancel/File name/File type/etc.) do not fit,
///   enlarge only as much as necessary, while never exceeding WorkArea.
/// - Lock the size only after the native layout is stable.
/// </summary>
internal sealed class FixedPositionOpenFileDialogGuard : IDisposable
{
    // Classic OpenFileDialog controls do not reliably stretch with their host
    // window. Keep the useful compact size on 1366 px, 2K and larger monitors;
    // WorkArea clamping below still shrinks either dimension on smaller screens.
    private const double PreferredDialogWidthDip = 640;
    private const double PreferredDialogHeightDip = 440;

    // Space between the dialog and monitor WorkArea.
    private const double MonitorMarginDip = 10;

    // Extra client padding after the furthest native child control.
    private const double ContentPaddingDip = 10;

    // Khoảng trống tối thiểu giữ cụm nút Mở/Hủy nằm trọn trong client area.
    private const double PrimaryButtonMarginDip = 10;

    private const int WhCbt = 5;
    private const int HcbtActivate = 5;

    private const int GwlStyle = -16;
    private const int DwmwaExtendedFrameBounds = 9;

    private const uint GwOwner = 4;
    private const uint MonitorDefaultToNearest = 0x00000002;

    private const int SwShow = 5;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    private const int WsThickFrame = 0x00040000;
    private const int WsMaximizeBox = 0x00010000;

    // Standard OK/Cancel IDs plus classic Common Dialog psh1/psh2 IDs.
    private const int IdOk = 1;
    private const int IdCancel = 2;
    private const int Psh1 = 0x0400;
    private const int Psh2 = 0x0401;

    private readonly IntPtr _ownerHandle;
    private readonly Dispatcher _dispatcher;
    private readonly double _ownerDpiScaleX;
    private readonly double _ownerDpiScaleY;
    private readonly HookProc _hookCallback;

    // Keep delegate alive while EnumChildWindows is executing.
    private readonly EnumWindowsProc _enumChildCallback;

    private IntPtr _hookHandle;

    // Temporary measurement state used only on UI thread.
    private IntPtr _measureDialogHandle;
    private int _measureClientOriginX;
    private int _measureClientOriginY;
    private int _furthestChildRight;
    private int _furthestChildBottom;

    public FixedPositionOpenFileDialogGuard(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        _ownerHandle = new WindowInteropHelper(owner).Handle;
        _dispatcher = owner.Dispatcher;

        DpiScale dpi = VisualTreeHelper.GetDpi(owner);
        _ownerDpiScaleX = dpi.DpiScaleX;
        _ownerDpiScaleY = dpi.DpiScaleY;

        _hookCallback = HookCallback;
        _enumChildCallback = EnumChildForMeasurement;

        if (_ownerHandle != IntPtr.Zero)
        {
            _hookHandle = SetWindowsHookEx(
                WhCbt,
                _hookCallback,
                IntPtr.Zero,
                GetCurrentThreadId());
        }
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code == HcbtActivate &&
            IsOwnedCommonDialog(wParam))
        {
            IntPtr dialogHandle = wParam;

            ReleaseCreationHook();

            // 1) Apply the normal compact target while resize style still exists.
            //    This lets the Shell process WM_SIZE and perform native re-layout.
            ApplyPreferredSizeWithinWorkArea(dialogHandle);

            // 2) Wait until Shell finishes its own localization/layout work.
            // Perform one correction only. Reapplying the preferred size and
            // scheduling several idle passes made slower PCs visibly repaint
            // and resize the dialog in stages.
            _dispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                () =>
                {
                    if (!IsWindow(dialogHandle))
                        return;

                    CorrectAndLockLayout(dialogHandle);
                });
        }

        return CallNextHookEx(
            IntPtr.Zero,
            code,
            wParam,
            lParam);
    }

    private void CorrectAndLockLayout(IntPtr dialogHandle)
    {
        if (!IsWindow(dialogHandle))
            return;

        // SetWindowPos processes WM_SIZE synchronously, so one measurement and
        // correction is sufficient before the fixed-size style is applied.
        ExpandIfNativeControlsDoNotFit(dialogHandle);

        // Final layout is now stable.
        // Bảo đảm riêng hai nút thao tác chính (Mở/Hủy) luôn nằm trọn trong
        // client area. Chỉ dịch cả cụm nút khi thực sự bị cắt, vì vậy bố cục
        // chuẩn của Windows ở các máy bình thường vẫn giữ nguyên.
        EnsurePrimaryActionButtonsVisible(dialogHandle);

        // Only NOW remove resize/maximize capability.
        LockDialogSize(dialogHandle);

        // Removing WS_THICKFRAME changes the non-client border by a few pixels.
        // Re-center only; never resize after locking.
        CenterCurrentSizeInOwnerMonitorWorkArea(dialogHandle);
    }

    private bool IsOwnedCommonDialog(IntPtr handle)
    {
        var className = new StringBuilder(64);

        return GetClassName(
                   handle,
                   className,
                   className.Capacity) > 0 &&
               className.ToString().Equals(
                   "#32770",
                   StringComparison.Ordinal) &&
               GetWindow(handle, GwOwner) == _ownerHandle;
    }

    /// <summary>
    /// Applies the normal preferred fixed size, limited independently by
    /// the current monitor WorkArea.
    ///
    /// Width and Height are NOT proportionally scaled together.
    /// If only one dimension does not fit, only that dimension is limited.
    /// </summary>
    private void ApplyPreferredSizeWithinWorkArea(
        IntPtr dialogHandle)
    {
        if (!TryGetMonitorAndWindow(
                dialogHandle,
                out MonitorInfo monitorInfo,
                out Rect windowRect))
        {
            return;
        }

        GetDialogDpiScale(
            dialogHandle,
            out double dpiScaleX,
            out double dpiScaleY);

        Rect visibleFrame =
            GetVisibleFrameRect(dialogHandle, windowRect);

        GetInvisibleFrameInsets(
            windowRect,
            visibleFrame,
            out int insetLeft,
            out int insetTop,
            out int insetRight,
            out int insetBottom);

        int marginX = DipToPixels(
            MonitorMarginDip,
            dpiScaleX);

        int marginY = DipToPixels(
            MonitorMarginDip,
            dpiScaleY);

        int maxVisibleWidth = Math.Max(
            1,
            monitorInfo.WorkArea.Width - (marginX * 2));

        int maxVisibleHeight = Math.Max(
            1,
            monitorInfo.WorkArea.Height - (marginY * 2));

        int preferredVisibleWidth = DipToPixels(
            PreferredDialogWidthDip,
            dpiScaleX);

        int preferredVisibleHeight = DipToPixels(
            PreferredDialogHeightDip,
            dpiScaleY);

        int visibleWidth = Math.Min(
            preferredVisibleWidth,
            maxVisibleWidth);

        int visibleHeight = Math.Min(
            preferredVisibleHeight,
            maxVisibleHeight);

        int outerWidth = Math.Min(
            monitorInfo.WorkArea.Width,
            visibleWidth + insetLeft + insetRight);

        int outerHeight = Math.Min(
            monitorInfo.WorkArea.Height,
            visibleHeight + insetTop + insetBottom);

        SetCenteredWindowSize(
            dialogHandle,
            monitorInfo,
            outerWidth,
            outerHeight,
            visibleWidth,
            visibleHeight,
            insetLeft,
            insetTop);
    }

    /// <summary>
    /// Measures the REAL child-window positions produced by the current Windows
    /// language/font/DPI. If any visible native control lies outside the dialog's
    /// client area, enlarge the dialog just enough to include it.
    ///
    /// This is the important locale-aware part:
    /// no Korean/Vietnamese/English string length is hard-coded.
    /// </summary>
    private bool ExpandIfNativeControlsDoNotFit(
        IntPtr dialogHandle)
    {
        if (!TryGetMonitorAndWindow(
                dialogHandle,
                out MonitorInfo monitorInfo,
                out Rect windowRect))
        {
            return false;
        }

        if (!GetClientRect(
                dialogHandle,
                out Rect clientRect))
        {
            return false;
        }

        var clientOrigin = new PointNative
        {
            X = 0,
            Y = 0
        };

        if (!ClientToScreen(
                dialogHandle,
                ref clientOrigin))
        {
            return false;
        }

        GetDialogDpiScale(
            dialogHandle,
            out double dpiScaleX,
            out double dpiScaleY);

        int paddingX = DipToPixels(
            ContentPaddingDip,
            dpiScaleX);

        int paddingY = DipToPixels(
            ContentPaddingDip,
            dpiScaleY);

        _measureDialogHandle = dialogHandle;
        _measureClientOriginX = clientOrigin.X;
        _measureClientOriginY = clientOrigin.Y;

        // Start with current client boundary.
        _furthestChildRight =
            clientOrigin.X + clientRect.Width;

        _furthestChildBottom =
            clientOrigin.Y + clientRect.Height;

        // EnumChildWindows includes descendant child windows too.
        EnumChildWindows(
            dialogHandle,
            _enumChildCallback,
            IntPtr.Zero);

        int currentClientRight =
            clientOrigin.X + clientRect.Width;

        int currentClientBottom =
            clientOrigin.Y + clientRect.Height;

        int overflowRight = Math.Max(
            0,
            _furthestChildRight - currentClientRight);

        int overflowBottom = Math.Max(
            0,
            _furthestChildBottom - currentClientBottom);

        // Add padding only when a native control is actually clipped.
        // Controls that legitimately touch/anchor to the client edge must not
        // cause the dialog to grow on every correction pass.
        int missingRight =
            overflowRight > 0
                ? overflowRight + paddingX
                : 0;

        int missingBottom =
            overflowBottom > 0
                ? overflowBottom + paddingY
                : 0;

        _measureDialogHandle = IntPtr.Zero;

        if (missingRight == 0 &&
            missingBottom == 0)
        {
            return false;
        }

        Rect visibleFrame =
            GetVisibleFrameRect(dialogHandle, windowRect);

        GetInvisibleFrameInsets(
            windowRect,
            visibleFrame,
            out int insetLeft,
            out int insetTop,
            out int insetRight,
            out int insetBottom);

        int marginX = DipToPixels(
            MonitorMarginDip,
            dpiScaleX);

        int marginY = DipToPixels(
            MonitorMarginDip,
            dpiScaleY);

        int maxVisibleWidth = Math.Max(
            1,
            monitorInfo.WorkArea.Width - (marginX * 2));

        int maxVisibleHeight = Math.Max(
            1,
            monitorInfo.WorkArea.Height - (marginY * 2));

        int currentVisibleWidth =
            Math.Max(1, visibleFrame.Width);

        int currentVisibleHeight =
            Math.Max(1, visibleFrame.Height);

        // Expand only the dimension that actually needs more space.
        int targetVisibleWidth = Math.Min(
            maxVisibleWidth,
            currentVisibleWidth + missingRight);

        int targetVisibleHeight = Math.Min(
            maxVisibleHeight,
            currentVisibleHeight + missingBottom);

        // WorkArea cannot accommodate anything larger.
        // In that case this is the maximum physically possible fixed dialog.
        if (targetVisibleWidth == currentVisibleWidth &&
            targetVisibleHeight == currentVisibleHeight)
        {
            return false;
        }

        int targetOuterWidth = Math.Min(
            monitorInfo.WorkArea.Width,
            targetVisibleWidth + insetLeft + insetRight);

        int targetOuterHeight = Math.Min(
            monitorInfo.WorkArea.Height,
            targetVisibleHeight + insetTop + insetBottom);

        SetCenteredWindowSize(
            dialogHandle,
            monitorInfo,
            targetOuterWidth,
            targetOuterHeight,
            targetVisibleWidth,
            targetVisibleHeight,
            insetLeft,
            insetTop);

        return true;
    }

    /// <summary>
    /// Called by EnumChildWindows.
    ///
    /// We measure native controls that Windows considers visible.
    /// WS_VISIBLE controls may still be clipped by the parent, which is exactly
    /// what we need to detect.
    /// </summary>
    private bool EnumChildForMeasurement(
        IntPtr childHandle,
        IntPtr lParam)
    {
        if (_measureDialogHandle == IntPtr.Zero)
            return false;

        int controlId = GetDlgCtrlID(childHandle);

        bool isPrimaryActionButton =
            controlId == IdOk ||
            controlId == IdCancel ||
            controlId == Psh1 ||
            controlId == Psh2;

        // Normally measure visible controls. Also keep measuring Open/Cancel even
        // if Shell temporarily hides them because the forced width is too small.
        if (!IsWindowVisible(childHandle) &&
            !isPrimaryActionButton)
        {
            return true;
        }

        if (!GetWindowRect(
                childHandle,
                out Rect childRect))
        {
            return true;
        }

        // Ignore zero-sized implementation windows.
        if (childRect.Width <= 0 ||
            childRect.Height <= 0)
        {
            return true;
        }

        // Only care about descendants whose geometry belongs around this dialog.
        //
        // Do not compare text/language here. Actual Windows geometry is the source
        // of truth, so Korean, Vietnamese, English, Japanese, etc. all use one path.
        _furthestChildRight = Math.Max(
            _furthestChildRight,
            childRect.Right);

        _furthestChildBottom = Math.Max(
            _furthestChildBottom,
            childRect.Bottom);

        return true;
    }

    /// <summary>
    /// Giữ nguyên bố cục native của Windows nếu hai nút Mở/Hủy đã nằm đúng chỗ.
    /// Nếu do DPI/độ phân giải/ngôn ngữ làm cụm nút bị cắt ở mép phải hoặc mép
    /// dưới, dịch NGUYÊN CỤM vào trong client area. Không đổi kích thước nút,
    /// không đổi text và không xếp lại ngang/dọc, nên giao diện vẫn giống dialog gốc.
    /// </summary>
    private void EnsurePrimaryActionButtonsVisible(IntPtr dialogHandle)
    {
        if (!GetClientRect(dialogHandle, out Rect clientRect))
            return;

        var clientOrigin = new PointNative { X = 0, Y = 0 };
        if (!ClientToScreen(dialogHandle, ref clientOrigin))
            return;

        IntPtr okButton = FindPrimaryButton(dialogHandle, IdOk, Psh1);
        IntPtr cancelButton = FindPrimaryButton(dialogHandle, IdCancel, Psh2);

        if (okButton == IntPtr.Zero && cancelButton == IntPtr.Zero)
            return;

        // Common Dialog đôi khi tạm ẩn button trong một vòng layout.
        // Sau khi layout ổn định, hai nút thao tác chính luôn phải hiện.
        if (okButton != IntPtr.Zero)
            ShowWindow(okButton, SwShow);
        if (cancelButton != IntPtr.Zero)
            ShowWindow(cancelButton, SwShow);

        var buttons = new List<(IntPtr Handle, Rect Rect)>();

        if (okButton != IntPtr.Zero &&
            GetWindowRect(okButton, out Rect okRect) &&
            okRect.Width > 0 &&
            okRect.Height > 0)
        {
            buttons.Add((okButton, okRect));
        }

        if (cancelButton != IntPtr.Zero &&
            GetWindowRect(cancelButton, out Rect cancelRect) &&
            cancelRect.Width > 0 &&
            cancelRect.Height > 0)
        {
            buttons.Add((cancelButton, cancelRect));
        }

        if (buttons.Count == 0)
            return;

        GetDialogDpiScale(
            dialogHandle,
            out double dpiScaleX,
            out double dpiScaleY);

        int marginX = DipToPixels(
            PrimaryButtonMarginDip,
            dpiScaleX);

        int marginY = DipToPixels(
            PrimaryButtonMarginDip,
            dpiScaleY);

        int groupLeft = buttons.Min(item => item.Rect.Left) - clientOrigin.X;
        int groupTop = buttons.Min(item => item.Rect.Top) - clientOrigin.Y;
        int groupRight = buttons.Max(item => item.Rect.Right) - clientOrigin.X;
        int groupBottom = buttons.Max(item => item.Rect.Bottom) - clientOrigin.Y;

        int dx = 0;
        int dy = 0;

        int maxRight = Math.Max(marginX, clientRect.Width - marginX);
        int maxBottom = Math.Max(marginY, clientRect.Height - marginY);

        if (groupRight > maxRight)
            dx = maxRight - groupRight;
        else if (groupLeft < marginX)
            dx = marginX - groupLeft;

        if (groupBottom > maxBottom)
            dy = maxBottom - groupBottom;
        else if (groupTop < marginY)
            dy = marginY - groupTop;

        if (dx == 0 && dy == 0)
            return;

        foreach ((IntPtr handle, Rect rect) in buttons)
        {
            int x = (rect.Left - clientOrigin.X) + dx;
            int y = (rect.Top - clientOrigin.Y) + dy;

            SetWindowPos(
                handle,
                IntPtr.Zero,
                x,
                y,
                0,
                0,
                SwpNoSize |
                SwpNoZOrder |
                SwpNoActivate);
        }
    }

    private static IntPtr FindPrimaryButton(
        IntPtr dialogHandle,
        int standardId,
        int classicId)
    {
        IntPtr handle = GetDlgItem(dialogHandle, standardId);
        if (handle != IntPtr.Zero)
            return handle;

        return GetDlgItem(dialogHandle, classicId);
    }

    private static void SetCenteredWindowSize(
        IntPtr dialogHandle,
        MonitorInfo monitorInfo,
        int outerWidth,
        int outerHeight,
        int visibleWidth,
        int visibleHeight,
        int invisibleInsetLeft,
        int invisibleInsetTop)
    {
        int visibleX =
            monitorInfo.WorkArea.Left +
            ((monitorInfo.WorkArea.Width - visibleWidth) / 2);

        int visibleY =
            monitorInfo.WorkArea.Top +
            ((monitorInfo.WorkArea.Height - visibleHeight) / 2);

        int x = visibleX - invisibleInsetLeft;
        int y = visibleY - invisibleInsetTop;

        SetWindowPos(
            dialogHandle,
            IntPtr.Zero,
            x,
            y,
            Math.Max(1, outerWidth),
            Math.Max(1, outerHeight),
            SwpNoZOrder | SwpNoActivate);
    }

    private void CenterCurrentSizeInOwnerMonitorWorkArea(
        IntPtr dialogHandle)
    {
        if (!TryGetMonitorAndWindow(
                dialogHandle,
                out MonitorInfo monitorInfo,
                out Rect windowRect))
        {
            return;
        }

        Rect visibleFrame =
            GetVisibleFrameRect(dialogHandle, windowRect);

        int visibleWidth = Math.Max(
            1,
            visibleFrame.Width);

        int visibleHeight = Math.Max(
            1,
            visibleFrame.Height);

        int x =
            monitorInfo.WorkArea.Left +
            ((monitorInfo.WorkArea.Width - visibleWidth) / 2) -
            (visibleFrame.Left - windowRect.Left);

        int y =
            monitorInfo.WorkArea.Top +
            ((monitorInfo.WorkArea.Height - visibleHeight) / 2) -
            (visibleFrame.Top - windowRect.Top);

        SetWindowPos(
            dialogHandle,
            IntPtr.Zero,
            x,
            y,
            0,
            0,
            SwpNoSize |
            SwpNoZOrder |
            SwpNoActivate);
    }

    private static void LockDialogSize(
        IntPtr dialogHandle)
    {
        int style = GetWindowLong(
            dialogHandle,
            GwlStyle);

        int fixedSizeStyle =
            style &
            ~WsThickFrame &
            ~WsMaximizeBox;

        if (fixedSizeStyle == style)
            return;

        SetWindowLong(
            dialogHandle,
            GwlStyle,
            fixedSizeStyle);

        SetWindowPos(
            dialogHandle,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            SwpNoSize |
            SwpNoMove |
            SwpNoZOrder |
            SwpNoActivate |
            SwpFrameChanged);
    }

    private bool TryGetMonitorAndWindow(
        IntPtr dialogHandle,
        out MonitorInfo monitorInfo,
        out Rect windowRect)
    {
        monitorInfo = new MonitorInfo
        {
            Size = (uint)Marshal.SizeOf<MonitorInfo>()
        };

        windowRect = default;

        IntPtr monitor = MonitorFromWindow(
            _ownerHandle,
            MonitorDefaultToNearest);

        return monitor != IntPtr.Zero &&
               GetMonitorInfo(
                   monitor,
                   ref monitorInfo) &&
               GetWindowRect(
                   dialogHandle,
                   out windowRect);
    }

    private void GetDialogDpiScale(
        IntPtr dialogHandle,
        out double dpiScaleX,
        out double dpiScaleY)
    {
        uint dpi = GetDpiForWindow(dialogHandle);

        if (dpi > 0)
        {
            dpiScaleX = dpi / 96.0;
            dpiScaleY = dpi / 96.0;
            return;
        }

        dpiScaleX = _ownerDpiScaleX;
        dpiScaleY = _ownerDpiScaleY;
    }

    private static int DipToPixels(
        double dip,
        double dpiScale)
    {
        return Math.Max(
            1,
            (int)Math.Round(dip * dpiScale));
    }

    private static Rect GetVisibleFrameRect(
        IntPtr dialogHandle,
        Rect windowRect)
    {
        if (DwmGetWindowAttribute(
                dialogHandle,
                DwmwaExtendedFrameBounds,
                out Rect measuredFrameRect,
                Marshal.SizeOf<Rect>()) == 0)
        {
            return measuredFrameRect;
        }

        return windowRect;
    }

    private static void GetInvisibleFrameInsets(
        Rect windowRect,
        Rect visibleFrame,
        out int left,
        out int top,
        out int right,
        out int bottom)
    {
        left = Math.Max(
            0,
            visibleFrame.Left - windowRect.Left);

        top = Math.Max(
            0,
            visibleFrame.Top - windowRect.Top);

        right = Math.Max(
            0,
            windowRect.Right - visibleFrame.Right);

        bottom = Math.Max(
            0,
            windowRect.Bottom - visibleFrame.Bottom);
    }

    private void ReleaseCreationHook()
    {
        if (_hookHandle == IntPtr.Zero)
            return;

        UnhookWindowsHookEx(_hookHandle);
        _hookHandle = IntPtr.Zero;
    }

    public void Dispose()
    {
        ReleaseCreationHook();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public Rect MonitorArea;
        public Rect WorkArea;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointNative
    {
        public int X;
        public int Y;
    }

    private delegate IntPtr HookProc(
        int code,
        IntPtr wParam,
        IntPtr lParam);

    private delegate bool EnumWindowsProc(
        IntPtr hWnd,
        IntPtr lParam);

    [DllImport(
        "user32.dll",
        SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hook,
        HookProc callback,
        IntPtr module,
        uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(
        IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hook,
        int code,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport(
        "user32.dll",
        CharSet = CharSet.Unicode)]
    private static extern int GetClassName(
        IntPtr handle,
        StringBuilder className,
        int maxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(
        IntPtr handle,
        uint command);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(
        IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(
        IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDlgItem(
        IntPtr dialogHandle,
        int controlId);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(
        IntPtr handle,
        int command);

    [DllImport("user32.dll")]
    private static extern int GetDlgCtrlID(
        IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(
        IntPtr parentHandle,
        EnumWindowsProc callback,
        IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(
        IntPtr handle,
        uint flags);

    [DllImport(
        "user32.dll",
        CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(
        IntPtr monitor,
        ref MonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(
        IntPtr handle,
        out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(
        IntPtr handle,
        out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(
        IntPtr handle,
        ref PointNative point);

    [DllImport(
        "user32.dll",
        SetLastError = true)]
    private static extern int GetWindowLong(
        IntPtr handle,
        int index);

    [DllImport(
        "user32.dll",
        SetLastError = true)]
    private static extern int SetWindowLong(
        IntPtr handle,
        int index,
        int newValue);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(
        IntPtr handle);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr handle,
        int attribute,
        out Rect value,
        int valueSize);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr handle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
