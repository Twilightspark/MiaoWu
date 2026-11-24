using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MiaoWu.App.ViewModels;

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

        private void OnSchedulesContentSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (SchedulesDetailPane == null || e == null || e.NewSize.Width <= 0)
            {
                return;
            }

            // 详情列占内容宽度的 47%，与列表 53% 的分栏比例一致；
            // 收起 / 展开时只动这一列的宽度，列表自动补满剩下的空间。
            SchedulesDetailPane.ExpandedWidth = Math.Max(0, Math.Round(e.NewSize.Width * 0.47));
        }

        private void OnModelSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e == null || e.AddedItems == null || e.AddedItems.Count == 0)
            {
                return;
            }

            ScheduleModelOption option = e.AddedItems[0] as ScheduleModelOption;
            SchedulesViewModel page = DataContext as SchedulesViewModel;
            if (option != null && page != null && page.Editor != null)
            {
                page.Editor.SelectedModelOption = option;
            }
        }

        private void OnTaskMenuClick(object sender, RoutedEventArgs e)
        {
            Button button = sender as Button;
            if (button == null || button.ContextMenu == null)
            {
                return;
            }

            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.Placement = PlacementMode.Bottom;
            button.ContextMenu.IsOpen = true;
        }
    }
}
