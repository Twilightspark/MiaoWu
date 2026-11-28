using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace MiaowuOffice
{
    /// <summary>WPS 主进程观测与清理。只认 wps / et / wpp 三个主程序。</summary>
    internal static class Processes
    {
        private static readonly string[] MainNames = new string[] { "wps", "et", "wpp" };

        /// <summary>当前在跑的 WPS 主进程（用于 wps_busy 判定）。</summary>
        public static List<Jso> RunningMain()
        {
            var list = new List<Jso>();
            for (int i = 0; i < MainNames.Length; i++)
            {
                Process[] procs;
                try { procs = Process.GetProcessesByName(MainNames[i]); }
                catch { continue; }
                for (int j = 0; j < procs.Length; j++)
                {
                    Process p = procs[j];
                    try
                    {
                        var item = new Jso();
                        item.Add("process", p.ProcessName);
                        item.Add("pid", p.Id);
                        string title = string.Empty;
                        try { title = p.MainWindowTitle ?? string.Empty; } catch { }
                        if (title.Length > 0) item.Add("window", title);
                        list.Add(item);
                    }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            return list;
        }

        public static HashSet<int> Snapshot()
        {
            var set = new HashSet<int>();
            for (int i = 0; i < MainNames.Length; i++)
            {
                Process[] procs;
                try { procs = Process.GetProcessesByName(MainNames[i]); }
                catch { continue; }
                for (int j = 0; j < procs.Length; j++)
                {
                    try { set.Add(procs[j].Id); } catch { }
                    finally { try { procs[j].Dispose(); } catch { } }
                }
            }
            return set;
        }

        /// <summary>只结束 pids 里列出的进程，不碰用户自己打开的实例。</summary>
        public static int KillPids(ICollection<int> pids)
        {
            if (pids == null || pids.Count == 0) return 0;
            int killed = 0;
            for (int i = 0; i < MainNames.Length; i++)
            {
                Process[] procs;
                try { procs = Process.GetProcessesByName(MainNames[i]); }
                catch { continue; }
                for (int j = 0; j < procs.Length; j++)
                {
                    Process p = procs[j];
                    try
                    {
                        if (!pids.Contains(p.Id)) continue;
                        p.Kill();
                        killed++;
                        try { p.WaitForExit(3000); } catch { }
                    }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            return killed;
        }

        public static bool AnyAlive(ICollection<int> pids)
        {
            if (pids == null || pids.Count == 0) return false;
            for (int i = 0; i < MainNames.Length; i++)
            {
                Process[] procs;
                try { procs = Process.GetProcessesByName(MainNames[i]); }
                catch { continue; }
                for (int j = 0; j < procs.Length; j++)
                {
                    try
                    {
                        if (pids.Contains(procs[j].Id)) return true;
                    }
                    catch { }
                    finally { try { procs[j].Dispose(); } catch { } }
                }
            }
            return false;
        }
    }

    /// <summary>
    /// 记录「哪些 WPS 进程是本次运行启动的」。收尾时只结束自己启动的实例：
    /// 先等 Quit 生效，仍在就杀掉，避免留下无窗口的僵尸进程挡住下一次调用。
    /// </summary>
    internal sealed class ComOwnership
    {
        private readonly HashSet<int> _before = Processes.Snapshot();
        private readonly HashSet<int> _owned = new HashSet<int>();

        /// <summary>在创建 COM 对象之后调用，抓取新出现的 WPS 主进程 PID。</summary>
        public void Capture()
        {
            for (int round = 0; round < 10; round++)
            {
                var now = Processes.Snapshot();
                foreach (int pid in now)
                {
                    if (!_before.Contains(pid)) _owned.Add(pid);
                }
                if (_owned.Count > 0) return;
                Thread.Sleep(200);
            }
        }

        public int OwnedCount { get { return _owned.Count; } }

        /// <summary>退出后兜底：最多等 6 秒，仍在的进程结束掉。</summary>
        public int Cleanup()
        {
            if (_owned.Count == 0) return 0;
            for (int i = 0; i < 30; i++)
            {
                if (!Processes.AnyAlive(_owned)) return 0;
                Thread.Sleep(200);
            }
            return Processes.KillPids(_owned);
        }
    }

    /// <summary>WPS COM 会话的公共外壳：前台忙判定、STA 主线程执行、看门狗、COM 释放。</summary>
    internal static class Wps
    {
        /// <summary>若已有 WPS 主程序在跑就返回 wps_busy，让用户先关掉；不接管用户的实例。</summary>
        public static void EnsureNotBusy(bool allowRunning)
        {
            if (allowRunning) return;
            var running = Processes.RunningMain();
            if (running.Count == 0) return;
            var detail = new Jso().Add("processes", running);
            throw new ToolException("wps_busy",
                "WPS 正在运行（" + running.Count.ToString() + " 个进程）。自动化会复用当前实例并可能改变你正在编辑的文档，请先保存并关闭所有 WPS 窗口后重试。",
                detail);
        }

        /// <summary>
        /// 在调用线程（Main 已标 [STAThread]）上执行 COM 工作，并用看门狗兜底：
        /// 超时后终止本次启动的 WPS 进程，卡住的 RPC 会以异常返回，调用方统一收到 wps_timeout。
        /// Office/WPS 的 COM 只认 STA；把工作放回带消息泵的主线程可以避免跨套间回调让服务器退出。
        /// </summary>
        public static T RunSta<T>(int timeoutMs, Func<T> work)
        {
            HashSet<int> before = Processes.Snapshot();
            bool[] timedOut = new bool[1];
            var watchdog = new Timer(delegate (object state)
            {
                timedOut[0] = true;
                var started = new HashSet<int>(Processes.Snapshot());
                foreach (int pid in before) started.Remove(pid);
                Processes.KillPids(started);
            }, null, timeoutMs, Timeout.Infinite);

            T result = default(T);
            Exception captured = null;
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
            finally
            {
                watchdog.Dispose();
            }

            if (timedOut[0])
            {
                var timeoutTool = captured as ToolException;
                string why = timeoutTool != null ? timeoutTool.Message
                    : (captured != null ? Friendly(captured) : "操作没有返回");
                throw new ToolException("wps_timeout",
                    "WPS 在 " + timeoutMs.ToString() + " 毫秒内没有完成操作（" + why + "）。已终止本次启动的 WPS 进程；大文档请减小范围或提高 --timeout。",
                    new Jso().Add("timeoutMs", timeoutMs));
            }

            if (captured != null)
            {
                var tool = captured as ToolException;
                if (tool != null) throw tool;
                throw new ToolException("wps_error", Friendly(captured), ErrorDetail(captured));
            }
            return result;
        }

        public static Jso ErrorDetail(Exception ex)
        {
            var detail = new Jso().Add("type", ex.GetType().FullName);
            var com = ex as COMException;
            if (com != null)
            {
                detail.Add("hresult", "0x" + com.HResult.ToString("X8"));
            }
            var inner = ex.InnerException;
            if (inner != null) detail.Add("inner", inner.Message);
            return detail;
        }

        /// <summary>把 COM 的英文报错折成一句人话，保留原文便于排查。</summary>
        public static string Friendly(Exception ex)
        {
            if (ex == null) return "未知错误";
            string msg = ex.Message;
            var com = ex as COMException;
            if (com != null)
            {
                if (com.HResult == unchecked((int)0x8001010A)) return "WPS 正忙（RPC_E_SERVERCALL_RETRYLATER）：" + msg;
                if (com.HResult == unchecked((int)0x800706BA)) return "WPS 进程已退出（RPC 服务器不可用）：" + msg;
                if (com.HResult == unchecked((int)0x80080005)) return "无法启动 WPS 进程（CO_E_SERVER_EXEC_FAILURE）：" + msg;
                if (com.HResult == unchecked((int)0x80040154)) return "WPS 组件未注册（REGDB_E_CLASSNOTREG）：" + msg;
                return "WPS 调用失败（0x" + com.HResult.ToString("X8") + "）：" + msg;
            }
            return msg;
        }

        /// <summary>创建 WPS 应用对象；注册表里没有 ProgID 时给出可执行的处置建议。</summary>
        public static dynamic Create(string progId, string friendly)
        {
            Type type = null;
            try { type = Type.GetTypeFromProgID(progId); }
            catch { }
            if (type == null)
            {
                throw new ToolException("wps_missing",
                    "本机没有注册 " + friendly + "（" + progId + "）。请确认 WPS Office 已正确安装，并至少启动过一次；若装了多个版本，请修复默认打开方式。",
                    new Jso().Add("progId", progId));
            }

            Exception last = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try { return Activator.CreateInstance(type); }
                catch (Exception ex)
                {
                    last = ex;
                    Thread.Sleep(400);
                }
            }
            throw new ToolException("wps_start_failed",
                "启动 " + friendly + " 失败：" + Friendly(last),
                new Jso().Add("progId", progId).Add("attempts", 3));
        }

        public static void Quit(object app)
        {
            if (app == null) return;
            try
            {
                Type t = app.GetType();
                t.InvokeMember("Quit", System.Reflection.BindingFlags.InvokeMethod, null, app, new object[0]);
            }
            catch { }
        }

        public static void Release(params object[] objects)
        {
            if (objects == null) return;
            for (int i = objects.Length - 1; i >= 0; i--)
            {
                object o = objects[i];
                if (o == null) continue;
                try
                {
                    if (Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
                }
                catch { }
            }
        }
    }
}
