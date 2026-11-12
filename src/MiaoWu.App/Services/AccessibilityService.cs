using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace MiaoWu.App.Services
{
    /// <summary>
    /// 系统高对比模式：把品牌色与灰阶替换为 SystemColors，保证边框、选中态与文字可见。
    /// 关闭高对比时恢复原始画刷引用（全部视图使用 DynamicResource）。
    /// </summary>
    public sealed class AccessibilityService : IDisposable
    {
        private static readonly string[] Keys =
        {
            "Brush.Canvas",
            "Brush.Side",
            "Brush.Line",
            "Brush.Line2",
            "Brush.Line3",
            "Brush.Text",
            "Brush.Text2",
            "Brush.Text3",
            "Brush.Sel",
            "Brush.Sel2",
            "Brush.Hover",
            "Brush.Accent",
            "Brush.AccentSoft",
            "Brush.Warning",
            "Brush.WarningSoft",
            "Brush.Danger",
            "Brush.DangerSoft",
            "Brush.Success"
        };

        private readonly Dictionary<string, object> _original =
            new Dictionary<string, object>(StringComparer.Ordinal);

        private bool _applied;
        private bool _disposed;

        public AccessibilityService()
        {
            if (Application.Current != null)
            {
                foreach (string key in Keys)
                {
                    if (Application.Current.Resources.Contains(key))
                    {
                        _original[key] = Application.Current.Resources[key];
                    }
                }
            }

            SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        }

        public bool IsHighContrast
        {
            get { return SystemParameters.HighContrast; }
        }

        public void Apply()
        {
            if (Application.Current == null)
            {
                return;
            }

            if (!SystemParameters.HighContrast)
            {
                Restore();
                return;
            }

            try
            {
                Set("Brush.Canvas", SystemColors.WindowBrush);
                Set("Brush.Side", SystemColors.WindowBrush);
                Set("Brush.Line", SystemColors.WindowTextBrush);
                Set("Brush.Line2", SystemColors.WindowTextBrush);
                Set("Brush.Line3", SystemColors.WindowTextBrush);
                Set("Brush.Text", SystemColors.WindowTextBrush);
                Set("Brush.Text2", SystemColors.WindowTextBrush);
                Set("Brush.Text3", SystemColors.WindowTextBrush);
                Set("Brush.Sel", SystemColors.HighlightBrush);
                Set("Brush.Sel2", SystemColors.HighlightBrush);
                Set("Brush.Hover", SystemColors.HighlightBrush);
                Set("Brush.Accent", SystemColors.HighlightBrush);
                Set("Brush.AccentSoft", SystemColors.WindowBrush);
                Set("Brush.Warning", SystemColors.WindowTextBrush);
                Set("Brush.WarningSoft", SystemColors.WindowBrush);
                Set("Brush.Danger", SystemColors.WindowTextBrush);
                Set("Brush.DangerSoft", SystemColors.WindowBrush);
                Set("Brush.Success", SystemColors.WindowTextBrush);
                _applied = true;
            }
            catch (Exception)
            {
            }
        }

        public void Restore()
        {
            if (!_applied || Application.Current == null)
            {
                return;
            }

            try
            {
                foreach (KeyValuePair<string, object> pair in _original)
                {
                    Application.Current.Resources[pair.Key] = pair.Value;
                }
            }
            catch (Exception)
            {
            }

            _applied = false;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
            Restore();
        }

        private void OnSystemParametersChanged(object sender, PropertyChangedEventArgs e)
        {
            if (string.Equals(e.PropertyName, "HighContrast", StringComparison.Ordinal))
            {
                Apply();
            }
        }

        private static void Set(string key, Brush brush)
        {
            if (Application.Current != null)
            {
                Application.Current.Resources[key] = brush;
            }
        }
    }
}
