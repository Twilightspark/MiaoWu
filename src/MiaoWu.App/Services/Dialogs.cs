using System.Windows;

namespace MiaoWu.App.Services
{
    /// <summary>确认对话框。抽成接口，避免 ViewModel 直接弹 MessageBox。</summary>
    public interface IDialogService
    {
        bool Confirm(string title, string message, string confirmText, string cancelText);
    }

    /// <summary>系统 MessageBox 实现；V1 只在移除工作区等不可撤销列表操作上使用。</summary>
    public sealed class MessageBoxDialogService : IDialogService
    {
        public bool Confirm(string title, string message, string confirmText, string cancelText)
        {
            MessageBoxResult result = MessageBox.Show(
                message,
                title,
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question,
                MessageBoxResult.Cancel);

            return result == MessageBoxResult.OK;
        }
    }
}
