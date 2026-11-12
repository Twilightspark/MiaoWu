using System;
using System.Collections.Generic;
using System.IO;
using MiaoWu.Core.Abstractions;
using MiaoWu.Core.Workspaces;

namespace MiaoWu.Infrastructure.Workspaces
{
    /// <summary>
    /// 文件树目录读取：只枚举一层，目录优先排序；
    /// 越权、IO 错误、失效路径都变成一行“无法读取”，不向 UI 抛异常。
    /// </summary>
    public sealed class FileSystemCatalog : IFileCatalog
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
