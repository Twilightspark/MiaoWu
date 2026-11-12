using System.IO;
using System.Windows.Forms;

namespace MiaoWu.App.Services
{
    /// <summary>选择文件夹。抽成接口便于离屏快照与测试替换。</summary>
    public interface IFolderPicker
    {
        string PickFolder(string description, string initialPath);
    }

    /// <summary>系统原生文件夹选择器（Win7 可用），不使用第三方对话框。</summary>
    public sealed class WindowsFolderPicker : IFolderPicker
    {
        public string PickFolder(string description, string initialPath)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = description ?? string.Empty;
                dialog.ShowNewFolderButton = false;

                if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath))
                {
                    dialog.SelectedPath = initialPath;
                }

                return dialog.ShowDialog() == DialogResult.OK
                    ? dialog.SelectedPath
                    : null;
            }
        }
    }

    /// <summary>选择文件（标签行右端 ＋ 与空态主按钮）。</summary>
    public interface IFilePicker
    {
        string PickFile(string title, string initialDirectory, string filter);
    }

    public sealed class WindowsFilePicker : IFilePicker
    {
        public string PickFile(string title, string initialDirectory, string filter)
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = title ?? string.Empty;
                dialog.CheckFileExists = true;
                dialog.Multiselect = false;
                dialog.Filter = string.IsNullOrWhiteSpace(filter) ? "所有文件|*.*" : filter;

                if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
                {
                    dialog.InitialDirectory = initialDirectory;
                }

                return dialog.ShowDialog() == DialogResult.OK
                    ? dialog.FileName
                    : null;
            }
        }
    }

    /// <summary>剪贴板写入；快照环境用空实现。</summary>
    public interface IClipboardService
    {
        bool SetText(string text);
    }

    public sealed class SystemClipboardService : IClipboardService
    {
        public bool SetText(string text)
        {
            try
            {
                Clipboard.SetText(text ?? string.Empty);
                return true;
            }
            catch (System.Exception)
            {
                return false;
            }
        }
    }
}
