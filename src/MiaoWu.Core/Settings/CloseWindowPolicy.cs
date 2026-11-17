namespace MiaoWu.Core.Settings
{
    /// <summary>关闭窗口后实际执行的窗口动作。托盘不可用时 HideToTray 会降级为 MinimizeToTaskbar。</summary>
    public enum CloseWindowDisposition
    {
        Ask,
        HideToTray,
        MinimizeToTaskbar,
        Exit
    }

    /// <summary>
    /// 把用户选择的关闭行为与“当前进程能否注册托盘图标”合成实际动作。
    /// 低完整性进程无法调用 Shell_NotifyIcon，必须有可见的降级出口。
    /// </summary>
    public static class CloseWindowPolicy
    {
        public static CloseWindowDisposition Resolve(CloseWindowAction action, bool trayAvailable)
        {
            switch (action)
            {
                case CloseWindowAction.MinimizeToTray:
                    return trayAvailable
                        ? CloseWindowDisposition.HideToTray
                        : CloseWindowDisposition.MinimizeToTaskbar;
                case CloseWindowAction.Exit:
                    return CloseWindowDisposition.Exit;
                default:
                    return CloseWindowDisposition.Ask;
            }
        }
    }
}
