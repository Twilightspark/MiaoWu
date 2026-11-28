using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace MiaowuOffice
{
    /// <summary>命令行入口。所有输出都是单个 JSON 对象，模型只读这一份。</summary>
    internal static class Program
    {
        // WPS 的 COM 只认 STA：主线程必须是 STA，COM 调用也放在这条线程上执行。
        [STAThread]
        private static int Main(string[] argv)
        {
            try { Console.OutputEncoding = new UTF8Encoding(false); }
            catch { }

            long started = Environment.TickCount;
            Args args;
            try
            {
                args = Args.Parse(argv ?? new string[0]);
            }
            catch (Exception ex)
            {
                return Fail(new ToolException("usage", "参数解析失败：" + ex.Message), null, started, null);
            }

            string command = args.Command;
            if (string.IsNullOrEmpty(command) || args.Flag("help")) return Emit(Help(), args, started, "help");

            try
            {
                Jso payload;
                switch (command.ToLowerInvariant())
                {
                    case "version":
                        payload = new Jso().Add("version", ToolInfo.Version);
                        break;
                    case "doctor":
                        payload = Doctor.Run(args);
                        break;
                    case "inspect":
                        payload = Commands.Inspect(args);
                        break;
                    case "apply":
                        payload = Commands.Apply(args);
                        break;
                    case "render":
                        payload = Commands.Render(args);
                        break;
                    case "restore":
                        payload = Commands.Restore(args);
                        break;
                    default:
                        throw new ToolException("usage",
                            "未知命令：" + command + "。可用：doctor / inspect / apply / render / restore / version",
                            new Jso().Add("help", "运行 miaowu_office.exe --help 查看用法"));
                }
                return Emit(payload, args, started, command.ToLowerInvariant());
            }
            catch (ToolException ex)
            {
                return Fail(ex, args, started, command);
            }
            catch (Exception ex)
            {
                return Fail(new ToolException("internal", ex.GetType().Name + "：" + ex.Message,
                    new Jso().Add("type", ex.GetType().FullName)), args, started, command);
            }
        }

        // ------------------------------------------------------------- 输出

        private static int Emit(Jso payload, Args args, long started, string command)
        {
            var root = new Jso();
            root.Add("ok", true);
            root.Add("tool", ToolInfoObject());
            if (command != null) root.Add("command", command);
            root.Add("elapsedMs", Environment.TickCount - started);
            foreach (var kv in payload) root.Add(kv.Key, kv.Value);

            bool pretty = args != null && args.Flag("pretty");
            string text = JsonText.Write(root, pretty);
            WriteResultFile(args, text);
            WriteStdout(text);
            return 0;
        }

        private static int Fail(ToolException ex, Args args, long started, string command)
        {
            var root = new Jso();
            root.Add("ok", false);
            root.Add("tool", ToolInfoObject());
            if (!string.IsNullOrEmpty(command)) root.Add("command", command);
            root.Add("error", WordOps.ErrorObject(ex));
            root.Add("elapsedMs", Environment.TickCount - started);

            bool pretty = args != null && args.Flag("pretty");
            string text = JsonText.Write(root, pretty);
            WriteResultFile(args, text);
            WriteStdout(text);
            return ExitCode(ex.Code);
        }

        private static Jso ToolInfoObject()
        {
            return new Jso().Add("name", ToolInfo.Name).Add("version", ToolInfo.Version);
        }

        private static void WriteStdout(string text)
        {
            try
            {
                Console.Out.Write(text);
                Console.Out.Write("\n");
                Console.Out.Flush();
            }
            catch { }
        }

        private static void WriteResultFile(Args args, string text)
        {
            if (args == null) return;
            string path = args.Get("result", null);
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                string full = Guard.FullPath(path, "--result");
                string workspace = args.Get("workspace", null);
                if (!string.IsNullOrEmpty(workspace))
                {
                    Guard.EnsureInside(Guard.FullPath(workspace, "--workspace"), full, "--result");
                }
                string dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(full, text, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                // 写结果文件失败不影响主流程，stdout 里已经有一份。
                Console.Error.WriteLine("[miaowu_office] 写 --result 失败：" + ex.Message);
            }
        }

        private static int ExitCode(string code)
        {
            switch (code)
            {
                case "usage": return 2;
                case "unsupported":
                case "unsupported_op": return 7;
                case "file_not_found":
                case "file_unreadable":
                case "invalid_file":
                case "bad_path":
                case "outside_workspace":
                case "bad_plan":
                case "backup_not_found":
                case "backup_corrupt": return 3;
                case "wps_busy": return 4;
                case "wps_missing":
                case "wps_start_failed":
                case "wps_error":
                case "wps_timeout": return 5;
                case "validation_failed":
                case "save_failed":
                case "replace_failed":
                case "file_locked":
                case "anchor_not_found":
                case "anchor_changed":
                case "index_out_of_range":
                case "style_not_found":
                case "replace_failed_partial":
                case "table_insert_failed":
                case "last_paragraph":
                case "property_failed": return 6;
                default: return 1;
            }
        }

        private static Jso Help()
        {
            var help = new Jso();
            help.Add("summary", "miaowu_office：用 WPS 读写 Office 文档的本地 CLI。只依赖 .NET Framework 4.8，可在 Windows 7 SP1 x64 运行。");
            help.Add("commands", new Jso()
                .Add("doctor", "环境自检：WPS 是否注册、通道是否可用、能力清单。--deep 会真起一次 WPS 文字验证 COM。")
                .Add("inspect", "读 Word 文档结构：段落（index / style / text / hash）、样式、表格、目录、页数。")
                .Add("apply", "按计划修改 Word 文档：替换、改段落、改样式、插表格、更新目录、导出 PDF。带备份与回滚。")
                .Add("render", "把文档渲染成 PDF / PNG（PPT 直接出 PNG；Word/Excel 出 PDF，装了 pdfium.dll 再转 PNG）。")
                .Add("restore", "从 .miaowu-backup-* 备份恢复。"));
            help.Add("usage", new string[]
            {
                "miaowu_office.exe doctor [--deep] [--pretty]",
                "miaowu_office.exe inspect --file report.docx [--from 1] [--max-paragraphs 400] [--pages]",
                "miaowu_office.exe apply --file report.docx --plan plan.json [--dry-run] [--result result.json]",
                "miaowu_office.exe render --file deck.pptx [--out 目录] [--format png] [--page 3] [--dpi 120]",
                "miaowu_office.exe render --file report.docx [--out 目录] [--format pdf]",
                "miaowu_office.exe restore --file report.docx [--backup 路径 | --latest | --list]"
            });
            help.Add("globalOptions", new Jso()
                .Add("--workspace", "工作区根目录；给定时所有文件路径必须落在它里面（推荐总是传）")
                .Add("--result", "把这一份 JSON 同时写到文件，便于二次读取")
                .Add("--pretty", "缩进输出")
                .Add("--timeout", "毫秒，默认 inspect 90s / apply 150s / render 120s")
                .Add("--allow-running", "允许复用已经打开的 WPS（默认拒绝并返回 wps_busy）"));
            help.Add("plan", "计划格式见 skills/office-wps/examples/word-plan.json；段落目标用 index+expect(hash) 最稳。");
            help.Add("exitCodes", new Jso()
                .Add("0", "成功")
                .Add("2", "参数错误")
                .Add("3", "文件/路径/计划问题")
                .Add("4", "wps_busy：有 WPS 在运行，请先关闭")
                .Add("5", "WPS 调用失败或超时")
                .Add("6", "写回失败（占用 / 只读 / 校验不通过）")
                .Add("7", "不支持的操作或格式"));
            return help;
        }
    }

    /// <summary>各命令的参数解析与调用。</summary>
    internal static class Commands
    {
        public static Jso Inspect(Args args)
        {
            string workspace;
            string file = ResolveFile(args, out workspace);
            if (!Word.Accepts(file))
            {
                throw new ToolException("unsupported",
                    "inspect 只支持 Word（.docx / .doc / .docm）：" + Path.GetExtension(file),
                    new Jso().Add("hint", "PPT 用 render 出 PNG 预览，Excel 用 render 出 PDF；这两个格式本技能不改内容"));
            }
            int from = Math.Max(1, args.GetInt("from", 1));
            int max = args.GetInt("max-paragraphs", 400);
            if (max < 1) max = 1;
            if (max > 2000) max = 2000;
            bool withPages = args.Flag("pages");

            var result = Word.Inspect(file, from, max, withPages);
            if (!string.IsNullOrEmpty(workspace)) result.Add("workspace", workspace);
            return result;
        }

        public static Jso Apply(Args args)
        {
            string workspace;
            string file = ResolveFile(args, out workspace);
            if (!Word.Accepts(file))
            {
                throw new ToolException("unsupported",
                    "apply 只支持 Word（.docx / .doc / .docm）：" + Path.GetExtension(file),
                    new Jso().Add("hint", "PPT / Excel 本技能只做预览，不修改内容；请让用户手动编辑"));
            }
            string planArg = args.Get("plan", null);
            if (string.IsNullOrEmpty(planArg)) throw new ToolException("usage", "apply 需要 --plan <计划文件>");
            string plan = Guard.FullPath(planArg, "--plan");
            Guard.EnsureInside(workspace, plan, "--plan");
            if (!File.Exists(plan)) throw new ToolException("file_not_found", "计划文件不存在：" + plan);

            int timeout = args.GetInt("timeout", Timeouts.Apply);
            bool dryRun = args.Flag("dry-run");
            bool continueOnError = args.Flag("continue-on-error");
            bool allowRunning = args.Flag("allow-running");

            return WordOps.Run(file, plan, dryRun, timeout, continueOnError, allowRunning, workspace);
        }

        public static Jso Render(Args args)
        {
            string workspace;
            string file = ResolveFile(args, out workspace);
            string outArg = args.Get("out", null);
            string format = args.Get("format", null);
            int page = args.GetInt("page", 0);
            int dpi = args.GetInt("dpi", 120);
            if (dpi < 60) dpi = 60;
            if (dpi > 200) dpi = 200;
            int timeout = args.GetInt("timeout", Timeouts.Render);

            if (!string.IsNullOrEmpty(outArg))
            {
                string fullOut = Guard.FullPath(outArg, "--out");
                Guard.EnsureInside(workspace, fullOut, "--out");
                outArg = fullOut;
            }

            var result = MiaowuOffice.Render.Run(file, outArg, format, page, dpi, timeout, args.Flag("allow-running"));
            result.Add("workspaceChecked", !string.IsNullOrEmpty(workspace));
            return result;
        }

        public static Jso Restore(Args args)
        {
            string workspace;
            string file = ResolveFile(args, out workspace);

            if (args.Flag("list"))
            {
                var backups = ListBackups(file);
                return new Jso().Add("file", file).Add("backups", backups).Add("count", backups.Count);
            }

            string backup;
            string backupArg = args.Get("backup", null);
            if (!string.IsNullOrEmpty(backupArg))
            {
                backup = Guard.FullPath(backupArg, "--backup");
                Guard.EnsureInside(workspace, backup, "--backup");
            }
            else if (args.Flag("latest"))
            {
                var backups = ListBackups(file);
                if (backups.Count == 0)
                {
                    throw new ToolException("backup_not_found", "同目录没有找到 " + Path.GetFileName(file) + " 的备份",
                        new Jso().Add("pattern", Path.GetFileNameWithoutExtension(file) + ".miaowu-backup-*"));
                }
                backup = (string)((Jso)backups[0])["path"];
            }
            else
            {
                throw new ToolException("usage", "restore 需要 --backup <路径>、--latest 或 --list");
            }

            if (!File.Exists(backup)) throw new ToolException("file_not_found", "备份文件不存在：" + backup);
            if (Ooxml.IsOoxml(backup))
            {
                var check = Ooxml.Validate(backup);
                if (!(bool)check["ok"])
                {
                    throw new ToolException("backup_corrupt", "备份文件本身不完整，拒绝用它覆盖当前文档", new Jso().Add("validation", check));
                }
            }

            string temp = Files.TempPath(file);
            try
            {
                File.Copy(backup, temp, true);
            }
            catch (Exception ex)
            {
                Files.TryDelete(temp);
                throw new ToolException("file_locked", "无法复制备份：" + ex.Message);
            }

            string safety = Files.ReplacedPath(file);
            long before = new FileInfo(file).Length;
            Files.ReplaceWithBackup(temp, file, safety);

            var result = new Jso();
            result.Add("file", file);
            result.Add("restoredFrom", backup);
            result.Add("previousVersion", safety);
            result.Add("sizeBefore", before);
            result.Add("sizeAfter", new FileInfo(file).Length);
            return result;
        }

        private static List<object> ListBackups(string file)
        {
            var list = new List<object>();
            string dir = Path.GetDirectoryName(file);
            string stem = Path.GetFileNameWithoutExtension(file);
            string ext = Path.GetExtension(file);
            try
            {
                var files = Directory.GetFiles(dir, stem + ".miaowu-backup-*" + ext);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                Array.Reverse(files);
                for (int i = 0; i < files.Length; i++)
                {
                    var info = new FileInfo(files[i]);
                    list.Add(new Jso()
                        .Add("path", info.FullName)
                        .Add("bytes", info.Length)
                        .Add("modified", info.LastWriteTime.ToString("yyyy-MM-ddTHH:mm:sszzz")));
                }
            }
            catch { }
            return list;
        }

        private static string ResolveFile(Args args, out string workspace)
        {
            workspace = args.Get("workspace", null);
            string raw = args.Get("file", null);
            if (string.IsNullOrEmpty(raw)) throw new ToolException("usage", "缺少 --file 参数");
            string full = Guard.FullPath(raw, "--file");
            if (!string.IsNullOrEmpty(workspace))
            {
                workspace = Guard.FullPath(workspace, "--workspace");
                Guard.EnsureInside(workspace, full, "--file");
            }
            if (!File.Exists(full)) throw new ToolException("file_not_found", "文件不存在：" + full);
            string ext = Path.GetExtension(full).ToLowerInvariant();
            if (ext.Length == 0) throw new ToolException("invalid_file", "文件没有扩展名，无法判断类型：" + full);
            return full;
        }
    }
}
