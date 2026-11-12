namespace MiaoWu.Core.Windowing
{
    /// <summary>
    /// 窗口外沿的呈现能力。
    /// Windows 11（build 22000+）由 DWM 负责窗口圆角与窗口外投影：把 WindowChrome 的玻璃框
    /// 向内延伸 1px 即可让系统绘制投影，但此时不能再给窗口设置 region，否则投影会被裁掉。
    /// Windows 10 及更早系统保留应用自绘的 8px region 圆角，没有系统外投影。
    /// </summary>
    public static class WindowFramePolicy
    {
        /// <summary>Windows 11 的首个正式版本 build。</summary>
        public const int FirstSystemFrameBuild = 22000;

        /// <summary>系统投影所需的玻璃框厚度（窗口底部，设备无关单位）。</summary>
        public const int SystemGlassFrameThickness = 1;

        /// <summary>当前系统是否能用 DWM 绘制窗口圆角与窗口外投影。</summary>
        public static bool SupportsSystemWindowShadow(int osBuild)
        {
            return osBuild >= FirstSystemFrameBuild;
        }

        /// <summary>WindowChrome 底部玻璃框厚度：系统投影开启时为 1，否则为 0。</summary>
        public static int GetGlassFrameThickness(int osBuild)
        {
            return SupportsSystemWindowShadow(osBuild) ? SystemGlassFrameThickness : 0;
        }
    }
}
