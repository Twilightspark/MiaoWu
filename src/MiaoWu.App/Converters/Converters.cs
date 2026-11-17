using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

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
                { "list_dir", "Ico.Browse" },
                { "read_file", "Ico.Browse" },
                { "search_files", "Ico.SearchStroke" },
                { "find_files", "Ico.SearchStroke" },
                { "edit_file", "Ico.EditStroke" },
                { "write_file", "Ico.EditStroke" },
                { "run_command", "Ico.Api" },
                { "load_skill", "Ico.Sparkle" }
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
}
