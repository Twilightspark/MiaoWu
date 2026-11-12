using System;
using System.IO;
using MiaoWu.Core.Agents.Tools;

namespace MiaoWu.Infrastructure.Tools
{
    /// <summary>
    /// 真实文件系统的属性探测。不存在与无权限都返回 false；后续读写会给出更具体的错误，
    /// 沙箱只负责“不要越界”，不负责诊断权限。
    /// </summary>
    public sealed class FileSystemPathProbe : IPathProbe
    {
        public static readonly FileSystemPathProbe Instance = new FileSystemPathProbe();

        public bool TryGetAttributes(string path, out FileAttributes attributes)
        {
            attributes = (FileAttributes)0;
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                attributes = File.GetAttributes(path);
                return true;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
            catch (NotSupportedException)
            {
                return false;
            }
        }
    }
}
