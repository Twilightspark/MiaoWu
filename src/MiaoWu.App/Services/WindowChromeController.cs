using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using MiaoWu.Core.Layout;
using MiaoWu.Core.Windowing;

namespace MiaoWu.App.Services
{
    /// <summary>
    /// 自绘窗口壳的原生行为：
    /// 1. Win7/无边框窗口最大化时按监视器工作区计算，避免盖住任务栏；
    /// 2. 顶栏右键与 Alt+Space 弹出系统菜单；
    /// 3. 窗口外沿：Win11 交由 DWM 画圆角与系统投影，Win10/7 保留 8px region 圆角；
    /// 4. 其余拖动、双击最大化、缩放交给 WindowChrome。
    /// </summary>
    public sealed class WindowChromeController
    {
        private const int WmGetMinMaxInfo = 0x0024;
        private const int WmNcRButtonUp = 0x00A5;
        private const int WmSysKeyDown = 0x0104;
        private const int WmSysCommand = 0x0112;
        private const int WmSize = 0x0005;
        private const int WmDpiChanged = 0x02E0;
        private const int HtCaption = 2;
        private const int VkMenu = 0x12;
        private const int VkSpace = 0x20;
        private const int DwmWindowCornerPreference = 33;
        private const int DwmWindowCornerRound = 2;
        private const uint MonitorDefaultToNearest = 2;
        private const uint TpmReturnCmd = 0x0100;
        private const uint TpmRightButton = 0x0002;
        private const double DefaultCornerRadius = 8;

        private readonly Window _window;
        private HwndSource _source;
        private IntPtr _hwnd;
        private bool _applyingRegion;
        private bool _systemFrame;

        public WindowChromeController(Window window)
        {
            _window = window ?? throw new ArgumentNullException(nameof(window));
        }

        /// <summary>
        /// 在窗口创建 HWND 之前配置玻璃框：Win11 将底边向内延伸 1px，
        /// WindowChrome 才会交给 DWM 画系统投影；Win10/7 保持 0。
        /// </summary>
        public static void ConfigureSystemFrame(Window window)
        {
            if (window == null)
            {
                throw new ArgumentNullException(nameof(window));
            }

            WindowChrome chrome = WindowChrome.GetWindowChrome(window);
            if (chrome == null)
            {
                return;
            }

            int glassFrameThickness = WindowFramePolicy.GetGlassFrameThickness(
                WindowsVersion.CurrentBuild);

            chrome.GlassFrameThickness = new Thickness(0, 0, 0, glassFrameThickness);
        }

        public void Attach()
        {
            if (_source != null)
            {
                return;
            }

            _hwnd = new WindowInteropHelper(_window).Handle;
            _source = HwndSource.FromHwnd(_hwnd);
            if (_source == null)
            {
                return;
            }

            _source.AddHook(WndProc);

            _systemFrame = WindowFramePolicy.SupportsSystemWindowShadow(
                WindowsVersion.CurrentBuild);

            if (_systemFrame)
            {
                // 圆角与投影都由 DWM 绘制；不能再设置 region，否则会裁掉投影。
                EnableSystemRoundedCorners(_hwnd);
            }
            else
            {
                ApplyRoundedCorners();
            }
        }

        public void Detach()
        {
            if (_source == null)
            {
                return;
            }

            _source.RemoveHook(WndProc);
            _source = null;
            _hwnd = IntPtr.Zero;
        }

        private IntPtr WndProc(
            IntPtr hwnd,
            int msg,
            IntPtr wParam,
            IntPtr lParam,
            ref bool handled)
        {
            switch (msg)
            {
                case WmSize:
                case WmDpiChanged:
                    ApplyRoundedCorners();
                    break;

                case WmGetMinMaxInfo:
                    ApplyMonitorWorkArea(lParam);
                    handled = true;
                    break;

                case WmNcRButtonUp:
                    if (wParam.ToInt32() == HtCaption)
                    {
                        ShowSystemMenuAtCursor();
                        handled = true;
                    }

                    break;

                case WmSysKeyDown:
                    if (wParam.ToInt32() == VkSpace && IsAltDown())
                    {
                        ShowSystemMenuAtCursor();
                        handled = true;
                    }

                    break;
            }

            return IntPtr.Zero;
        }

        private void ApplyRoundedCorners()
        {
            if (_systemFrame || _applyingRegion || _hwnd == IntPtr.Zero)
            {
                return;
            }

            _applyingRegion = true;
            try
            {
                if (IsZoomed(_hwnd) || IsIconic(_hwnd))
                {
                    SetWindowRgn(_hwnd, IntPtr.Zero, true);
                    return;
                }

                Rect rect;
                if (!GetWindowRect(_hwnd, out rect))
                {
                    return;
                }

                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;
                if (width <= 0 || height <= 0)
                {
                    return;
                }

                double scale = VisualTreeHelper.GetDpi(_window).DpiScaleX;
                int diameter = (int)Math.Round(GetCornerRadius() * scale * 2.0);
                if (diameter < 2)
                {
                    diameter = 2;
                }

                IntPtr region = CreateRoundRectRgn(0, 0, width + 1, height + 1, diameter, diameter);
                if (region == IntPtr.Zero)
                {
                    return;
                }

                if (SetWindowRgn(_hwnd, region, true) == 0)
                {
                    DeleteObject(region);
                }
            }
            finally
            {
                _applyingRegion = false;
            }
        }

        private static double GetCornerRadius()
        {
            object value = Application.Current == null
                ? null
                : Application.Current.TryFindResource("Metric.Window.CornerRadius");

            return value is double ? (double)value : DefaultCornerRadius;
        }

        /// <summary>
        /// 让 Win11 的 DWM 绘制窗口圆角。系统仍会在最大化/贴边时自动改为直角，
        /// 并在还原后恢复圆角。旧系统调用会失败，调用方不会走到这里。
        /// </summary>
        private static void EnableSystemRoundedCorners(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            int preference = DwmWindowCornerRound;
            DwmSetWindowAttribute(hwnd, DwmWindowCornerPreference, ref preference, sizeof(int));
        }

        private void ApplyMonitorWorkArea(IntPtr lParam)
        {
            IntPtr monitor = MonitorFromWindow(_hwnd, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero)
            {
                return;
            }

            var info = new MonitorInfo
            {
                cbSize = Marshal.SizeOf(typeof(MonitorInfo))
            };

            if (!GetMonitorInfo(monitor, ref info))
            {
                return;
            }

            var mmi = (MinMaxInfo)Marshal.PtrToStructure(lParam, typeof(MinMaxInfo));
            Rect work = info.rcWork;
            Rect monitorRect = info.rcMonitor;

            MaximizedWindowBounds bounds = WindowPlacementPolicy.ComputeMaximizedBounds(
                monitorRect.Left,
                monitorRect.Top,
                monitorRect.Right,
                monitorRect.Bottom,
                work.Left,
                work.Top,
                work.Right,
                work.Bottom);

            mmi.ptMaxPosition.X = bounds.X;
            mmi.ptMaxPosition.Y = bounds.Y;
            mmi.ptMaxSize.X = bounds.Width;
            mmi.ptMaxSize.Y = bounds.Height;

            // 自定义处理 WM_GETMINMAXINFO 会跳过 WPF 默认的最小尺寸写入，
            // 这里按 MinWidth/MinHeight 补回，保证拖动缩放不会小于内容设计下限。
            DpiScale dpi = VisualTreeHelper.GetDpi(_window);
            if (_window.MinWidth > 0)
            {
                int minimumTrackWidth = (int)Math.Ceiling(_window.MinWidth * dpi.DpiScaleX);
                if (minimumTrackWidth > mmi.ptMinTrackSize.X)
                {
                    mmi.ptMinTrackSize.X = minimumTrackWidth;
                }
            }

            if (_window.MinHeight > 0)
            {
                int minimumTrackHeight = (int)Math.Ceiling(_window.MinHeight * dpi.DpiScaleY);
                if (minimumTrackHeight > mmi.ptMinTrackSize.Y)
                {
                    mmi.ptMinTrackSize.Y = minimumTrackHeight;
                }
            }

            Marshal.StructureToPtr(mmi, lParam, true);
        }

        private void ShowSystemMenuAtCursor()
        {
            Point point;
            if (!GetCursorPos(out point))
            {
                return;
            }

            IntPtr menu = GetSystemMenu(_hwnd, false);
            if (menu == IntPtr.Zero)
            {
                return;
            }

            SetForegroundWindow(_hwnd);
            int command = TrackPopupMenuEx(
                menu,
                TpmReturnCmd | TpmRightButton,
                point.X,
                point.Y,
                _hwnd,
                IntPtr.Zero);

            if (command != 0)
            {
                PostMessage(_hwnd, WmSysCommand, new IntPtr(command), IntPtr.Zero);
            }
        }

        private static bool IsAltDown()
        {
            return (GetKeyState(VkMenu) & 0x8000) != 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Point
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MinMaxInfo
        {
            public Point ptReserved;
            public Point ptMaxSize;
            public Point ptMaxPosition;
            public Point ptMinTrackSize;
            public Point ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MonitorInfo
        {
            public int cbSize;
            public Rect rcMonitor;
            public Rect rcWork;
            public int dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        [DllImport("user32.dll")]
        private static extern IntPtr GetSystemMenu(IntPtr hwnd, bool revert);

        [DllImport("user32.dll")]
        private static extern int TrackPopupMenuEx(
            IntPtr menu,
            uint flags,
            int x,
            int y,
            IntPtr hwnd,
            IntPtr parameters);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll")]
        private static extern short GetKeyState(int virtualKey);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

        [DllImport("user32.dll")]
        private static extern bool IsZoomed(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(
            int left,
            int top,
            int right,
            int bottom,
            int widthEllipse,
            int heightEllipse);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr handle);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd,
            int attribute,
            ref int value,
            int size);
    }
}
