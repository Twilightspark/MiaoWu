using System;
using System.IO;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;

namespace MiaoWu.Infrastructure.Storage
{
    /// <summary>
    /// 目录可写性检查与权限修复。
    /// <para>
    /// MiaoWu 的数据目录（<c>用户目录\.miaowu</c>、<c>&lt;工作区&gt;\.miaowu</c>）都是“当前用户私有”，
    /// 正常运行不需要管理员权限。但目录可能由别的账户或提权进程创建、也可能被放在只读位置，
    /// 因此每次创建目录后都做一次写探针：探针失败时先尝试给当前用户补一条继承的完全控制 ACE，
    /// 仍然失败就抛出带指引的异常，避免日志 / 会话在后续写入时以更难懂的方式失败。
    /// </para>
    /// </summary>
    public static class FolderAccess
    {
        private const string ProbeFilePrefix = ".miaowu-write-test-";

        /// <summary>目录是否存在且当前账户可以创建 / 删除文件。</summary>
        public static bool IsWritable(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return false;
            }

            string probe = Path.Combine(
                directory,
                ProbeFilePrefix + Guid.NewGuid().ToString("N") + ".tmp");

            try
            {
                using (var stream = new FileStream(
                    probe,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    1,
                    FileOptions.None))
                {
                    stream.WriteByte(0);
                }

                // 建得出、删得掉才算可写：原子替换依赖删除 / 改名。
                File.Delete(probe);
                return true;
            }
            catch (Exception ex) when (IsAccessDenied(ex))
            {
                return false;
            }
            finally
            {
                TryDelete(probe);
            }
        }

        /// <summary>
        /// 确保目录存在并且当前账户可写。权限不足时先尝试修复 ACL，再失败则抛
        /// <see cref="UnauthorizedAccessException"/>，消息里带上可操作的处置建议。
        /// </summary>
        public static void EnsureWritable(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new ArgumentException("目录不能为空。", nameof(directory));
            }

            Directory.CreateDirectory(directory);
            if (IsWritable(directory))
            {
                return;
            }

            if (TryGrantCurrentUserFullControl(directory) && IsWritable(directory))
            {
                return;
            }

            throw new UnauthorizedAccessException(BuildMessage(directory));
        }

        /// <summary>
        /// 给目录补上 Hidden 属性。工作区数据目录默认不出现在资源管理器与工作区文件视图中；
        /// 属性设置失败（例如只读盘）不阻断会话，目录仍然可用。
        /// </summary>
        public static void EnsureHidden(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return;
            }

            try
            {
                FileAttributes attributes = File.GetAttributes(directory);
                if ((attributes & FileAttributes.Hidden) == 0)
                {
                    File.SetAttributes(directory, attributes | FileAttributes.Hidden);
                }
            }
            catch (Exception)
            {
                // 属性只是外观约定；不因为无法设置而影响数据读写。
            }
        }

        /// <summary>
        /// 给当前账户补一条可继承的完全控制 ACE。目录属主不是当前账户、或进程本身已被降权时
        /// 无法修改 ACL，此时返回 <c>false</c>，由调用方给出提示。
        /// </summary>
        public static bool TryGrantCurrentUserFullControl(string directory)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    return false;
                }

                WindowsIdentity identity = WindowsIdentity.GetCurrent();
                IdentityReference user = identity == null ? null : identity.User;
                if (user == null)
                {
                    return false;
                }

                var info = new DirectoryInfo(directory);
                DirectorySecurity security = info.GetAccessControl();
                security.AddAccessRule(new FileSystemAccessRule(
                    user,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
                info.SetAccessControl(security);
                return true;
            }
            catch (Exception ex) when (IsRecoverable(ex))
            {
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string BuildMessage(string directory)
        {
            return "当前账户无法写入目录：" + directory + "。"
                + "MiaoWu 的配置、日志、会话与缓存都写在这里，请确认该目录存在且当前用户有修改权限"
                + "（资源管理器 → 右键属性 → 安全）；如果目录是由管理员账户创建的，请用创建它的账户运行，"
                + "或删除该目录让 MiaoWu 重新创建。";
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
            }
        }

        private static bool IsAccessDenied(Exception ex)
        {
            return ex is UnauthorizedAccessException || ex is SecurityException;
        }

        private static bool IsRecoverable(Exception ex)
        {
            return ex is UnauthorizedAccessException
                || ex is SecurityException
                || ex is IOException
                || ex is ArgumentException
                || ex is InvalidOperationException
                || ex is PlatformNotSupportedException
                || ex is NotSupportedException;
        }
    }
}
