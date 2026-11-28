using System;

namespace MiaoWu.App.Services
{
    /// <summary>
    /// 系统通知能力。回答完成这类普通提示不再占应用内的输入区，交给操作系统去提醒；
    /// 审批与提问仍然走应用内弹窗，因为必须在本窗口里显式处理。
    /// </summary>
    public interface ISystemNotificationService
    {
        /// <summary>当前进程能否发系统通知（需要托盘图标；低完整性进程为 false）。</summary>
        bool IsAvailable { get; }

        /// <summary>发一条系统通知；窗口在前台时压掉，返回值表示是否真的发出。</summary>
        bool Notify(string title, string text);
    }

    /// <summary>
    /// 托盘气泡实现（<c>Shell_NotifyIcon</c> 的 <c>NIF_INFO</c>）：Win7 起可用，
    /// 不引入运行时依赖。低完整性进程没有托盘图标，静默降级为不发。
    /// </summary>
    public sealed class SystemNotificationService : ISystemNotificationService
    {
        private readonly Func<bool> _isAppInForeground;
        private TrayService _tray;

        /// <param name="isAppInForeground">
        /// 窗口是否在前台；为 true 时不弹系统通知，避免在用户眼前重复一遍界面上已经能看到的状态。
        /// </param>
        public SystemNotificationService(Func<bool> isAppInForeground = null)
        {
            _isAppInForeground = isAppInForeground;
        }

        public bool IsAvailable
        {
            get { return _tray != null && _tray.IsAvailable; }
        }

        /// <summary>托盘创建后接上；托盘不可用时保持为 null，通知静默丢弃。</summary>
        public void Attach(TrayService tray)
        {
            _tray = tray;
        }

        public bool Notify(string title, string text)
        {
            if (!IsAvailable)
            {
                return false;
            }

            if (_isAppInForeground != null && _isAppInForeground())
            {
                return false;
            }

            _tray.ShowBalloon(title, text);
            return true;
        }
    }
}
