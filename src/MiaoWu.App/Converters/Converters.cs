using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace MiaoWu.App.Converters
{
    /// <summary>把 DIP 宽度转换为 GridLength，用于列宽绑定。</summary>
    public sealed class DoubleToGridLengthConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double width = value is double ? (double)value : 0;
            return new GridLength(width);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is GridLength)
            {
                return ((GridLength)value).Value;
            }

            return 0d;
        }
    }

    /// <summary>
    /// 0–100 的百分比 → 14×14 盒内的圆环弧线（从 12 点开始顺时针）。
    /// 只用来画上下文占用环，0 返回空几何，100 返回整圆。
    /// </summary>
    public sealed class PercentToArcConverter : IValueConverter
    {
        private const double Box = 14;
        private const double Radius = 6;
        private static readonly Point Center = new Point(Box / 2, Box / 2);

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double percent = value is double ? (double)value : 0;
            if (percent <= 0)
            {
                return Geometry.Empty;
            }

            if (percent >= 100)
            {
                return new EllipseGeometry(Center, Radius, Radius);
            }

            double angle = 360.0 * percent / 100.0;
            double radians = (angle - 90) * Math.PI / 180.0;
            var end = new Point(
                Center.X + Radius * Math.Cos(radians),
                Center.Y + Radius * Math.Sin(radians));

            var figure = new PathFigure
            {
                StartPoint = new Point(Center.X, Center.Y - Radius),
                IsClosed = false,
                IsFilled = false
            };
            figure.Segments.Add(new ArcSegment(
                end,
                new Size(Radius, Radius),
                0,
                angle > 180,
                SweepDirection.Clockwise,
                true));

            return new PathGeometry(new[] { figure });
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>折叠时隐藏右分隔条。</summary>
    public sealed class InverseBooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool flag = value is bool && (bool)value;
            return flag ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>true 显示横向滚动条，false 关闭（文件树只有超过 8 层才需要横向滚动）。</summary>
    public sealed class BooleanToScrollBarVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool visible = value is bool && (bool)value;
            return visible ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>空字符串显示元素（查找框占位），非空隐藏。</summary>
    public sealed class EmptyStringToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string text = value as string;
            return string.IsNullOrEmpty(text) ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>非空字符串显示元素；用于状态、模型、用量这类可有可无的元信息。</summary>
    public sealed class NonEmptyStringToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string text = value as string;
            return string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>
    /// 工具名 → 16 网格描边图标几何（DSH 的工具族词表，见 docs/06 §9.5）。
    /// 先按工具族归类，认不出的回落到 sparkle，不给每个工具单独配图标。
    /// </summary>
    public sealed class ToolIconConverter : IValueConverter
    {
        private static readonly Dictionary<string, string> Icons =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "read", "Ico.Browse" },
                { "grep", "Ico.SearchStroke" },
                { "glob", "Ico.SearchStroke" },
                { "edit", "Ico.EditStroke" },
                { "write", "Ico.EditStroke" },
                { "pwsh", "Ico.Api" },
                { "skill", "Ico.Sparkle" },
                { "web_fetch", "Ico.Browse" },
                { "working_directory", "Ico.Browse" },
                { "job_list", "Ico.Clock" },
                { "job_output", "Ico.Clock" },
                { "job_kill", "Ico.Clock" },
                { "present", "Ico.Sparkle" },
                { "create_goal", "Ico.Sparkle" },
                { "get_goal", "Ico.Sparkle" },
                { "update_goal", "Ico.Sparkle" }
            };

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string name = value as string ?? string.Empty;
            string key;
            if (!Icons.TryGetValue(name, out key))
            {
                key = "Ico.Sparkle";
            }

            Application app = Application.Current;
            return app == null ? null : app.TryFindResource(key);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>
    /// 资源键 → 图标几何。输入框触发菜单用它把 ViewModel 的图标键（Ico.File / Ico.Folder …）
    /// 换成一个 16 网格几何，避免在 DataTemplate 里写多份 Path。
    /// </summary>
    public sealed class ResourceKeyToGeometryConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string key = value as string;
            if (string.IsNullOrEmpty(key))
            {
                return null;
            }

            Application app = Application.Current;
            return app == null ? null : app.TryFindResource(key);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
