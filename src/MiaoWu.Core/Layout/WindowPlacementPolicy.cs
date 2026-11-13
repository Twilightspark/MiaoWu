using System;

namespace MiaoWu.Core.Layout
{
    /// <summary>
    /// 启动窗口尺寸的收敛策略：默认 1440x900，工作区不足时按 90% 收敛，
    /// 并保证窗口至少能显示到设计最小尺寸或整个工作区。
    /// 恢复上次窗口尺寸时不再套用 90%，只做工作区上限收敛。
    /// </summary>
    public sealed class WindowPlacementMetrics
    {
        public double DefaultWidth { get; set; } = 1440;

        public double DefaultHeight { get; set; } = 900;

        public double MinimumWidth { get; set; } = 1080;

        public double MinimumHeight { get; set; } = 760;

        /// <summary>工作区不足时使用的比例。</summary>
        public double WorkAreaRatio { get; set; } = 0.9;

        /// <summary>
        /// true 表示 <see cref="DefaultWidth"/> / <see cref="DefaultHeight"/> 来自上次退出时
        /// 记录的窗口尺寸，直接收敛到工作区，不再按 <see cref="WorkAreaRatio"/> 缩小。
        /// </summary>
        public bool RestoreSavedSize { get; set; }
    }

    public struct WindowPlacement
    {
        public WindowPlacement(
            double left,
            double top,
            double width,
            double height,
            double minimumWidth,
            double minimumHeight)
        {
            Left = left;
            Top = top;
            Width = width;
            Height = height;
            MinimumWidth = minimumWidth;
            MinimumHeight = minimumHeight;
        }

        public double Left { get; }

        public double Top { get; }

        public double Width { get; }

        public double Height { get; }

        public double MinimumWidth { get; }

        public double MinimumHeight { get; }
    }

    public static class WindowPlacementPolicy
    {
        public static WindowPlacement Compute(
            double workLeft,
            double workTop,
            double workWidth,
            double workHeight,
            WindowPlacementMetrics metrics = null)
        {
            metrics = metrics ?? new WindowPlacementMetrics();

            if (workWidth <= 0 || workHeight <= 0)
            {
                return new WindowPlacement(
                    workLeft,
                    workTop,
                    metrics.DefaultWidth,
                    metrics.DefaultHeight,
                    metrics.MinimumWidth,
                    metrics.MinimumHeight);
            }

            double ratio = metrics.RestoreSavedSize ? 1.0 : metrics.WorkAreaRatio;
            double width = Math.Min(metrics.DefaultWidth, workWidth * ratio);
            double height = Math.Min(metrics.DefaultHeight, workHeight * ratio);

            double minimumWidth = Math.Min(metrics.MinimumWidth, workWidth);
            double minimumHeight = Math.Min(metrics.MinimumHeight, workHeight);

            width = Math.Max(width, minimumWidth);
            height = Math.Max(height, minimumHeight);

            double left = workLeft + ((workWidth - width) / 2.0);
            double top = workTop + ((workHeight - height) / 2.0);

            return new WindowPlacement(left, top, width, height, minimumWidth, minimumHeight);
        }

        /// <summary>
        /// 计算无边框窗口最大化时传给 WM_GETMINMAXINFO 的位置与尺寸。
        /// ptMaxPosition 相对监视器原点，ptMaxSize 为工作区尺寸。
        /// </summary>
        public static MaximizedWindowBounds ComputeMaximizedBounds(
            int monitorLeft,
            int monitorTop,
            int monitorRight,
            int monitorBottom,
            int workLeft,
            int workTop,
            int workRight,
            int workBottom)
        {
            return new MaximizedWindowBounds(
                Math.Abs(workLeft - monitorLeft),
                Math.Abs(workTop - monitorTop),
                Math.Abs(workRight - workLeft),
                Math.Abs(workBottom - workTop));
        }
    }

    public struct MaximizedWindowBounds
    {
        public MaximizedWindowBounds(int x, int y, int width, int height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public int X { get; }

        public int Y { get; }

        public int Width { get; }

        public int Height { get; }
    }
}
