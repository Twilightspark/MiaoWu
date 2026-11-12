using System;
using System.Runtime.InteropServices;

namespace MiaoWu.Core.Windowing
{
    /// <summary>
    /// 真实系统版本。GetVersionEx / <see cref="Environment.OSVersion"/> 在没有
    /// Windows 10+ supportedOS 清单时会返回兼容层的 Windows 8（build 9200），
    /// 因此统一走 ntdll!RtlGetVersion，避免宿主进程清单不同导致窗口外沿判断漂移。
    /// </summary>
    public static class WindowsVersion
    {
        /// <summary>当前系统 build，例如 Windows 11 为 22000+。</summary>
        public static int CurrentBuild
        {
            get
            {
                var info = new OsVersionInfoEx
                {
                    Size = (uint)Marshal.SizeOf(typeof(OsVersionInfoEx))
                };

                return RtlGetVersion(ref info) == 0
                    ? (int)info.BuildNumber
                    : Environment.OSVersion.Version.Build;
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OsVersionInfoEx
        {
            public uint Size;
            public uint MajorVersion;
            public uint MinorVersion;
            public uint BuildNumber;
            public uint PlatformId;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string ServicePack;

            public ushort ServicePackMajor;
            public ushort ServicePackMinor;
            public ushort SuiteMask;
            public byte ProductType;
            public byte Reserved;
        }

        [DllImport("ntdll.dll")]
        private static extern int RtlGetVersion(ref OsVersionInfoEx versionInfo);
    }
}
