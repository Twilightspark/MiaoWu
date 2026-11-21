using System;
using System.Windows;
using MiaoWu.App.ViewModels;

namespace MiaoWu.App.Services
{
    /// <summary>二次确认。抽成接口，避免 ViewModel 直接弹系统 MessageBox 或操作浮层。</summary>
    public interface IDialogService
    {
        /// <summary>
        /// 询问用户是否继续。返回 true 表示用户点了确认按钮。
        /// </summary>
        /// <param name="destructive">
        /// 危险操作（删除、覆盖、清空）：确认按钮用 Danger 前景色；
        /// 只从列表移除工作区这类可逆操作传 false，确认按钮保持普通文字色。
        /// </param>
        bool Confirm(string title, string message, string confirmText, string cancelText, bool destructive = true);
    }

    /// <summary>
    /// 应用内确认浮层实现：复用关闭确认浮层的卡片样式，不新开 Window。
    /// 同步阻塞到用户选择，调用方无需改成异步。
    /// </summary>
    public sealed class ConfirmDialogService : IDialogService
    {
        private readonly ConfirmPromptViewModel _prompt;

        public ConfirmDialogService(ConfirmPromptViewModel prompt)
        {
            _prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));
        }

        public bool Confirm(
            string title,
            string message,
            string confirmText,
            string cancelText,
            bool destructive = true)
        {
            // 没有 Application（纯单元测试 / 设计时）就不弹窗，按取消处理，避免静默放行删除。
            if (Application.Current == null || Application.Current.Dispatcher == null)
            {
                return false;
            }

            return _prompt.Confirm(title, message, confirmText, cancelText, destructive);
        }
    }
}
