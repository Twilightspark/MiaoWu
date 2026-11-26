using System;
using System.Diagnostics;
using System.IO;
using MiaoWu.Core.Updates;

namespace MiaoWu.Infrastructure.Updates
{
    /// <summary>
    /// 用 Inno Setup 安装包更新：只接受已下载到本地的 exe，启动时加
    /// <c>/SILENT /CLOSEAPPLICATIONS /NORESTART</c>。安装程序带 requireAdministrator 清单，
    /// 由 Windows 弹出 UAC；本类不自行提权，也不改写安装目录。
    /// </summary>
    public sealed class SetupUpdateInstaller : IUpdateInstaller
    {
        private const string Arguments = "/SILENT /CLOSEAPPLICATIONS /NORESTART";

        public bool Install(string installerPath, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(installerPath))
            {
                error = "安装包路径为空。";
                return false;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(installerPath);
            }
            catch (Exception exception)
            {
                error = "安装包路径无效：" + exception.Message;
                return false;
            }

            if (!File.Exists(fullPath))
            {
                error = "安装包不存在，可能已被删除；请重新检查更新。";
                return false;
            }

            if (!string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                error = "更新包不是可执行安装程序。";
                return false;
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = fullPath,
                    Arguments = Arguments,
                    WorkingDirectory = Path.GetDirectoryName(fullPath) ?? string.Empty,
                    UseShellExecute = true
                };

                using (Process process = Process.Start(startInfo))
                {
                    // 只负责拉起安装程序；安装完成后由安装器按自身配置关闭 / 重启应用。
                }

                return true;
            }
            catch (Exception exception)
            {
                error = "无法启动安装程序：" + exception.Message;
                return false;
            }
        }
    }
}
