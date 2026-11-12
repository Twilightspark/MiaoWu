using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MiaoWu.Infrastructure.Tools
{
    /// <summary>
    /// 写文件前的备份与恢复。备份放在全局 <c>backups\</c> 目录，
    /// 文件名带时间戳、原路径短哈希与原名，避免不同目录下同名文件互相覆盖。
    /// </summary>
    public sealed class FileBackupService
    {
        private readonly string _directory;

        public FileBackupService(string backupsDirectory)
        {
            if (string.IsNullOrWhiteSpace(backupsDirectory))
            {
                throw new ArgumentException("备份目录不能为空。", nameof(backupsDirectory));
            }

            _directory = Path.GetFullPath(backupsDirectory);
        }

        public string BackupsDirectory
        {
            get { return _directory; }
        }

        /// <summary>把源文件复制到备份目录，返回备份路径。失败时抛 IOException。</summary>
        public string Backup(string sourcePath, DateTimeOffset timestamp)
        {
            if (!File.Exists(sourcePath))
            {
                throw new IOException("要备份的文件不存在。");
            }

            Directory.CreateDirectory(_directory);

            string fileName = Path.GetFileName(sourcePath);
            string hash = ToolHashing.ShortPathHash(sourcePath);
            string stamp = timestamp.ToLocalTime().ToString("yyyyMMdd-HHmmss-fff");
            string candidate = Path.Combine(_directory, stamp + "-" + hash + "-" + fileName);

            int suffix = 1;
            while (File.Exists(candidate))
            {
                candidate = Path.Combine(
                    _directory,
                    stamp + "-" + hash + "-" + fileName + "-" + suffix.ToString());
                suffix++;
            }

            File.Copy(sourcePath, candidate, false);
            return candidate;
        }

        /// <summary>用备份覆盖目标文件，写完做哈希校验。</summary>
        public void Restore(string backupPath, string targetPath)
        {
            if (!File.Exists(backupPath))
            {
                throw new IOException("备份文件不存在。");
            }

            byte[] bytes = File.ReadAllBytes(backupPath);
            string directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            ToolAtomicWrite.WriteAllBytes(targetPath, bytes);

            string expected = ToolHashing.Sha256Hex(bytes);
            string actual = ToolHashing.Sha256Hex(File.ReadAllBytes(targetPath));
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("恢复后哈希校验失败。");
            }
        }

        /// <summary>查找某个目标文件最近一次的备份；没有时返回 null。</summary>
        public string FindLatestBackup(string targetPath)
        {
            if (!Directory.Exists(_directory))
            {
                return null;
            }

            string fileName = Path.GetFileName(targetPath);
            string hash = ToolHashing.ShortPathHash(targetPath);
            string suffix = "-" + hash + "-" + fileName;

            string latest = null;
            try
            {
                foreach (string path in Directory.EnumerateFiles(_directory, "*" + suffix))
                {
                    string name = Path.GetFileName(path);
                    if (latest == null
                        || string.Compare(name, Path.GetFileName(latest), StringComparison.Ordinal) > 0)
                    {
                        latest = path;
                    }
                }
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }

            return latest;
        }
    }

    /// <summary>SHA-256 与短哈希工具；写文件与备份共用。</summary>
    public static class ToolHashing
    {
        public static string Sha256Hex(byte[] bytes)
        {
            if (bytes == null)
            {
                return string.Empty;
            }

            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                var builder = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                {
                    builder.Append(hash[i].ToString("x2"));
                }

                return builder.ToString();
            }
        }

        /// <summary>文件内容的 SHA-256；文件不存在返回空串。</summary>
        public static string Sha256FileHex(string path)
        {
            if (!File.Exists(path))
            {
                return string.Empty;
            }

            return Sha256Hex(File.ReadAllBytes(path));
        }

        /// <summary>路径的短哈希（8 位），用于备份文件名，不用于安全判断。</summary>
        public static string ShortPathHash(string path)
        {
            string normalized = (path ?? string.Empty).Replace('/', '\\').ToLowerInvariant();
            byte[] bytes = Encoding.UTF8.GetBytes(normalized);
            string hex = Sha256Hex(bytes);
            return hex.Length >= 8 ? hex.Substring(0, 8) : hex;
        }
    }

    /// <summary>
    /// 同目录临时文件 + File.Replace / File.Move 的原子写；调用方负责校验内容。
    /// 目标不存在时用 Move，存在时用 Replace（不保留系统备份，备份由调用方先行完成）。
    /// </summary>
    internal static class ToolAtomicWrite
    {
        public static void WriteAllBytes(string path, byte[] bytes)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("目标路径不能为空。", nameof(path));
            }

            string directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory))
            {
                throw new IOException("目标路径没有父目录。");
            }

            string temp = Path.Combine(
                directory,
                "." + Path.GetFileName(path) + ".miaowu-" + Guid.NewGuid().ToString("N") + ".tmp");

            try
            {
                using (var stream = new FileStream(
                    temp,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None))
                {
                    stream.Write(bytes ?? new byte[0], 0, bytes == null ? 0 : bytes.Length);
                    stream.Flush(true);
                }

                if (File.Exists(path))
                {
                    File.Replace(temp, path, null, true);
                }
                else
                {
                    File.Move(temp, path);
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(temp))
                    {
                        File.Delete(temp);
                    }
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
