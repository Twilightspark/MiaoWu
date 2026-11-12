using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace MiaoWu.App.Services
{
    /// <summary>
    /// 托盘图标与菜单。关闭窗口后智能体继续在后台运行，托盘是唯一的可见入口。
    /// Win7 下必须 Visible=false + Dispose，否则会留下幽灵图标。
    /// </summary>
    public sealed class TrayService : IDisposable
    {
        private readonly NotifyIcon _icon;
        private readonly ToolStripMenuItem _showItem;
        private readonly ToolStripMenuItem _newSessionItem;
        private bool _disposed;

        public TrayService(string tooltip)
        {
            Icon icon = LoadIcon();
            string text = string.IsNullOrWhiteSpace(tooltip) ? "MiaoWu" : tooltip;
            if (text.Length > 63)
            {
                text = text.Substring(0, 63);
            }

            var menu = new ContextMenuStrip();
            _showItem = new ToolStripMenuItem("打开主窗口");
            _showItem.Click += (sender, args) => Raise(OpenRequested);
            menu.Items.Add(_showItem);

            _newSessionItem = new ToolStripMenuItem("新建会话");
            _newSessionItem.Click += (sender, args) => Raise(NewSessionRequested);
            menu.Items.Add(_newSessionItem);

            menu.Items.Add(new ToolStripSeparator());

            var updateItem = new ToolStripMenuItem("检查更新");
            updateItem.Click += (sender, args) => Raise(CheckUpdateRequested);
            menu.Items.Add(updateItem);

            menu.Items.Add(new ToolStripSeparator());

            var exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += (sender, args) => Raise(ExitRequested);
            menu.Items.Add(exitItem);

            _icon = new NotifyIcon
            {
                Icon = icon,
                Text = text,
                Visible = true,
                ContextMenuStrip = menu
            };
            _icon.DoubleClick += (sender, args) => Raise(OpenRequested);
        }

        public event EventHandler OpenRequested;

        public event EventHandler NewSessionRequested;

        public event EventHandler CheckUpdateRequested;

        public event EventHandler ExitRequested;

        public void ShowBalloon(string title, string text)
        {
            try
            {
                if (_icon != null)
                {
                    _icon.ShowBalloonTip(2500, title, text, ToolTipIcon.Info);
                }
            }
            catch (Exception)
            {
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                if (_icon != null)
                {
                    _icon.Visible = false;
                    _icon.Dispose();
                }
            }
            catch (Exception)
            {
            }
        }

        private void Raise(EventHandler handler)
        {
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private static Icon LoadIcon()
        {
            try
            {
                string path = Process.GetCurrentProcess().MainModule.FileName;
                Icon icon = Icon.ExtractAssociatedIcon(path);
                if (icon != null)
                {
                    return icon;
                }
            }
            catch (Exception)
            {
            }

            return SystemIcons.Application;
        }
    }
}
