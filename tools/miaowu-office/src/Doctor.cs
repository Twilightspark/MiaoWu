using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace MiaowuOffice
{
    /// <summary>第一轮诊断：这台机器能不能跑、WPS 注册得对不对、哪些能力可用。</summary>
    internal static class Doctor
    {
        private static readonly string[] CandidateKeys = new string[]
        {
            @"SOFTWARE\WOW6432Node\Kingsoft\Office\6.0\common",
            @"SOFTWARE\Kingsoft\Office\6.0\common",
            @"SOFTWARE\WOW6432Node\Kingsoft\Office",
            @"SOFTWARE\Kingsoft\Office",
            @"SOFTWARE\WOW6432Node\Kingsoft\Office\6.0",
            @"SOFTWARE\Kingsoft\Office\6.0"
        };

        private static readonly string[] CandidateValues = new string[]
        {
            "Version", "InstallRoot", "InstallPath", "Path", "CommonPath", "ExePath"
        };

        public static Jso Run(Args args)
        {
            var result = new Jso();

            var runtime = new Jso();
            runtime.Add("arch", Environment.Is64BitProcess ? "x64" : "x86");
            runtime.Add("osBits", Environment.Is64BitOperatingSystem ? 64 : 32);
            runtime.Add("clr", Environment.Version.ToString());
            runtime.Add("os", Win32.Describe());
            runtime.Add("machine", Environment.MachineName);
            runtime.Add("processors", Environment.ProcessorCount);
            runtime.Add("systemPageBytes", Environment.SystemPageSize);
            result.Add("runtime", runtime);

            var wps = new Jso();
            var registry = new Jso();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < CandidateKeys.Length; i++)
            {
                try
                {
                    using (RegistryKey key = Registry.LocalMachine.OpenSubKey(CandidateKeys[i]))
                    {
                        if (key == null) continue;
                        for (int v = 0; v < CandidateValues.Length; v++)
                        {
                            if (seen.Contains(CandidateValues[v])) continue;
                            object value = key.GetValue(CandidateValues[v]);
                            if (value == null) continue;
                            string text = Convert.ToString(value);
                            if (string.IsNullOrEmpty(text)) continue;
                            registry.Add(CandidateValues[v], text);
                            seen.Add(CandidateValues[v]);
                        }
                    }
                }
                catch { }
            }
            if (registry.Count > 0) wps.Add("registry", registry);

            var progIds = new Jso();
            progIds.Add("writer", ProgIdPresent("KWPS.Application"));
            progIds.Add("spreadsheet", ProgIdPresent("KET.Application"));
            progIds.Add("presentation", ProgIdPresent("KWPP.Application"));
            wps.Add("progIds", progIds);
            wps.Add("installed", ProgIdPresent("KWPS.Application") && ProgIdPresent("KET.Application") && ProgIdPresent("KWPP.Application"));

            var busy = Processes.RunningMain();
            wps.Add("busy", busy);
            wps.Add("canAutomate", busy.Count == 0);
            result.Add("wps", wps);

            var capabilities = new Jso();
            capabilities.Add("inspect", new string[] { "docx", "doc", "docm" });
            capabilities.Add("apply", WordOps.KnownOps);
            capabilities.Add("renderPdf", new string[] { "docx", "doc", "xlsx", "xls", "pptx", "ppt" });
            capabilities.Add("renderPng", new string[] { "pptx", "ppt" });
            capabilities.Add("renderPngViaPdfium", Pdfium.Available() ? new string[] { "docx", "doc", "xlsx", "xls" } : new string[0]);
            capabilities.Add("restore", true);
            result.Add("capabilities", capabilities);

            var pdfium = new Jso();
            string dll = Pdfium.DllPath();
            pdfium.Add("available", dll != null);
            if (dll != null)
            {
                pdfium.Add("path", dll);
                try { pdfium.Add("bytes", new FileInfo(dll).Length); } catch { }
            }
            else
            {
                pdfium.Add("hint", "把 64 位 pdfium.dll 放到 miaowu_office.exe 同目录，Word/Excel 就能把 PDF 转成 PNG");
            }
            result.Add("pdfium", pdfium);

            var temp = new Jso();
            try
            {
                string dir = Path.GetTempPath();
                temp.Add("dir", dir);
                string root = Path.GetPathRoot(dir);
                if (!string.IsNullOrEmpty(root))
                {
                    var drive = new DriveInfo(root);
                    temp.Add("freeMb", drive.AvailableFreeSpace / (1024 * 1024));
                }
            }
            catch { }
            result.Add("temp", temp);

            var limits = new Jso();
            limits.Add("maxPlanOperations", 60);
            limits.Add("maxInspectParagraphs", 2000);
            limits.Add("maxTableRows", 60);
            limits.Add("maxTableColumns", 12);
            limits.Add("singleDocumentAtATime", true);
            limits.Add("note", "文档超过 50MB 或段落超过 5000 时，请分段 inspect（--from/--max-paragraphs）与分段 apply");
            result.Add("limits", limits);

            if (args.Flag("deep"))
            {
                result.Add("deep", Deep());
            }

            return result;
        }

        private static bool ProgIdPresent(string progId)
        {
            try { return Type.GetTypeFromProgID(progId) != null; }
            catch { return false; }
        }

        private static Jso Deep()
        {
            var deep = new Jso();
            // 真正起一次 WPS 文字，验证 COM 通道与版本；不打开任何文档。
            try
            {
                deep.Add("writer", Wps.RunSta(30000, delegate ()
                {
                    dynamic app = null;
                    var ownership = new ComOwnership();
                    try
                    {
                        app = Wps.Create("KWPS.Application", "WPS 文字");
                        ownership.Capture();
                        var item = new Jso();
                        item.Add("ok", true);
                        item.Add("version", Word.TryStr(delegate { return (string)app.Version; }));
                        item.Add("build", Word.TryStr(delegate { return (string)app.Build; }));
                        item.Add("name", Word.TryStr(delegate { return (string)app.Name; }));
                        return item;
                    }
                    finally
                    {
                        Wps.Quit((object)app);
                        Wps.Release((object)app);
                        ownership.Cleanup();
                    }
                }));
            }
            catch (Exception ex)
            {
                var tool = ex as ToolException;
                deep.Add("writer", new Jso().Add("ok", false)
                    .Add("code", tool != null ? tool.Code : "wps_error")
                    .Add("message", ex.Message));
            }
            return deep;
        }
    }
}
