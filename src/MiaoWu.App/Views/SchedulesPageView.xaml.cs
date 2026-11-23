using System;
using System.Windows;
using System.Windows.Controls;

namespace MiaoWu.App.Views
{
    /// <summary>
    /// 定时任务页面：占满中/右栏的原三栏内容区，左栏保持不动。
    /// 打开时把焦点送到搜索框，Esc 收起由 ShellView 统一处理。
    /// </summary>
    public partial class SchedulesPageView : UserControl
    {
        public SchedulesPageView()
        {
            InitializeComponent();
            IsVisibleChanged += OnIsVisibleChanged;
        }

        private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!(bool)e.NewValue)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (SearchBox != null && IsVisible)
                {
                    SearchBox.Focus();
                }
            }));
        }
    }
}
