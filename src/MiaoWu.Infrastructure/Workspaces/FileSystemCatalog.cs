using System;
using System.Collections.Generic;
using System.IO;
using MiaoWu.Core.Abstractions;
using MiaoWu.Core.Conversations;
using MiaoWu.Core.Workspaces;

namespace MiaoWu.Infrastructure.Workspaces
{
    /// <summary>
    /// 文件树目录读取：只枚举一层，目录优先排序；
    /// 越权、IO 错误、失效路径都变成一行“无法读取”，不向 UI 抛异常。
    /// </summary>
    public sealed class FileSystemCatalog : IFileCatalog, IFileEditor
    {
        public IReadOnlyList<FileNode> GetChildren(string directoryPath, int depth)
        {
            var nodes = new List<FileNode>();
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                nodes.Add(CreateErrorNode(directoryPath ?? string.Empty, depth, "路径为空"));
                return nodes;
            }

            try
            {
                var directory = new DirectoryInfo(directoryPath);
                if (!directory.Exists)
                {
                    nodes.Add(CreateErrorNode(directoryPath, depth, "文件夹不存在或已被移动"));
                    return nodes;
                }

                foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos())
                {
                    FileAttributes attributes;
                    try
                    {
                        attributes = entry.Attributes;
                    }
                    catch (Exception)
                    {
                        // 单个条目元数据不可读时跳过，不影响其它子项。
                        continue;
                    }

                    bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                    bool isReparsePoint = (attributes & FileAttributes.ReparsePoint) != 0;

                    // 工作区数据目录（.miaowu，兼容旧版 .agent）属于应用内部状态，不在文件视图出现。
                    if (isDirectory && AgentWorkspaceLayout.IsDataDirectoryName(entry.Name))
                    {
                        continue;
                    }

                    nodes.Add(new FileNode(entry.Name, entry.FullName, isDirectory, depth)
                    {
                        IsReparsePoint = isReparsePoint,
                        State = isDirectory ? FileNodeState.NotLoaded : FileNodeState.Loaded
                    });
                }

                FileTreePolicy.SortInPlace(nodes);
                return nodes;
            }
            catch (UnauthorizedAccessException)
            {
                return new[] { CreateErrorNode(directoryPath, depth, "没有权限读取此文件夹") };
            }
            catch (IOException ex)
            {
                return new[] { CreateErrorNode(directoryPath, depth, "读取失败：" + ex.Message) };
            }
            catch (Exception ex)
            {
                return new[] { CreateErrorNode(directoryPath, depth, "无法读取：" + ex.Message) };
            }
        }

        /// <summary>同目录内重命名文件；目标已存在、源不是文件或名称非法时抛出说明性异常。</summary>
        public string Rename(string filePath, string newName)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("文件路径不能为空。", nameof(filePath));
            }

            string source = Path.GetFullPath(filePath);
            if (Directory.Exists(source))
            {
                throw new InvalidOperationException("目录重命名尚未支持。");
            }

            if (!File.Exists(source))
            {
                throw new FileNotFoundException("文件不存在或已被移动。", filePath);
            }

            string target;
            string error;
            if (!FileEditPolicy.TryBuildTargetPath(source, newName, out target, out error))
            {
                throw new InvalidOperationException(error);
            }

            if (File.Exists(target) || Directory.Exists(target))
            {
                throw new IOException("同名文件或文件夹已存在。");
            }

            File.Move(source, target);
            return target;
        }

        /// <summary>删除文件到回收站，便于误删后恢复。</summary>
        public void Delete(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("文件路径不能为空。", nameof(filePath));
            }

            string target = Path.GetFullPath(filePath);
            if (Directory.Exists(target))
            {
                throw new InvalidOperationException("目录删除尚未支持。");
            }

            if (!File.Exists(target))
            {
                throw new FileNotFoundException("文件不存在或已被移动。", filePath);
            }

            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                target,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }

        private static FileNode CreateErrorNode(string directoryPath, int depth, string message)
        {
            return new FileNode("无法读取：" + message, directoryPath, false, depth)
            {
                State = FileNodeState.Error,
                ErrorMessage = message
            };
        }
    }
}
