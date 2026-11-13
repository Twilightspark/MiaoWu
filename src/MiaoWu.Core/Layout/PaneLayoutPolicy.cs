using System;

namespace MiaoWu.Core.Layout
{
    /// <summary>
    /// 三栏布局的尺寸约束。数值来自 docs/03-ui-design.md 第 4 节。
    /// </summary>
    public sealed class PaneMetrics
    {
        public double LeftMin { get; set; } = 208;

        public double LeftMax { get; set; } = 320;

        public double LeftDefault { get; set; } = 262;

        public double RightMin { get; set; } = 384;

        public double RightMax { get; set; } = 560;

        public double RightDefault { get; set; } = 428;

        public double MiddleMin { get; set; } = 480;

        public double Splitter { get; set; } = 4;

        /// <summary>三栏内容设计的最小高度，窗口高度不得低于该值。</summary>
        public double MinimumHeight { get; set; } = 760;

        /// <summary>设计基准窗口宽度：1440 时是 262 / 742 / 428。</summary>
        public double ReferenceWidth { get; set; } = 1440;
    }

    /// <summary>
    /// 一次布局计算的结果，单位为设备无关像素（DIP）。
    /// </summary>
    public struct PaneLayout
    {
        public PaneLayout(double left, double middle, double right, bool degraded)
        {
            Left = left;
            Middle = middle;
            Right = right;
            Degraded = degraded;
        }

        public double Left { get; }

        public double Middle { get; }

        public double Right { get; }

        /// <summary>窗口窄于该布局能支持的最小宽度时为 true。</summary>
        public bool Degraded { get; }
    }

    /// <summary>
    /// 三栏宽度策略：
    /// 1. 以 1440 为设计基准；
    /// 2. 窗口变窄时先压缩左栏（到 208），再压缩右栏（到 384），最后压缩中栏（保底 480）；
    /// 3. 折叠右栏时右栏为 0，左栏仍按同一顺序收敛；
    /// 4. 用户拖动分隔条后，以新的 preferred 宽度为准，窗口缩放仍按上面的顺序让位。
    /// </summary>
    public sealed class PaneLayoutPolicy
    {
        private readonly PaneMetrics _metrics;

        public PaneLayoutPolicy(PaneMetrics metrics)
        {
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
            LeftPreferred = metrics.LeftDefault;
            RightPreferred = metrics.RightDefault;
        }

        public double LeftPreferred { get; private set; }

        public double RightPreferred { get; private set; }

        /// <summary>
        /// 三栏展开时内容区的最小宽度：左栏、中栏、右栏的最小宽度加两条分隔条。
        /// 窗口最小宽度由这个值决定，不再单独写死。
        /// </summary>
        public double MinimumWindowWidth
        {
            get
            {
                return _metrics.LeftMin
                    + _metrics.MiddleMin
                    + _metrics.RightMin
                    + (_metrics.Splitter * 2);
            }
        }

        /// <summary>三栏内容设计的最小高度。</summary>
        public double MinimumWindowHeight
        {
            get { return _metrics.MinimumHeight; }
        }

        public PaneLayout Compute(double windowWidth, bool rightCollapsed)
        {
            if (windowWidth <= 0)
            {
                windowWidth = _metrics.ReferenceWidth;
            }

            double left = Clamp(LeftPreferred, _metrics.LeftMin, _metrics.LeftMax);
            double right = rightCollapsed
                ? 0
                : Clamp(RightPreferred, _metrics.RightMin, _metrics.RightMax);

            double deficit = _metrics.ReferenceWidth - windowWidth;
            if (deficit > 0)
            {
                double leftRoom = Math.Max(0, left - _metrics.LeftMin);
                double takeLeft = Math.Min(deficit, leftRoom);
                left -= takeLeft;
                deficit -= takeLeft;

                double rightRoom = Math.Max(0, right - _metrics.RightMin);
                double takeRight = Math.Min(deficit, rightRoom);
                right -= takeRight;
            }

            double middle = windowWidth - left - right - (_metrics.Splitter * 2);
            if (middle < 0)
            {
                middle = 0;
            }

            return new PaneLayout(
                left,
                middle,
                right,
                middle < _metrics.MiddleMin);
        }

        public void SetPreferred(double left, double right)
        {
            LeftPreferred = Clamp(left, _metrics.LeftMin, _metrics.LeftMax);
            RightPreferred = Clamp(right, _metrics.RightMin, _metrics.RightMax);
        }

        /// <summary>当前窗口宽度下左栏允许的最大值（保证中栏不低于 480）。</summary>
        public double GetLeftMaximum(double windowWidth, double rightWidth)
        {
            double available = windowWidth - rightWidth - (_metrics.Splitter * 2) - _metrics.MiddleMin;
            return Math.Max(_metrics.LeftMin, Math.Min(_metrics.LeftMax, available));
        }

        /// <summary>当前窗口宽度下右栏允许的最大值（保证中栏不低于 480）。</summary>
        public double GetRightMaximum(double windowWidth, double leftWidth)
        {
            double available = windowWidth - leftWidth - (_metrics.Splitter * 2) - _metrics.MiddleMin;
            return Math.Max(_metrics.RightMin, Math.Min(_metrics.RightMax, available));
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            if (value < minimum)
            {
                return minimum;
            }

            if (value > maximum)
            {
                return maximum;
            }

            return value;
        }
    }
}
