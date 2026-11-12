using System;
using System.Collections.Generic;
using System.IO;

namespace MiaoWu.Core.Agents.Tools
{
    /// <summary>沙箱允许访问的对象类型。</summary>
    public enum PathAccessKind
    {
        Any = 0,
        File = 1,
        Directory = 2
    }

    /// <summary>路径校验的结论。除 <see cref="Allowed"/> 外都不会产生文件 IO。</summary>
    public enum PathSandboxStatus
    {
        Allowed = 0,

        /// <summary>空串或纯空白。</summary>
        Empty = 1,

        /// <summary>UNC、设备路径、盘符相对路径或根相对路径。</summary>
        UncOrDevice = 2,

        /// <summary>非法字符、设备名、非法格式。</summary>
        InvalidPath = 3,

        /// <summary>规范化后不在工作区根之下。</summary>
        RootEscape = 4,

        /// <summary>超过 MAX_PATH。</summary>
        TooLong = 5,

        /// <summary>路径上存在重解析点（junction / symlink），可能逃出工作区。</summary>
        ReparsePoint = 6,

        /// <summary>目标不存在。</summary>
        NotFound = 7,

        /// <summary>期望文件但目标不是文件，或反之。</summary>
        WrongKind = 8
    }

    /// <summary>只读的属性探测接缝：真实实现走文件系统，测试可以注入假数据。</summary>
    public interface IPathProbe
    {
        /// <summary>取路径属性；不存在或无权限时返回 false。</summary>
        bool TryGetAttributes(string path, out FileAttributes attributes);
    }

    /// <summary>一次路径校验的结果，带规范化后的绝对路径与拒绝原因。</summary>
    public sealed class PathSandboxResult
    {
        internal PathSandboxResult(
            PathSandboxStatus status,
            string normalizedPath,
            string relativePath,
            string detail)
        {
            Status = status;
            NormalizedPath = normalizedPath;
            RelativePath = relativePath;
            Detail = detail;
        }

        public PathSandboxStatus Status { get; }

        public bool IsAllowed
        {
            get { return Status == PathSandboxStatus.Allowed; }
        }

        /// <summary>允许时为规范化绝对路径；拒绝时可能为空。</summary>
        public string NormalizedPath { get; }

        /// <summary>相对工作区根的路径（使用反斜杠）；根目录本身为空串。</summary>
        public string RelativePath { get; }

        /// <summary>给用户与模型看的中文原因，例如“路径不在工作区内”。</summary>
        public string Detail { get; }

        public override string ToString()
        {
            return IsAllowed
                ? (RelativePath.Length == 0 ? "." : RelativePath)
                : Detail;
        }
    }

    /// <summary>
    /// 工作区路径沙箱。所有工具访问磁盘前都必须先经过它，任何失败都返回结构化拒绝，
    /// 不抛异常。
    /// </summary>
    /// <remarks>
    /// <para>校验顺序：空白 → 设备/UNC/根相对 → 非法字符与设备名 → 规范化 →
    /// 根目录包含 → 长度 → 逐段重解析点 → 存在性与类型。</para>
    /// <para>比较使用 <see cref="StringComparison.OrdinalIgnoreCase"/>；Windows 路径不区分大小写。</para>
    /// <para>重解析点检查在打开文件前完成，但与随后的打开之间仍有 TOCTOU 窗口；
    /// 本地单用户桌面场景接受该窗口，不为此引入句柄级校验。</para>
    /// </remarks>
    public sealed class WorkspacePathSandbox
    {
        private const int MaxPathLength = 259;
        private static readonly char[] Separators = { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        private static readonly char[] InvalidNameChars = { '"', '<', '>', '|', '*', '?', ':' };
        private static readonly string[] ReservedDeviceNames =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        private readonly IPathProbe _probe;

        public WorkspacePathSandbox(string workspaceRoot, IPathProbe probe)
        {
            if (string.IsNullOrWhiteSpace(workspaceRoot))
            {
                throw new ArgumentException("工作区根目录不能为空。", nameof(workspaceRoot));
            }

            string fullRoot;
            try
            {
                fullRoot = Path.GetFullPath(workspaceRoot);
            }
            catch (Exception exception) when (
                exception is ArgumentException
                || exception is NotSupportedException
                || exception is PathTooLongException)
            {
                throw new ArgumentException("工作区根目录不是合法路径。", nameof(workspaceRoot), exception);
            }

            RootPath = TrimTrailingSeparators(fullRoot);
            _probe = probe;
        }

        /// <summary>规范化后的工作区根（不带末尾分隔符，盘符根除外）。</summary>
        public string RootPath { get; }

        /// <summary>校验一个由模型或 UI 给出的路径。</summary>
        /// <param name="candidate">相对工作区根的路径，或工作区内的绝对路径。</param>
        /// <param name="kind">期望的文件类型。</param>
        /// <param name="allowMissing">
        /// 目标可以不存在（写文件的新建场景）。为 false 时不存在会返回 <see cref="PathSandboxStatus.NotFound"/>。
        /// </param>
        public PathSandboxResult Resolve(
            string candidate,
            PathAccessKind kind,
            bool allowMissing)
        {
            if (candidate == null || candidate.Trim().Length == 0)
            {
                return Reject(PathSandboxStatus.Empty, "路径为空。");
            }

            if (candidate.IndexOf('\0') >= 0)
            {
                return Reject(PathSandboxStatus.InvalidPath, "路径包含空字符。");
            }

            if (HasUncOrDevicePrefix(candidate))
            {
                return Reject(
                    PathSandboxStatus.UncOrDevice,
                    "不接受 UNC 或设备路径，请使用工作区内的路径。");
            }

            for (int i = 0; i < candidate.Length; i++)
            {
                char current = candidate[i];
                if (current < ' ')
                {
                    return Reject(PathSandboxStatus.InvalidPath, "路径包含控制字符。");
                }
            }

            // 盘符相对路径（C:foo）会随当前目录漂移，直接拒绝。
            if (candidate.Length >= 2
                && char.IsLetter(candidate[0])
                && candidate[1] == ':'
                && (candidate.Length == 2 || (candidate[2] != '\\' && candidate[2] != '/')))
            {
                return Reject(PathSandboxStatus.UncOrDevice, "不接受盘符相对路径。");
            }

            // 以单个分隔符开头：没有盘符也没有 UNC，GetFullPath 会解析到当前盘根。
            if ((candidate[0] == '\\' || candidate[0] == '/')
                && !(candidate.Length >= 2 && (candidate[1] == '\\' || candidate[1] == '/')))
            {
                return Reject(
                    PathSandboxStatus.UncOrDevice,
                    "不接受根相对路径，请使用相对工作区根的路径。");
            }

            if (ContainsInvalidNameChars(candidate))
            {
                return Reject(
                    PathSandboxStatus.InvalidPath,
                    "路径包含非法字符（\" < > | * ? :）。");
            }

            if (ContainsReservedDeviceName(candidate))
            {
                return Reject(PathSandboxStatus.InvalidPath, "路径包含 Windows 设备名。");
            }

            string combined;
            try
            {
                combined = Path.IsPathRooted(candidate)
                    ? candidate
                    : Path.Combine(RootPath, candidate);
            }
            catch (ArgumentException)
            {
                return Reject(PathSandboxStatus.InvalidPath, "路径格式不合法。");
            }

            string full;
            try
            {
                full = Path.GetFullPath(combined);
            }
            catch (PathTooLongException)
            {
                return Reject(PathSandboxStatus.TooLong, "路径超过系统长度上限。");
            }
            catch (Exception exception) when (
                exception is ArgumentException
                || exception is NotSupportedException)
            {
                return Reject(PathSandboxStatus.InvalidPath, "路径格式不合法。");
            }

            full = TrimTrailingSeparators(full);

            bool isRoot;
            if (!TryGetRelative(full, out isRoot, out string relative))
            {
                return Reject(
                    PathSandboxStatus.RootEscape,
                    "路径不在当前工作区内：" + CandidateForDisplay(candidate));
            }

            if (full.Length > MaxPathLength)
            {
                return Reject(PathSandboxStatus.TooLong, "路径超过系统长度上限。");
            }

            string blockedSegment;
            if (FindReparsePoint(full, out blockedSegment))
            {
                return Reject(
                    PathSandboxStatus.ReparsePoint,
                    "路径经过重解析点，可能指向工作区外：" + blockedSegment);
            }

            FileAttributes attributes = (FileAttributes)0;
            bool exists = _probe != null && _probe.TryGetAttributes(full, out attributes);

            // 工作区根本身不要求探测存在性：打开工作区时已经确认过，根也可能是 junction。
            if (isRoot)
            {
                if (kind == PathAccessKind.File)
                {
                    return Reject(PathSandboxStatus.WrongKind, "目标是目录，不是文件。");
                }

                return new PathSandboxResult(
                    PathSandboxStatus.Allowed,
                    full,
                    string.Empty,
                    string.Empty);
            }

            if (exists)
            {
                bool isDirectory = (attributes & FileAttributes.Directory) == FileAttributes.Directory;
                if (kind == PathAccessKind.File && isDirectory)
                {
                    return Reject(PathSandboxStatus.WrongKind, "目标是目录，不是文件。");
                }

                if (kind == PathAccessKind.Directory && !isDirectory)
                {
                    return Reject(PathSandboxStatus.WrongKind, "目标是文件，不是目录。");
                }
            }
            else if (!allowMissing)
            {
                return Reject(PathSandboxStatus.NotFound, "路径不存在：" + CandidateForDisplay(candidate));
            }
            else if (kind == PathAccessKind.Directory)
            {
                return Reject(PathSandboxStatus.NotFound, "目录不存在：" + CandidateForDisplay(candidate));
            }

            return new PathSandboxResult(PathSandboxStatus.Allowed, full, relative, string.Empty);
        }

        public PathSandboxResult Resolve(string candidate, PathAccessKind kind)
        {
            return Resolve(candidate, kind, false);
        }

        /// <summary>把工作区内的绝对路径转成相对路径；不在工作区内时返回 null。</summary>
        public string ToRelativePath(string absolutePath)
        {
            if (string.IsNullOrWhiteSpace(absolutePath))
            {
                return null;
            }

            string full;
            try
            {
                full = TrimTrailingSeparators(Path.GetFullPath(absolutePath));
            }
            catch (Exception exception) when (
                exception is ArgumentException
                || exception is NotSupportedException
                || exception is PathTooLongException)
            {
                return null;
            }

            bool isRoot;
            string relative;
            if (!TryGetRelative(full, out isRoot, out relative))
            {
                return null;
            }

            return relative.Length == 0 ? "." : relative;
        }

        private bool TryGetRelative(string full, out bool isRoot, out string relative)
        {
            isRoot = false;
            relative = null;

            if (string.Equals(full, RootPath, StringComparison.OrdinalIgnoreCase))
            {
                isRoot = true;
                relative = string.Empty;
                return true;
            }

            string prefix = RootPath.EndsWith("\\", StringComparison.Ordinal)
                ? RootPath
                : RootPath + "\\";

            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            relative = full.Substring(prefix.Length);
            return true;
        }

        private bool FindReparsePoint(string full, out string blockedSegment)
        {
            blockedSegment = null;
            if (_probe == null)
            {
                return false;
            }

            // 根目录本身可能是 junction（工作区放在别的盘），不参与检查。
            bool isRoot;
            string relative;
            if (!TryGetRelative(full, out isRoot, out relative) || isRoot || relative.Length == 0)
            {
                return false;
            }

            string[] segments = relative.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
            string current = RootPath;
            for (int i = 0; i < segments.Length; i++)
            {
                current = current.EndsWith("\\", StringComparison.Ordinal)
                    ? current + segments[i]
                    : current + "\\" + segments[i];

                FileAttributes attributes;
                if (_probe.TryGetAttributes(current, out attributes)
                    && (attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                {
                    blockedSegment = current;
                    return true;
                }
            }

            return false;
        }

        private static bool HasUncOrDevicePrefix(string candidate)
        {
            if (candidate.Length < 2)
            {
                return false;
            }

            char first = candidate[0];
            char second = candidate[1];
            return (first == '\\' && second == '\\') || (first == '/' && second == '/');
        }

        private static bool ContainsInvalidNameChars(string candidate)
        {
            for (int i = 0; i < candidate.Length; i++)
            {
                char current = candidate[i];
                if (current == ':' && i == 1 && char.IsLetter(candidate[0]))
                {
                    // 盘符后的冒号是合法的；其余冒号都是 ADS 或非法字符。
                    continue;
                }

                for (int j = 0; j < InvalidNameChars.Length; j++)
                {
                    if (current == InvalidNameChars[j])
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool ContainsReservedDeviceName(string candidate)
        {
            string[] segments = candidate.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < segments.Length; i++)
            {
                string name = segments[i];
                int dot = name.IndexOf('.');
                if (dot >= 0)
                {
                    name = name.Substring(0, dot);
                }

                name = name.TrimEnd(' ');
                for (int j = 0; j < ReservedDeviceNames.Length; j++)
                {
                    if (string.Equals(name, ReservedDeviceNames[j], StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static string TrimTrailingSeparators(string path)
        {
            string trimmed = path;
            while (trimmed.Length > 3
                   && (trimmed[trimmed.Length - 1] == '\\' || trimmed[trimmed.Length - 1] == '/'))
            {
                trimmed = trimmed.Substring(0, trimmed.Length - 1);
            }

            return trimmed;
        }

        private static string CandidateForDisplay(string candidate)
        {
            const int MaxLength = 160;
            string text = (candidate ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
            return text.Length <= MaxLength ? text : text.Substring(0, MaxLength) + "…";
        }

        private static PathSandboxResult Reject(PathSandboxStatus status, string detail)
        {
            return new PathSandboxResult(status, null, null, detail);
        }
    }
}
