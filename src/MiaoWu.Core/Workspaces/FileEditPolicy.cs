using System;
using System.Collections.Generic;
using System.IO;

namespace MiaoWu.Core.Workspaces
{
    /// <summary>
    /// 文件重命名的名称校验与目标路径计算。只做纯规则判断，不访问磁盘，
    /// 便于单测覆盖；实际移动与冲突检查由 Infrastructure 完成。
    /// </summary>
    public static class FileEditPolicy
    {
        public const string EmptyNameError = "文件名不能为空。";

        public const string InvalidNameError = "文件名不能包含路径分隔符或非法字符。";

        public const string ReservedNameError = "该名称是系统保留名称，请换一个。";

        public const string SameNameError = "文件名没有变化。";

        private static readonly char[] InvalidChars = { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

        private static readonly HashSet<string> ReservedNames = new HashSet<string>(
            new[]
            {
                "CON", "PRN", "AUX", "NUL",
                "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
            },
            StringComparer.OrdinalIgnoreCase);

        /// <summary>去掉首尾空白；空名、非法字符、保留名与路径分隔符返回错误。</summary>
        public static bool TryNormalizeName(string newName, out string normalized, out string error)
        {
            normalized = (newName ?? string.Empty).Trim();
            error = null;

            if (normalized.Length == 0)
            {
                error = EmptyNameError;
                return false;
            }

            if (normalized == "." || normalized == ".."
                || normalized.IndexOfAny(InvalidChars) >= 0
                || normalized.IndexOf(Path.DirectorySeparatorChar) >= 0
                || normalized.IndexOf(Path.AltDirectorySeparatorChar) >= 0
                || normalized.EndsWith(".", StringComparison.Ordinal))
            {
                error = InvalidNameError;
                return false;
            }

            string stem = Path.GetFileNameWithoutExtension(normalized);
            if (ReservedNames.Contains(stem))
            {
                error = ReservedNameError;
                return false;
            }

            return true;
        }

        /// <summary>计算同目录下的目标路径；同名（忽略大小写）返回 false 并给出提示。</summary>
        public static bool TryBuildTargetPath(
            string filePath,
            string newName,
            out string targetPath,
            out string error)
        {
            targetPath = null;
            error = null;

            if (string.IsNullOrWhiteSpace(filePath))
            {
                error = EmptyNameError;
                return false;
            }

            string normalized;
            if (!TryNormalizeName(newName, out normalized, out error))
            {
                return false;
            }

            string directory = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(directory))
            {
                error = InvalidNameError;
                return false;
            }

            if (string.Equals(
                Path.GetFileName(filePath),
                normalized,
                StringComparison.OrdinalIgnoreCase))
            {
                error = SameNameError;
                return false;
            }

            targetPath = Path.Combine(directory, normalized);
            return true;
        }
    }
}
