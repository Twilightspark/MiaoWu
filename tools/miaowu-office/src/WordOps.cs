using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MiaowuOffice
{
    /// <summary>计划里的一条操作。</summary>
    internal sealed class OpItem
    {
        public int Seq;
        public string Name;
        public Dictionary<string, object> Raw;
        public int Phase;
        public int TargetIndex;
        public bool Resolved;
        public bool Done;
        public string Note;
        public string PdfPath;
        public Jso Result;
    }

    /// <summary>段落文本缓存：一次锚点解析可能扫全文，避免重复 COM 往返。</summary>
    internal sealed class ParaCache
    {
        private readonly dynamic _doc;
        private readonly Dictionary<int, string> _texts = new Dictionary<int, string>();

        public ParaCache(dynamic doc)
        {
            _doc = doc;
            Count = (int)doc.Paragraphs.Count;
        }

        public int Count { get; private set; }

        public string Text(int index)
        {
            string cached;
            if (_texts.TryGetValue(index, out cached)) return cached;
            string text = Word.ReadParagraphText(_doc, index);
            _texts[index] = text;
            return text;
        }
    }

    /// <summary>Word 计划的解析、校验、执行与原子落盘。</summary>
    internal static class WordOps
    {
        internal static readonly string[] KnownOps = new string[]
        {
            "replace_text",
            "replace_paragraph",
            "insert_paragraph_after",
            "insert_paragraph_before",
            "delete_paragraph",
            "set_paragraph_style",
            "insert_table",
            "append_paragraph",
            "update_toc",
            "insert_toc",
            "set_property",
            "export_pdf"
        };

        public static Jso Run(string file, string planPath, bool dryRun, int timeoutMs, bool continueOnError, bool allowRunning, string workspace)
        {
            Dictionary<string, object> plan;
            var loaded = JsonRead.Load(planPath);
            plan = loaded as Dictionary<string, object>;
            if (plan == null)
            {
                // 允许计划文件直接是操作数组。
                var array = loaded as ArrayList;
                if (array == null) throw new ToolException("bad_plan", "计划文件必须是 JSON 对象（含 operations）或操作数组");
                plan = new Dictionary<string, object>();
                plan["operations"] = array;
            }

            var rawOps = JsonRead.List(plan, "operations");
            if (rawOps.Count == 0) throw new ToolException("usage", "计划里没有 operations");
            if (rawOps.Count > 60) throw new ToolException("usage", "一次最多 60 条操作，请拆成多次 apply");

            var ops = new List<OpItem>();
            for (int i = 0; i < rawOps.Count; i++)
            {
                var dict = rawOps[i] as Dictionary<string, object>;
                if (dict == null) throw new ToolException("bad_plan", "第 " + (i + 1).ToString() + " 条操作不是对象");
                ops.Add(BuildOp(i + 1, dict));
            }

            if (dryRun)
            {
                return Word.Run(file, true, timeoutMs, allowRunning, delegate (WordContext ctx)
                {
                    var cache = new ParaCache(ctx.Doc);
                    ResolveAll(ctx, cache, ops, true, workspace);
                    return BuildResult(ctx, ops, file, true, null, null);
                });
            }

            string temp = Files.TempPath(file);
            try
            {
                File.Copy(file, temp, true);
            }
            catch (Exception ex)
            {
                throw new ToolException("file_locked", "无法创建临时副本（文件可能被占用）：" + ex.Message,
                    new Jso().Add("file", file).Add("hint", "请关闭正在打开该文档的程序后重试"));
            }

            Jso result = null;
            try
            {
                result = Word.Run(temp, false, timeoutMs, allowRunning, delegate (WordContext ctx)
                {
                    var cache = new ParaCache(ctx.Doc);
                    ResolveAll(ctx, cache, ops, false, workspace);
                    ExecuteAll(ctx, cache, ops, continueOnError);
                    if (ctx.Changed && !ctx.Saved)
                    {
                        try
                        {
                            ctx.Doc.Save();
                            ctx.Saved = true;
                        }
                        catch (Exception ex)
                        {
                            throw new ToolException("save_failed", "保存临时副本失败：" + Wps.Friendly(ex),
                                new Jso().Add("hint", "文档可能被密码保护或只读"));
                        }
                    }
                    return BuildResult(ctx, ops, file, false, null, null);
                });
            }
            catch
            {
                Files.TryDelete(temp);
                throw;
            }

            if (!string.IsNullOrEmpty(temp) && File.Exists(temp))
            {
                if (Ooxml.IsOoxml(file))
                {
                    var check = Ooxml.Validate(temp);
                    result.Add("validation", check);
                    if (!(bool)check["ok"])
                    {
                        Files.TryDelete(temp);
                        throw new ToolException("validation_failed",
                            "写回前的完整性校验没通过，已放弃本次修改（原文件未动）。",
                            new Jso().Add("validation", check).Add("hint", "把计划拆小后重试；若必现，请改用 WPS 手工保存一次再试"));
                    }
                }

                long before = new FileInfo(file).Length;
                string backup = Files.BackupPath(file);
                Files.ReplaceWithBackup(temp, file, backup);
                result.Add("backup", backup);
                result.Add("sizeBefore", before);
                result.Add("sizeAfter", new FileInfo(file).Length);
            }

            return result;
        }

        private static OpItem BuildOp(int seq, Dictionary<string, object> dict)
        {
            string name = JsonRead.Str(dict, "op", null);
            if (string.IsNullOrEmpty(name))
            {
                name = JsonRead.Str(dict, "action", null);
            }
            if (string.IsNullOrEmpty(name)) throw new ToolException("bad_plan", "第 " + seq.ToString() + " 条操作缺少 op 字段");
            name = name.Trim().ToLowerInvariant();
            if (!KnownOps.Contains(name))
            {
                throw new ToolException("unsupported_op", "不支持的 op：" + name,
                    new Jso().Add("supported", KnownOps));
            }

            var op = new OpItem();
            op.Seq = seq;
            op.Name = name;
            op.Raw = dict;
            op.Phase = PhaseOf(name);
            op.Result = new Jso().Add("seq", seq).Add("op", name);
            return op;
        }

        private static int PhaseOf(string name)
        {
            switch (name)
            {
                case "replace_text":
                    return 1;
                case "update_toc":
                case "insert_toc":
                    return 3;
                case "append_paragraph":
                    return 4;
                case "set_property":
                    return 5;
                case "export_pdf":
                    return 6;
                default:
                    return 1;
            }
        }

        private static bool NeedsTarget(string name)
        {
            switch (name)
            {
                case "replace_paragraph":
                case "insert_paragraph_after":
                case "insert_paragraph_before":
                case "delete_paragraph":
                case "set_paragraph_style":
                case "insert_table":
                case "insert_toc":
                    return true;
                default:
                    return false;
            }
        }

        // ------------------------------------------------------------- 解析阶段

        private static void ResolveAll(WordContext ctx, ParaCache cache, List<OpItem> ops, bool dryRun, string workspace)
        {
            for (int i = 0; i < ops.Count; i++)
            {
                OpItem op = ops[i];
                try
                {
                    ResolveOne(ctx, cache, op, dryRun, workspace);
                    if (dryRun)
                    {
                        op.Result.Add("ok", true);
                        op.Result.Add("wouldApply", true);
                    }
                }
                catch (ToolException ex)
                {
                    op.Result.Add("ok", false).Add("error", ErrorObject(ex));
                    throw;
                }
            }
        }

        private static void ResolveOne(WordContext ctx, ParaCache cache, OpItem op, bool dryRun, string workspace)
        {
            if (NeedsTarget(op.Name))
            {
                op.TargetIndex = ResolveIndex(cache, op);
                op.Resolved = true;
                op.Result.Add("target", new Jso()
                    .Add("index", op.TargetIndex)
                    .Add("text", Txt.Preview(cache.Text(op.TargetIndex), 100)));
            }
            else if (op.Name == "replace_text" && (JsonRead.Has(op.Raw, "index") || JsonRead.Has(op.Raw, "anchor")))
            {
                op.TargetIndex = ResolveIndex(cache, op);
                op.Resolved = true;
                op.Result.Add("target", new Jso()
                    .Add("index", op.TargetIndex)
                    .Add("text", Txt.Preview(cache.Text(op.TargetIndex), 100)));
            }

            switch (op.Name)
            {
                case "replace_text":
                    ValidateFind(op);
                    break;
                case "replace_paragraph":
                    RequireText(op);
                    break;
                case "insert_paragraph_after":
                case "insert_paragraph_before":
                case "append_paragraph":
                    RequireText(op);
                    ValidateStyle(ctx, op);
                    break;
                case "set_paragraph_style":
                    if (string.IsNullOrEmpty(JsonRead.Str(op.Raw, "style", null)))
                        throw new ToolException("bad_plan", "第 " + op.Seq.ToString() + " 条 set_paragraph_style 缺少 style");
                    ValidateStyle(ctx, op);
                    break;
                case "insert_table":
                    ValidateTable(op);
                    break;
                case "export_pdf":
                    op.PdfPath = ResolvePdfPath(ctx, op, workspace);
                    break;
                case "set_property":
                    if (string.IsNullOrEmpty(JsonRead.Str(op.Raw, "name", null)))
                        throw new ToolException("bad_plan", "第 " + op.Seq.ToString() + " 条 set_property 缺少 name");
                    break;
            }
        }

        private static int ResolveIndex(ParaCache cache, OpItem op)
        {
            int index;
            if (JsonRead.Has(op.Raw, "index"))
            {
                index = JsonRead.Int(op.Raw, "index", 0);
                if (index < 1 || index > cache.Count)
                {
                    throw new ToolException("index_out_of_range",
                        "段落索引 " + index.ToString() + " 超出范围（当前共 " + cache.Count.ToString() + " 段）",
                        new Jso().Add("index", index).Add("paragraphCount", cache.Count));
                }
            }
            else
            {
                string anchor = JsonRead.Str(op.Raw, "anchor", null);
                if (string.IsNullOrEmpty(anchor))
                {
                    throw new ToolException("bad_plan",
                        "第 " + op.Seq.ToString() + " 条操作需要 index 或 anchor 指定目标段落");
                }
                string mode = JsonRead.Str(op.Raw, "anchorMode", "contains").ToLowerInvariant();
                int occurrence = Math.Max(1, JsonRead.Int(op.Raw, "occurrence", 1));
                int matches = 0;
                int hit = 0;
                for (int i = 1; i <= cache.Count; i++)
                {
                    string text = cache.Text(i);
                    bool ok = mode == "exact"
                        ? string.Equals(text, anchor, StringComparison.Ordinal)
                        : text.IndexOf(anchor, StringComparison.Ordinal) >= 0;
                    if (!ok) continue;
                    matches++;
                    if (matches == occurrence) { hit = i; break; }
                }
                if (hit == 0)
                {
                    throw new ToolException("anchor_not_found",
                        "没有找到第 " + occurrence.ToString() + " 个包含「" + Txt.Preview(anchor, 40) + "」的段落",
                        new Jso().Add("anchor", anchor).Add("occurrence", occurrence).Add("matches", matches)
                            .Add("hint", "先用 inspect 看 paragraph 的 index 与 text，再改用 index 精准指定"));
                }
                index = hit;
            }

            string expect = JsonRead.Str(op.Raw, "expect", null);
            if (!string.IsNullOrEmpty(expect))
            {
                string actual = Txt.Hash(cache.Text(index));
                if (!string.Equals(expect, actual, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ToolException("anchor_changed",
                        "第 " + op.Seq.ToString() + " 条操作的目标段落内容已变化（expect=" + expect + "，实际=" + actual + "）",
                        new Jso().Add("index", index).Add("expect", expect).Add("actual", actual)
                            .Add("text", Txt.Preview(cache.Text(index), 120))
                            .Add("hint", "重新 inspect 后按最新 hash 生成计划"));
                }
            }
            return index;
        }

        private static void ValidateFind(OpItem op)
        {
            string find = JsonRead.Str(op.Raw, "find", null);
            if (string.IsNullOrEmpty(find))
            {
                throw new ToolException("bad_plan", "第 " + op.Seq.ToString() + " 条 replace_text 缺少 find");
            }
            if (!JsonRead.Has(op.Raw, "replace")) throw new ToolException("bad_plan", "第 " + op.Seq.ToString() + " 条 replace_text 缺少 replace");
            string strategy = JsonRead.Str(op.Raw, "strategy", "find").ToLowerInvariant();
            if (strategy != "find" && strategy != "paragraph")
            {
                throw new ToolException("bad_plan", "strategy 只能是 find 或 paragraph");
            }
        }

        private static void RequireText(OpItem op)
        {
            if (!JsonRead.Has(op.Raw, "text"))
            {
                throw new ToolException("bad_plan", "第 " + op.Seq.ToString() + " 条 " + op.Name + " 缺少 text");
            }
        }

        private static void ValidateStyle(WordContext ctx, OpItem op)
        {
            string style = JsonRead.Str(op.Raw, "style", null);
            if (string.IsNullOrEmpty(style)) return;
            if (!Word.StyleExists(ctx.Doc, style))
            {
                throw new ToolException("style_not_found",
                    "样式不存在：" + style,
                    new Jso().Add("style", style).Add("available", Word.ParagraphStyleNames(ctx.Doc))
                        .Add("hint", "Style 名要用中文版 WPS 的本地名，例如「标题 1」「正文」"));
            }
        }

        private static void ValidateTable(OpItem op)
        {
            var rows = JsonRead.List(op.Raw, "rows");
            if (rows.Count == 0) throw new ToolException("bad_plan", "第 " + op.Seq.ToString() + " 条 insert_table 缺少 rows");
            if (rows.Count > 60) throw new ToolException("usage", "表格最多 60 行（低配机器限制），需要更长请拆成多次插入");
            int cols = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                int c = CellList(rows[i]).Count;
                if (c > cols) cols = c;
            }
            if (cols == 0) throw new ToolException("bad_plan", "insert_table 的 rows 里没有单元格");
            if (cols > 12) throw new ToolException("usage", "表格最多 12 列，需要更宽请拆表");
        }

        private static string ResolvePdfPath(WordContext ctx, OpItem op, string workspace)
        {
            string target = JsonRead.Str(op.Raw, "path", null);
            string dir = Path.GetDirectoryName(ctx.Path);
            string pdf;
            if (string.IsNullOrEmpty(target))
            {
                pdf = Path.Combine(dir, Path.GetFileNameWithoutExtension(ctx.Path) + ".pdf");
            }
            else
            {
                pdf = Path.IsPathRooted(target) ? target : Path.Combine(dir, target);
            }
            string resolved = Guard.FullPath(pdf, "export_pdf.path");
            Guard.EnsureInside(workspace, resolved, "export_pdf.path");
            return resolved;
        }

        private static List<object> CellList(object row)
        {
            var list = row as ArrayList;
            if (list != null) return new List<object>(list.ToArray());
            var arr = row as object[];
            if (arr != null) return new List<object>(arr);
            var l = row as List<object>;
            if (l != null) return l;
            return new List<object>();
        }

        // ------------------------------------------------------------- 执行阶段

        private static void ExecuteAll(WordContext ctx, ParaCache cache, List<OpItem> ops, bool continueOnError)
        {
            var sorted = ops.OrderBy(delegate (OpItem o) { return o.Phase; })
                            .ThenByDescending(delegate (OpItem o) { return o.TargetIndex; })
                            .ThenBy(delegate (OpItem o) { return o.Seq; })
                            .ToList();

            // 阶段 6 的 export_pdf 永远最后，避免导出到一半的文档。
            foreach (OpItem op in sorted)
            {
                try
                {
                    ExecuteOne(ctx, op);
                    op.Done = true;
                    op.Result.Add("ok", true);
                    if (!string.IsNullOrEmpty(op.Note)) op.Result.Add("note", op.Note);
                }
                catch (ToolException ex)
                {
                    op.Result.Add("ok", false).Add("error", ErrorObject(ex));
                    if (!continueOnError) throw;
                }
                catch (Exception ex)
                {
                    var wrapped = new ToolException("wps_error", Wps.Friendly(ex), Wps.ErrorDetail(ex));
                    op.Result.Add("ok", false).Add("error", ErrorObject(wrapped));
                    if (!continueOnError) throw wrapped;
                }
            }
        }

        private static void ExecuteOne(WordContext ctx, OpItem op)
        {
            dynamic doc = ctx.Doc;
            switch (op.Name)
            {
                case "replace_text":
                    DoReplaceText(ctx, op);
                    break;
                case "replace_paragraph":
                    DoReplaceParagraph(ctx, op);
                    break;
                case "insert_paragraph_after":
                    DoInsertParagraph(ctx, op, true);
                    break;
                case "insert_paragraph_before":
                    DoInsertParagraph(ctx, op, false);
                    break;
                case "delete_paragraph":
                    DoDeleteParagraph(ctx, op);
                    break;
                case "set_paragraph_style":
                    DoSetStyle(ctx, op);
                    break;
                case "insert_table":
                    DoInsertTable(ctx, op);
                    break;
                case "append_paragraph":
                    DoAppendParagraph(ctx, op);
                    break;
                case "update_toc":
                    DoUpdateToc(ctx, op);
                    break;
                case "insert_toc":
                    DoInsertToc(ctx, op);
                    break;
                case "set_property":
                    DoSetProperty(ctx, op);
                    break;
                case "export_pdf":
                    DoExportPdf(ctx, op);
                    break;
                default:
                    throw new ToolException("unsupported_op", "不支持的 op：" + op.Name);
            }
        }

        private static void DoReplaceParagraph(WordContext ctx, OpItem op)
        {
            int index = op.TargetIndex;
            string before = Word.ReadParagraphText(ctx.Doc, index);
            string text = JsonRead.Str(op.Raw, "text", string.Empty);
            Word.SetParagraphText(ctx.Doc, index, text);
            ctx.Changed = true;
            op.Result.Add("target", new Jso().Add("index", index).Add("hashBefore", Txt.Hash(before)).Add("textBefore", Txt.Preview(before, 100)));
            op.Result.Add("text", Txt.Preview(Txt.Flat(text), 200));

            string style = JsonRead.Str(op.Raw, "style", null);
            if (!string.IsNullOrEmpty(style))
            {
                try { ctx.Doc.Paragraphs.Item(index).Style = style; }
                catch (Exception ex) { op.Note = "样式设置失败：" + Wps.Friendly(ex); }
            }
        }

        private static void DoInsertParagraph(WordContext ctx, OpItem op, bool after)
        {
            int index = op.TargetIndex;
            dynamic doc = ctx.Doc;
            dynamic paragraph = doc.Paragraphs.Item(index);
            dynamic range = paragraph.Range;
            if (after) range.InsertParagraphAfter();
            else range.InsertParagraphBefore();

            int newIndex = after ? index + 1 : index;
            string text = JsonRead.Str(op.Raw, "text", string.Empty);
            Word.SetParagraphText(doc, newIndex, text);

            string style = JsonRead.Str(op.Raw, "style", null);
            if (!string.IsNullOrEmpty(style))
            {
                try { doc.Paragraphs.Item(newIndex).Style = style; }
                catch (Exception ex) { op.Note = "样式设置失败：" + Wps.Friendly(ex); }
            }
            else
            {
                // 默认继承锚点段落的样式（新段落通常已自动继承，这里显式兜底一次）。
                string anchorStyle = Word.TryStr(delegate { return (string)doc.Paragraphs.Item(index).Style.NameLocal; });
                if (!string.IsNullOrEmpty(anchorStyle))
                {
                    try { doc.Paragraphs.Item(newIndex).Style = anchorStyle; } catch { }
                }
            }

            ctx.Changed = true;
            op.Result.Add("target", new Jso().Add("index", newIndex).Add("afterIndex", index));
            op.Result.Add("text", Txt.Preview(Txt.Flat(text), 200));
        }

        private static void DoDeleteParagraph(WordContext ctx, OpItem op)
        {
            int index = op.TargetIndex;
            dynamic doc = ctx.Doc;
            int before = Word.ParagraphCount(doc);
            if (before <= 1) throw new ToolException("last_paragraph", "文档只剩一个段落，不能再删除");

            string text = Word.ReadParagraphText(doc, index);
            dynamic range = doc.Paragraphs.Item(index).Range;
            range.Delete();

            int after = Word.ParagraphCount(doc);
            if (after >= before)
            {
                // 末段无法整段删除时退化为清空正文。
                try { Word.SetParagraphText(doc, Math.Min(index, after), string.Empty); } catch { }
                op.Note = "末段不能整段删除，已清空该段正文";
            }
            ctx.Changed = true;
            op.Result.Add("target", new Jso().Add("index", index).Add("textBefore", Txt.Preview(text, 100)));
            op.Result.Add("paragraphCountBefore", before);
            op.Result.Add("paragraphCountAfter", after);
        }

        private static void DoSetStyle(WordContext ctx, OpItem op)
        {
            int index = op.TargetIndex;
            string style = JsonRead.Str(op.Raw, "style", null);
            string before = Word.TryStr(delegate { return (string)ctx.Doc.Paragraphs.Item(index).Style.NameLocal; });
            try
            {
                ctx.Doc.Paragraphs.Item(index).Style = style;
            }
            catch (Exception ex)
            {
                throw new ToolException("style_not_found", "设置样式失败：" + style + "，" + Wps.Friendly(ex),
                    new Jso().Add("style", style));
            }
            ctx.Changed = true;
            op.Result.Add("target", new Jso().Add("index", index).Add("styleBefore", before).Add("styleAfter", style));
        }

        private static void DoInsertTable(WordContext ctx, OpItem op)
        {
            int index = op.TargetIndex;
            dynamic doc = ctx.Doc;
            var rows = JsonRead.List(op.Raw, "rows");
            int rowCount = rows.Count;
            int colCount = 0;
            for (int i = 0; i < rowCount; i++) colCount = Math.Max(colCount, CellList(rows[i]).Count);

            dynamic paragraph = doc.Paragraphs.Item(index);
            paragraph.Range.InsertParagraphAfter();
            int anchorIndex = index + 1;

            dynamic table;
            try
            {
                table = doc.Tables.Add(doc.Paragraphs.Item(anchorIndex).Range, rowCount, colCount);
            }
            catch (Exception ex)
            {
                throw new ToolException("table_insert_failed", "插入表格失败：" + Wps.Friendly(ex),
                    new Jso().Add("index", index).Add("rows", rowCount).Add("columns", colCount));
            }

            for (int r = 0; r < rowCount; r++)
            {
                var cells = CellList(rows[r]);
                for (int c = 0; c < colCount; c++)
                {
                    string value = c < cells.Count ? Convert.ToString(cells[c]) : string.Empty;
                    try { table.Cell(r + 1, c + 1).Range.Text = (value ?? string.Empty).Replace("\r\n", "\v").Replace("\n", "\v"); }
                    catch { }
                }
            }

            bool header = JsonRead.Bool(op.Raw, "header", true);
            if (header)
            {
                try { table.Rows.Item(1).Range.Font.Bold = true; } catch { }
                try { table.Rows.Item(1).HeadingFormat = -1; } catch { }
            }

            string tableStyle = JsonRead.Str(op.Raw, "style", null);
            if (!string.IsNullOrEmpty(tableStyle))
            {
                try { table.Style = tableStyle; }
                catch (Exception ex) { op.Note = "表格样式设置失败：" + Wps.Friendly(ex); }
            }
            try
            {
                table.Borders.InsideLineStyle = Wd.LineStyleSingle;
                table.Borders.OutsideLineStyle = Wd.LineStyleSingle;
            }
            catch { }

            // 文档以表格结尾时补一个空段落，否则 Word/WPS 再打开会报结构问题。
            try
            {
                int count = (int)doc.Paragraphs.Count;
                dynamic last = doc.Paragraphs.Item(count);
                if ((int)last.Range.Tables.Count > 0) last.Range.InsertParagraphAfter();
            }
            catch { }

            ctx.Changed = true;
            op.Result.Add("target", new Jso().Add("index", anchorIndex).Add("rows", rowCount).Add("columns", colCount));
        }

        private static void DoAppendParagraph(WordContext ctx, OpItem op)
        {
            dynamic doc = ctx.Doc;
            dynamic paragraph = doc.Content.Paragraphs.Add();
            int index = (int)doc.Paragraphs.Count;
            string text = JsonRead.Str(op.Raw, "text", string.Empty);
            Word.SetParagraphText(doc, index, text);

            string style = JsonRead.Str(op.Raw, "style", null);
            if (!string.IsNullOrEmpty(style))
            {
                try { paragraph.Style = style; }
                catch (Exception ex) { op.Note = "样式设置失败：" + Wps.Friendly(ex); }
            }
            ctx.Changed = true;
            op.Result.Add("target", new Jso().Add("index", index));
            op.Result.Add("text", Txt.Preview(Txt.Flat(text), 200));
        }

        private static void DoReplaceText(WordContext ctx, OpItem op)
        {
            string find = JsonRead.Str(op.Raw, "find", null);
            string replace = JsonRead.Str(op.Raw, "replace", string.Empty) ?? string.Empty;
            bool matchCase = JsonRead.Bool(op.Raw, "caseSensitive", false);
            bool all = JsonRead.Bool(op.Raw, "all", true);
            string strategy = JsonRead.Str(op.Raw, "strategy", "find").ToLowerInvariant();

            int replaced;
            if (strategy == "paragraph")
            {
                replaced = ReplaceByParagraph(ctx, op, find, replace, all);
            }
            else
            {
                dynamic range = op.TargetIndex > 0 ? ctx.Doc.Paragraphs.Item(op.TargetIndex).Range : ctx.Doc.Content;
                replaced = FindReplace(range, find, replace, matchCase, all);
                if (replaced < 0)
                {
                    op.Note = "替换文本里包含查找文本，无法逐次计数，只做了整批替换";
                    replaced = 1;
                }
            }

            if (replaced > 0) ctx.Changed = true;
            op.Result.Add("replaced", replaced);
            if (op.TargetIndex > 0) op.Result.Add("scope", new Jso().Add("index", op.TargetIndex));
            else op.Result.Add("scope", "document");
            op.Result.Add("find", Txt.Preview(find, 60));
            op.Result.Add("replace", Txt.Preview(replace, 60));
            if (replaced == 0) op.Result.Add("warning", "没有找到要替换的内容");
        }

        private static int FindReplace(dynamic range, string find, string replace, bool matchCase, bool all)
        {
            dynamic finder = range.Find;
            try { finder.ClearFormatting(); } catch { }
            try { finder.Replacement.ClearFormatting(); } catch { }

            object missing = Type.Missing;
            bool reruns = all && replace.IndexOf(find, StringComparison.Ordinal) >= 0;
            int count = 0;
            int limit = all ? 5000 : 1;

            while (count < limit)
            {
                bool hit;
                try
                {
                    hit = (bool)finder.Execute(find, matchCase, false, false, false, false, true, 0, false, replace,
                        Wd.ReplaceOne, missing, missing, missing, missing);
                }
                catch (Exception ex)
                {
                    throw new ToolException("replace_failed", "查找替换失败：" + Wps.Friendly(ex),
                        new Jso().Add("hint", "WPS 的 Find.Execute 参数不兼容时可改用 \"strategy\":\"paragraph\"（会丢失段内局部格式）"));
                }
                if (!hit) break;
                count++;
                if (reruns) break;
            }

            if (reruns)
            {
                // 替换串包含查找串（如 a→aa），逐次会无限循环，改为整批替换。
                try
                {
                    bool hit = (bool)finder.Execute(find, matchCase, false, false, false, false, true, 0, false, replace,
                        Wd.ReplaceAll, missing, missing, missing, missing);
                    return hit ? -1 : 0;
                }
                catch (Exception ex)
                {
                    throw new ToolException("replace_failed", "整批替换失败：" + Wps.Friendly(ex));
                }
            }
            return count;
        }

        private static int ReplaceByParagraph(WordContext ctx, OpItem op, string find, string replace, bool all)
        {
            dynamic doc = ctx.Doc;
            int total = 0;
            if (op.TargetIndex > 0)
            {
                total = ReplaceInParagraph(doc, op.TargetIndex, find, replace, all);
            }
            else
            {
                int count = (int)doc.Paragraphs.Count;
                for (int i = 1; i <= count; i++)
                {
                    total += ReplaceInParagraph(doc, i, find, replace, all);
                    if (!all && total > 0) break;
                }
            }
            if (total > 0) op.Note = "按段落整体替换，段内的局部加粗/颜色会丢失";
            return total;
        }

        private static int ReplaceInParagraph(dynamic doc, int index, string find, string replace, bool all)
        {
            string text = Word.ReadParagraphText(doc, index);
            if (text.Length == 0 || text.IndexOf(find, StringComparison.Ordinal) < 0) return 0;
            string next = all ? text.Replace(find, replace) : ReplaceFirst(text, find, replace);
            int hits = CountOccurrences(text, find);
            if (!all) hits = 1;
            Word.SetParagraphText(doc, index, next);
            return hits;
        }

        private static string ReplaceFirst(string text, string find, string replace)
        {
            int at = text.IndexOf(find, StringComparison.Ordinal);
            if (at < 0) return text;
            return text.Substring(0, at) + replace + text.Substring(at + find.Length);
        }

        private static int CountOccurrences(string text, string find)
        {
            if (string.IsNullOrEmpty(find)) return 0;
            int count = 0;
            int at = 0;
            while ((at = text.IndexOf(find, at, StringComparison.Ordinal)) >= 0)
            {
                count++;
                at += find.Length;
                if (count > 5000) break;
            }
            return count;
        }

        private static void DoUpdateToc(WordContext ctx, OpItem op)
        {
            dynamic doc = ctx.Doc;
            int tocCount = Word.TryInt(delegate { return (int)doc.TablesOfContents.Count; });
            int updated = 0;
            for (int i = 1; i <= tocCount; i++)
            {
                try
                {
                    dynamic toc = doc.TablesOfContents.Item(i);
                    toc.Update();
                    updated++;
                }
                catch { }
            }
            try { doc.Fields.Update(); } catch { }
            try { doc.Repaginate(); } catch { }
            ctx.Changed = true;
            op.Result.Add("tocCount", tocCount);
            op.Result.Add("updated", updated);
            if (tocCount == 0) op.Result.Add("warning", "文档里没有目录域；如需插入目录请用 insert_toc");
        }

        private static void DoInsertToc(WordContext ctx, OpItem op)
        {
            dynamic doc = ctx.Doc;
            int upper = Math.Max(1, Math.Min(9, JsonRead.Int(op.Raw, "levels", 3)));
            int index = op.TargetIndex > 0 ? op.TargetIndex : 1;
            dynamic range = doc.Paragraphs.Item(index).Range;
            range.Collapse(1);
            dynamic toc = doc.TablesOfContents.Add(range, true, 1, upper);
            try { toc.Update(); } catch { }
            try { doc.Repaginate(); } catch { }
            ctx.Changed = true;
            op.Result.Add("target", new Jso().Add("index", index).Add("levels", upper));
        }

        private static void DoSetProperty(WordContext ctx, OpItem op)
        {
            dynamic doc = ctx.Doc;
            string name = JsonRead.Str(op.Raw, "name", null);
            string value = JsonRead.Str(op.Raw, "value", string.Empty) ?? string.Empty;
            try
            {
                doc.BuiltInDocumentProperties.Item(name).Value = value;
            }
            catch (Exception ex)
            {
                throw new ToolException("property_failed",
                    "设置文档属性失败：" + name + "，" + Wps.Friendly(ex),
                    new Jso().Add("name", name).Add("supported", new string[] { "Title", "Subject", "Author", "Keywords", "Comments", "Company", "Category" }));
            }
            ctx.Changed = true;
            op.Result.Add("name", name);
            op.Result.Add("value", Txt.Preview(value, 120));
        }

        private static void DoExportPdf(WordContext ctx, OpItem op)
        {
            string pdf = op.PdfPath;
            if (string.IsNullOrEmpty(pdf)) pdf = ResolvePdfPath(ctx, op, null);
            string dir = Path.GetDirectoryName(pdf);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            Word.ExportPdf(ctx.Doc, pdf);
            op.Result.Add("path", pdf);
            op.Result.Add("bytes", new FileInfo(pdf).Length);
        }

        // ------------------------------------------------------------- 结果汇总

        private static Jso BuildResult(WordContext ctx, List<OpItem> ops, string file, bool dryRun, string backup, Jso validation)
        {
            var result = new Jso();
            result.Add("path", file);
            result.Add("dryRun", dryRun);
            result.Add("changed", ctx.Changed);
            result.Add("saved", ctx.Saved);

            var list = new List<object>();
            int applied = 0;
            int failed = 0;
            int resolved = 0;
            foreach (OpItem op in ops.OrderBy(delegate (OpItem o) { return o.Seq; }))
            {
                if (op.Done) applied++;
                if (op.Resolved) resolved++;
                if (op.Result["ok"] is bool && !(bool)op.Result["ok"]) failed++;
                list.Add(op.Result);
            }
            result.Add("operations", list);
            result.Add("summary", new Jso().Add("total", ops.Count).Add("applied", applied).Add("resolved", resolved).Add("failed", failed));

            if (dryRun)
            {
                result.Add("note", "dryRun 只做了目标解析与参数校验，没有修改文件");
            }
            if (backup != null) result.Add("backup", backup);
            if (validation != null) result.Add("validation", validation);
            return result;
        }

        internal static Jso ErrorObject(ToolException ex)
        {
            var error = new Jso().Add("code", ex.Code).Add("message", ex.Message);
            if (ex.Detail != null) error.Add("detail", ex.Detail);
            return error;
        }
    }
}
