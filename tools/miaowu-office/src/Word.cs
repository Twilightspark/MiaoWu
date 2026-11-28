using System;
using System.Collections.Generic;
using System.IO;

namespace MiaowuOffice
{
    /// <summary>Word 用的常量（与 Word VBA 枚举值一致，WPS 同样接受）。</summary>
    internal static class Wd
    {
        public const int StatisticWords = 0;
        public const int StatisticLines = 1;
        public const int StatisticPages = 2;
        public const int StatisticCharacters = 3;
        public const int StatisticParagraphs = 4;

        public const int ExportFormatPdf = 17;
        public const int DoNotSaveChanges = 0;
        public const int SaveChanges = -1;

        public const int ActiveEndPageNumber = 3;
        public const int WithInTable = 12;

        public const int ReplaceOne = 1;
        public const int ReplaceAll = 2;
        public const int FindStop = 0;

        public const int LineStyleSingle = 1;
    }

    /// <summary>一次 Word 会话的上下文，仅在 STA 工作线程内有效。</summary>
    internal sealed class WordContext
    {
        public dynamic App;
        public dynamic Doc;
        public string Path;
        public bool ReadOnly;
        public bool Changed;
        public bool Saved;
    }

    /// <summary>Word（.docx / .doc）的打开、检查、导出。</summary>
    internal static class Word
    {
        public static readonly string[] Extensions = new string[] { ".docx", ".doc", ".docm" };

        public static bool Accepts(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            for (int i = 0; i < Extensions.Length; i++) if (Extensions[i] == ext) return true;
            return false;
        }

        /// <summary>启动 WPS 文字、打开文档、执行 work、关闭退出。只返回纯数据，COM 对象不出线程。</summary>
        public static T Run<T>(string path, bool readOnly, int timeoutMs, bool allowRunning, Func<WordContext, T> work)
        {
            Wps.EnsureNotBusy(allowRunning);
            return Wps.RunSta(timeoutMs, delegate ()
            {
                dynamic app = null;
                dynamic doc = null;
                var ownership = new ComOwnership();
                try
                {
                    Trace.Log("word: creating KWPS.Application");
                    app = Wps.Create("KWPS.Application", "WPS 文字");
                    Trace.Log("word: created, capturing pids");
                    ownership.Capture();
                    Trace.Log("word: owned=" + ownership.OwnedCount.ToString());
                    try { app.Visible = false; } catch { }
                    Trace.Log("word: visible=false");
                    try { app.DisplayAlerts = 0; } catch { }
                    Trace.Log("word: open " + path);
                    doc = app.Documents.Open(path, false, readOnly, false);
                    Trace.Log("word: opened");
                    var ctx = new WordContext();
                    ctx.App = app;
                    ctx.Doc = doc;
                    ctx.Path = path;
                    ctx.ReadOnly = readOnly;
                    try
                    {
                        Trace.Log("word: work begin");
                        T value = work(ctx);
                        Trace.Log("word: work end");
                        return value;
                    }
                    finally
                    {
                        Trace.Log("word: close doc");
                        try { doc.Close(Wd.DoNotSaveChanges); } catch { }
                    }
                }
                finally
                {
                    Trace.Log("word: quit");
                    Wps.Quit((object)app);
                    Trace.Log("word: quit done");
                    Wps.Release((object)doc, (object)app);
                    Trace.Log("word: released");
                    int killed;
                    try { killed = ownership.Cleanup(); }
                    catch (Exception cleanupError) { Trace.Log("word: cleanup failed " + cleanupError.Message); killed = -1; }
                    Trace.Log("word: cleanup killed=" + killed.ToString());
                }
            });
        }

        public static string ReadParagraphText(dynamic doc, int index)
        {
            try
            {
                dynamic p = doc.Paragraphs.Item(index);
                return Txt.Flat((string)p.Range.Text);
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>把段落正文换掉，保留段落标记（因此样式、编号、页边距都不变）。</summary>
        public static void SetParagraphText(dynamic doc, int index, string text)
        {
            dynamic p = doc.Paragraphs.Item(index);
            dynamic range = p.Range;
            // Range 末尾带段落标记，先把它排除，否则会把后一段并进来。
            range.MoveEnd(1, -1);
            range.Text = NormalizeInput(text);
        }

        private static string NormalizeInput(string text)
        {
            if (text == null) return string.Empty;
            // 段落内换行用「手动换行符」(0x0B)，避免一行文本被拆成多个段落。
            return text.Replace("\r\n", "\v").Replace("\n", "\v").Replace("\r", "\v");
        }

        public static int ParagraphCount(dynamic doc)
        {
            try { return (int)doc.Paragraphs.Count; }
            catch (Exception ex) { throw new ToolException("wps_error", "读取段落数失败：" + Wps.Friendly(ex)); }
        }

        public static bool StyleExists(dynamic doc, string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            try
            {
                dynamic style = doc.Styles.Item(name);
                return style != null;
            }
            catch { return false; }
        }

        public static List<string> ParagraphStyleNames(dynamic doc)
        {
            var names = new List<string>();
            try
            {
                int count = (int)doc.Styles.Count;
                for (int i = 1; i <= count && names.Count < 80; i++)
                {
                    try
                    {
                        dynamic style = doc.Styles.Item(i);
                        int type = (int)style.Type;
                        bool inUse = false;
                        try { inUse = (bool)style.InUse; } catch { }
                        if (type != 1 || !inUse) continue;
                        string name = (string)style.NameLocal;
                        if (!string.IsNullOrEmpty(name) && !names.Contains(name)) names.Add(name);
                    }
                    catch { }
                }
            }
            catch { }
            return names;
        }

        public static void ExportPdf(dynamic doc, string pdfPath)
        {
            try
            {
                doc.ExportAsFixedFormat(pdfPath, Wd.ExportFormatPdf);
            }
            catch (Exception ex)
            {
                throw new ToolException("pdf_export_failed", "导出 PDF 失败：" + Wps.Friendly(ex),
                    new Jso().Add("path", pdfPath).Add("hint", "确认目标目录可写，且文档没有未处理的密码保护"));
            }
            if (!File.Exists(pdfPath))
            {
                throw new ToolException("pdf_export_failed", "PDF 导出没有产生文件：" + pdfPath);
            }
        }

        // ---------------------------------------------------------------- 检查

        public static Jso Inspect(string path, int from, int max, bool withPages)
        {
            return Run(path, true, Timeouts.Inspect, false, delegate (WordContext ctx)
            {
                return InspectCore(ctx, from, max, withPages);
            });
        }

        private static Jso InspectCore(WordContext ctx, int from, int max, bool withPages)
        {
            dynamic doc = ctx.Doc;
            var info = new Jso();
            var file = new FileInfo(ctx.Path);
            info.Add("path", ctx.Path);
            info.Add("name", file.Name);
            info.Add("extension", file.Extension.ToLowerInvariant());
            info.Add("sizeBytes", file.Length);
            info.Add("modified", file.LastWriteTime.ToString("yyyy-MM-ddTHH:mm:sszzz"));

            info.Add("pageCount", TryInt(delegate { return (int)doc.ComputeStatistics(Wd.StatisticPages); }));
            info.Add("wordCount", TryInt(delegate { return (int)doc.ComputeStatistics(Wd.StatisticWords); }));
            info.Add("charCount", TryInt(delegate { return (int)doc.ComputeStatistics(Wd.StatisticCharacters); }));
            info.Add("paragraphCount", TryInt(delegate { return (int)doc.Paragraphs.Count; }));
            info.Add("tableCount", TryInt(delegate { return (int)doc.Tables.Count; }));
            info.Add("inlineShapeCount", TryInt(delegate { return (int)doc.InlineShapes.Count; }));
            info.Add("shapeCount", TryInt(delegate { return (int)doc.Shapes.Count; }));
            info.Add("fieldCount", TryInt(delegate { return (int)doc.Fields.Count; }));
            info.Add("tocCount", TryInt(delegate { return (int)doc.TablesOfContents.Count; }));
            info.Add("revisionCount", TryInt(delegate { return (int)doc.Revisions.Count; }));
            info.Add("protection", TryInt(delegate { return (int)doc.ProtectionType; }));
            info.Add("hasPassword", TryBool(delegate { return (string)doc.Password != string.Empty; }));

            var props = new Jso();
            AddProperty(props, doc, "Title");
            AddProperty(props, doc, "Subject");
            AddProperty(props, doc, "Author");
            AddProperty(props, doc, "Keywords");
            AddProperty(props, doc, "Comments");
            if (props.Count > 0) info.Add("properties", props);

            // TOC 覆盖的字符区间，用于给段落打 toc 标记。
            var tocRanges = new List<int[]>();
            int tocCount = TryInt(delegate { return (int)doc.TablesOfContents.Count; });
            for (int i = 1; i <= tocCount; i++)
            {
                try
                {
                    dynamic toc = doc.TablesOfContents.Item(i);
                    dynamic range = toc.Range;
                    tocRanges.Add(new int[] { (int)range.Start, (int)range.End });
                }
                catch { }
            }

            int paragraphCount = TryInt(delegate { return (int)doc.Paragraphs.Count; });

            // 表格覆盖的字符区间：段落列表里会包含表格单元格，用 inTable 标出来，避免误改表格内容。
            var tableRanges = new List<int[]>();
            int tableCount = TryInt(delegate { return (int)doc.Tables.Count; });
            for (int i = 1; i <= tableCount && i <= 60; i++)
            {
                try
                {
                    dynamic table = doc.Tables.Item(i);
                    dynamic range = table.Range;
                    tableRanges.Add(new int[] { (int)range.Start, (int)range.End, i });
                }
                catch { }
            }

            int start = Math.Max(1, from);
            int end = Math.Min(paragraphCount, start + Math.Max(1, max) - 1);
            var paragraphs = new List<object>();
            for (int i = start; i <= end; i++)
            {
                var item = ParagraphInfo(doc, i, withPages, tocRanges, tableRanges);
                if (item != null) paragraphs.Add(item);
            }
            info.Add("paragraphs", paragraphs);
            info.Add("paragraphWindow", new Jso().Add("from", start).Add("to", end));
            info.Add("truncated", end < paragraphCount);
            if (end < paragraphCount) info.Add("nextFrom", end + 1);

            if (end < paragraphCount || paragraphCount <= 400)
            {
                info.Add("headings", CollectHeadings(doc, paragraphCount));
            }

            info.Add("styles", ParagraphStyleNames(doc));
            info.Add("tables", CollectTables(doc, 20));

            var outline = new Jso();
            outline.Add("unit", "段落索引从 1 开始；apply 的 index 与这里一致");
            outline.Add("hash", "段落文本的 8 位短哈希；apply 用 expect 传它可以防止改错段落");
            info.Add("outline", outline);

            return info;
        }

        private static object ParagraphInfo(dynamic doc, int index, bool withPages, List<int[]> tocRanges, List<int[]> tableRanges)
        {
            dynamic p;
            string raw;
            try
            {
                p = doc.Paragraphs.Item(index);
                raw = (string)p.Range.Text;
            }
            catch
            {
                return null;
            }

            string text = Txt.Flat(raw);
            int start = TryInt(delegate { return (int)p.Range.Start; });
            int outlineLevel = TryInt(delegate { return (int)p.OutlineLevel; });

            var item = new Jso();
            item.Add("index", index);
            item.Add("style", TryStr(delegate { return (string)p.Style.NameLocal; }));
            item.Add("text", text);
            item.Add("hash", Txt.Hash(text));
            item.Add("chars", text.Length);
            if (outlineLevel >= 1 && outlineLevel <= 9) item.Add("outline", outlineLevel);
            if (text.Length == 0) item.Add("empty", true);

            bool inToc = false;
            for (int i = 0; i < tocRanges.Count; i++)
            {
                if (start >= tocRanges[i][0] && start < tocRanges[i][1]) { inToc = true; break; }
            }
            if (inToc) item.Add("toc", true);
            for (int i = 0; i < tableRanges.Count; i++)
            {
                if (start >= tableRanges[i][0] && start < tableRanges[i][1])
                {
                    item.Add("inTable", true);
                    item.Add("tableIndex", tableRanges[i][2]);
                    break;
                }
            }
            if (Txt.HasField(raw)) item.Add("field", true);

            int listType = TryInt(delegate { return (int)p.Range.ListFormat.ListType; });
            if (listType > 0) item.Add("list", listType);

            if (withPages) item.Add("page", TryInt(delegate { return (int)p.Range.Information(Wd.ActiveEndPageNumber); }));

            return item;
        }

        private static List<object> CollectHeadings(dynamic doc, int paragraphCount)
        {
            var list = new List<object>();
            for (int i = 1; i <= paragraphCount; i++)
            {
                int level = -1;
                string style = null;
                try
                {
                    dynamic p = doc.Paragraphs.Item(i);
                    level = (int)p.OutlineLevel;
                    if (level < 1 || level > 3) continue;
                    style = (string)p.Style.NameLocal;
                }
                catch { continue; }

                string text = Txt.Flat(ReadRawText(doc, i));
                if (text.Length == 0) continue;
                var item = new Jso();
                item.Add("index", i);
                item.Add("level", level);
                item.Add("text", Txt.Preview(text, 120));
                if (!string.IsNullOrEmpty(style)) item.Add("style", style);
                list.Add(item);
                if (list.Count >= 300) break;
            }
            return list;
        }

        private static string ReadRawText(dynamic doc, int index)
        {
            try
            {
                dynamic p = doc.Paragraphs.Item(index);
                return (string)p.Range.Text;
            }
            catch { return string.Empty; }
        }

        private static List<object> CollectTables(dynamic doc, int limit)
        {
            var list = new List<object>();
            int count = TryInt(delegate { return (int)doc.Tables.Count; });
            for (int i = 1; i <= count && i <= limit; i++)
            {
                try
                {
                    dynamic table = doc.Tables.Item(i);
                    var item = new Jso();
                    item.Add("index", i);
                    item.Add("rows", (int)table.Rows.Count);
                    item.Add("columns", (int)table.Columns.Count);
                    string style = TryStr(delegate { return (string)table.Style.NameLocal; });
                    if (!string.IsNullOrEmpty(style)) item.Add("style", style);

                    var header = new List<object>();
                    int cols = Math.Min((int)table.Columns.Count, 8);
                    for (int c = 1; c <= cols; c++)
                    {
                        string cell = string.Empty;
                        try { cell = Txt.Flat((string)table.Cell(1, c).Range.Text); }
                        catch { }
                        header.Add(Txt.Preview(cell, 60));
                    }
                    item.Add("firstRow", header);
                    list.Add(item);
                }
                catch { }
            }
            return list;
        }

        private static void AddProperty(Jso props, dynamic doc, string name)
        {
            try
            {
                dynamic value = doc.BuiltInDocumentProperties.Item(name).Value;
                string text = Convert.ToString(value);
                if (!string.IsNullOrEmpty(text)) props.Add(name, Txt.Preview(text, 200));
            }
            catch { }
        }

        public static int TryInt(Func<int> read)
        {
            try { return read(); }
            catch { return -1; }
        }

        public static bool TryBool(Func<bool> read)
        {
            try { return read(); }
            catch { return false; }
        }

        public static string TryStr(Func<string> read)
        {
            try { return read() ?? string.Empty; }
            catch { return string.Empty; }
        }
    }
}
