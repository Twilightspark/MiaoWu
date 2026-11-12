using System;

namespace MiaoWu.Core
{
    /// <summary>
    /// 应用级常量与版本信息。
    /// </summary>
    public static class AppInfo
    {
        /// <summary>产品名称，用于窗口标题、日志和提示。</summary>
        public const string Name = "MiaoWu";

        /// <summary>当前程序集版本，格式为 Major.Minor.Patch。</summary>
        public static string Version
        {
            get
            {
                Version version = typeof(AppInfo).Assembly.GetName().Version;
                return version == null
                    ? "0.0.0"
                    : version.ToString(3);
            }
        }
    }
}
