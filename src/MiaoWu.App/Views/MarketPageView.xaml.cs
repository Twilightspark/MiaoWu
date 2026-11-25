using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MiaoWu.App.ViewModels;

namespace MiaoWu.App.Views
{
    /// <summary>
    /// 能力市场页面：占满中/右栏，左栏保持不动。默认只有技能目录，
    /// 选中技能后详情从右侧滑出；Esc 先收详情，再按一次关闭页面。
    /// </summary>
    public partial class MarketPageView : UserControl
    {
        public MarketPageView()
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

        private void OnMarketContentSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (MarketDetailPane == null || e == null || e.NewSize.Width <= 0)
            {
                return;
            }

            // 与定时任务页同一比例：列表 53%，详情 47%。
            MarketDetailPane.ExpandedWidth = Math.Max(0, Math.Round(e.NewSize.Width * 0.47));
        }

        private void OnPageMenuClick(object sender, RoutedEventArgs e)
        {
            OpenMenu(sender as Button);
        }

        private void OnDetailMenuClick(object sender, RoutedEventArgs e)
        {
            OpenMenu(sender as Button);
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            MarketViewModel page = DataContext as MarketViewModel;
            if (page == null)
            {
                return;
            }

            if (e.Key == Key.Escape)
            {
                page.Escape();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.F5)
            {
                if (page.RefreshCommand.CanExecute(null))
                {
                    page.RefreshCommand.Execute(null);
                }

                e.Handled = true;
            }
        }

        private static void OpenMenu(Button button)
        {
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
