using System;
using System.IO;
using System.Text;

namespace MiaoWu.Infrastructure.Storage
{
    /// <summary>
    /// 原子写：同目录临时文件 → File.Replace（保留备份）或 File.Move。
    /// 写入后立即 Flush(true)，避免异常退出留下半行 JSON。
    /// </summary>
    internal static class AtomicFile
    {
        public static void WriteAllText(string path, string content)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("文件路径不能为空。", nameof(path));
            }

            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None))
                {
                    var bytes = new UTF8Encoding(false).GetBytes(content ?? string.Empty);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }

                if (File.Exists(path))
                {
                    string backupPath = path + ".bak";
                    try
                    {
                        File.Replace(tempPath, path, backupPath, true);
                    }
                    catch (IOException)
                    {
                        // 某些文件系统（或目标被占用）不支持 Replace，退回删除后移动。
                        File.Delete(path);
                        File.Move(tempPath, path);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        File.Delete(path);
                        File.Move(tempPath, path);
                    }
                }
                else
                {
                    File.Move(tempPath, path);
                }
            }
            finally
            {
                TryDelete(tempPath);
            }
        }

        public static string ReadAllText(string path)
        {
            return File.ReadAllText(path);
        }

        public static void WriteAllBytes(string path, byte[] content)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("文件路径不能为空。", nameof(path));
            }

            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None))
                {
                    stream.Write(content ?? new byte[0], 0, content == null ? 0 : content.Length);
                    stream.Flush(true);
                }

                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(tempPath, path, path + ".bak", true);
                    }
                    catch (IOException)
                    {
                        File.Delete(path);
                        File.Move(tempPath, path);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        File.Delete(path);
                        File.Move(tempPath, path);
                    }
                }
                else
                {
                    File.Move(tempPath, path);
                }
            }
            finally
            {
                TryDelete(tempPath);
            }
        }

        public static void BackupCorrupt(string path, string suffix)
        {
            if (!File.Exists(path))
            {
                return;
            }

            try
            {
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
                string target = path + "." + suffix + "-" + stamp;
                File.Copy(path, target, false);
                File.Delete(path);
            }
            catch (Exception)
            {
                // 备份失败不阻塞启动：下次保存会覆盖。
            }
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
                // 临时文件清理失败可忽略，文件仍在 .tmp-* 名下。
            }
        }
    }
}
