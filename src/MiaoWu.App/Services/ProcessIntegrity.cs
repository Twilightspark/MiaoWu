using System;
using System.Runtime.InteropServices;

namespace MiaoWu.App.Services
{
    /// <summary>
    /// 进程完整性级别探测。Windows 只允许 Medium 及以上的进程向任务栏注册托盘图标；
    /// 从 VS Code / 沙箱 / 受限终端启动时进程是 Low，Shell_NotifyIcon 会返回 Access Denied。
    /// 无法读取时按“可用”处理，避免误伤正常启动路径。
    /// </summary>
    internal static class ProcessIntegrity
    {
        private const uint TokenQuery = 0x0008;
        private const int TokenIntegrityLevel = 25;
        private const uint SecurityMandatoryMediumRid = 0x2000;

        public static bool IsAtLeastMedium()
        {
            IntPtr token = IntPtr.Zero;
            IntPtr buffer = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out token))
                {
                    return true;
                }

                int size;
                GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out size);
                if (size <= 0)
                {
                    return true;
                }

                buffer = Marshal.AllocHGlobal(size);
                if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, size, out size))
                {
                    return true;
                }

                // TOKEN_MANDATORY_LABEL 的第一个字段是 SID_AND_ATTRIBUTES，首指针即 SID。
                IntPtr sid = Marshal.ReadIntPtr(buffer);
                if (sid == IntPtr.Zero)
                {
                    return true;
                }

                byte subAuthorityCount = Marshal.ReadByte(GetSidSubAuthorityCount(sid), 0);
                if (subAuthorityCount == 0)
                {
                    return true;
                }

                // 完整性级别是 SID 的最后一个 SubAuthority，例如 Medium = 0x2000。
                IntPtr ridPointer = GetSidSubAuthority(sid, (uint)(subAuthorityCount - 1));
                if (ridPointer == IntPtr.Zero)
                {
                    return true;
                }

                uint rid = (uint)Marshal.ReadInt32(ridPointer);
                return rid >= SecurityMandatoryMediumRid;
            }
            catch (Exception)
            {
                return true;
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(buffer);
                }

                if (token != IntPtr.Zero)
                {
                    CloseHandle(token);
                }
            }
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(
            IntPtr token,
            int informationClass,
            IntPtr information,
            int length,
            out int returnedLength);

        [DllImport("advapi32.dll")]
        private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

        [DllImport("advapi32.dll")]
        private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
    }
}
