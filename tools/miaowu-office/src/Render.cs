using System;
using System.Collections.Generic;
using System.IO;

namespace MiaowuOffice
{
    /// <summary>把文档渲染成可看的 PDF / PNG。</summary>
    internal static class Render
    {
        public static Jso Run(string file, string outArg, string format, int page, int dpi, int timeoutMs, bool allowRunning)
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            string dir;
            string stem;
            ResolveOut(outArg, file, out dir, out stem);

            string format2 = string.IsNullOrEmpty(format) ? DefaultFormat(ext) : format.Trim().ToLowerInvariant();
            if (format2 != "pdf" && format2 != "png")
            {
                throw new ToolException("usage", "--format 只能是 pdf 或 png");
            }
            if (!Directory.Exists(dir))
            {
                try { Directory.CreateDirectory(dir); }
                catch (Exception ex) { throw new ToolException("bad_path", "无法创建输出目录：" + ex.Message); }
            }

            var result = new Jso();
            result.Add("path", file);
            result.Add("format", format2);
            result.Add("outDir", dir);
            result.Add("dpi", dpi);

            var warnings = new List<object>();
            var pages = new List<object>();
            result.Add("pages", pages);
            string pdfPath = null;
            string delivered = "none";

            if (ext == ".pptx" || ext == ".ppt")
            {
                if (format2 == "pdf")
                {
                    pdfPath = Path.Combine(dir, stem + ".pdf");
                    Ppt.ExportPdf(file, pdfPath, timeoutMs, allowRunning);
                    delivered = "pdf";
                }
                else
                {
                    pages.AddRange(Ppt.RenderPng(file, dir, stem, page, dpi, timeoutMs, allowRunning));
                    delivered = pages.Count > 0 ? "png" : "none";
                }
            }
            else if (ext == ".docx" || ext == ".doc" || ext == ".docm")
            {
                pdfPath = Path.Combine(dir, stem + ".pdf");
                Word.Run(file, true, timeoutMs, allowRunning, delegate (WordContext ctx)
                {
                    Word.ExportPdf(ctx.Doc, pdfPath);
                    return 0;
                });
                result.Add("pdfBytes", new FileInfo(pdfPath).Length);
                delivered = "pdf";

                if (format2 == "png")
                {
                    var rendered = Pdfium.Render(pdfPath, dir, stem, page, dpi);
                    var renderedPages = rendered["pages"] as List<object>;
                    if (renderedPages != null) pages.AddRange(renderedPages);
                    if (rendered["ok"] is bool && (bool)rendered["ok"])
                    {
                        delivered = "png";
                        if (rendered.Has("truncated")) result.Add("truncated", true);
                    }
                    else
                    {
                        warnings.Add(rendered["error"] ?? new Jso().Add("code", "pdf_render_failed").Add("message", "PDF 转 PNG 失败"));
                        result.Add("pdfOnly", true);
                        warnings.Add(new Jso().Add("code", "pdf_only")
                            .Add("message", "Word 只能交付 PDF。想看页面图像，请把 pdfium.dll 放到 exe 同目录（可选依赖）。"));
                    }
                }
            }
            else if (ext == ".xlsx" || ext == ".xls")
            {
                pdfPath = Path.Combine(dir, stem + ".pdf");
                try
                {
                    Excel.ExportPdf(file, pdfPath, Timeouts.Excel, allowRunning);
                    result.Add("pdfBytes", new FileInfo(pdfPath).Length);
                    delivered = "pdf";
                }
                catch (ToolException ex)
                {
                    warnings.Add(new Jso().Add("code", ex.Code).Add("message", ex.Message));
                    warnings.Add(new Jso().Add("code", "excel_pdf_unavailable")
                        .Add("message", "WPS 表格的 PDF 导出不稳定。可以改成把工作表另存为 CSV，或用 WPS 手工导出一次 PDF。"));
                }

                if (format2 == "png" && File.Exists(pdfPath))
                {
                    var rendered = Pdfium.Render(pdfPath, dir, stem, page, dpi);
                    var renderedPages = rendered["pages"] as List<object>;
                    if (renderedPages != null) pages.AddRange(renderedPages);
                    if (rendered["ok"] is bool && (bool)rendered["ok"]) delivered = "png";
                    else
                    {
                        warnings.Add(rendered["error"] ?? new Jso().Add("code", "pdf_render_failed").Add("message", "PDF 转 PNG 失败"));
                        result.Add("pdfOnly", true);
                    }
                }
            }
            else
            {
                throw new ToolException("unsupported", "render 只支持 .docx/.doc、.xlsx/.xls、.pptx/.ppt：" + ext);
            }

            result.Add("delivered", delivered);
            if (pdfPath != null && File.Exists(pdfPath)) result.Add("pdf", pdfPath);
            if (warnings.Count > 0) result.Add("warnings", warnings);
            if (pdfPath != null && !File.Exists(pdfPath) && pages.Count == 0)
            {
                throw new ToolException("render_failed", "没有产生任何输出文件", new Jso().Add("warnings", warnings));
            }
            return result;
        }

        private static string DefaultFormat(string ext)
        {
            if (ext == ".pptx" || ext == ".ppt") return "png";
            return "pdf";
        }

        private static void ResolveOut(string outArg, string file, out string dir, out string stem)
        {
            string fileDir = Path.GetDirectoryName(file);
            if (string.IsNullOrEmpty(outArg))
            {
                dir = fileDir;
                stem = Path.GetFileNameWithoutExtension(file);
                return;
            }
            string full = Guard.FullPath(outArg, "--out");
            if (Directory.Exists(full))
            {
                dir = full;
                stem = Path.GetFileNameWithoutExtension(file);
                return;
            }
            string ext = Path.GetExtension(full).ToLowerInvariant();
            if (ext == ".png" || ext == ".pdf")
            {
                dir = Path.GetDirectoryName(full);
                stem = Path.GetFileNameWithoutExtension(full);
                return;
            }
            dir = full;
            stem = Path.GetFileNameWithoutExtension(file);
        }
    }

    /// <summary>WPS 演示：幻灯片导出 PNG，或整包导出 PDF。</summary>
    internal static class Ppt
    {
        public static List<object> RenderPng(string file, string dir, string stem, int onlyPage, int dpi, int timeoutMs, bool allowRunning)
        {
            ToolException firstError = null;
            try
            {
                // 先试无窗口打开：不打扰用户，也不抢焦点。
                return ExportSlides(file, dir, stem, onlyPage, dpi, timeoutMs, allowRunning, false);
            }
            catch (ToolException ex)
            {
                firstError = ex;
                if (ex.Code == "wps_busy" || ex.Code == "wps_missing" || ex.Code == "wps_timeout") throw;
            }

            try
            {
                // WPS 演示对「不可见 + 无窗口」支持不稳，退一步用可见窗口。
                return ExportSlides(file, dir, stem, onlyPage, dpi, timeoutMs, allowRunning, true);
            }
            catch (ToolException ex)
            {
                if (ex.Code == "wps_busy" || ex.Code == "wps_missing") throw;
                throw new ToolException(ex.Code, ex.Message + "（无窗口模式也失败了：" + firstError.Message + "）", ex.Detail);
            }
        }

        private static List<object> ExportSlides(string file, string dir, string stem, int onlyPage, int dpi, int timeoutMs, bool allowRunning, bool visible)
        {
            Wps.EnsureNotBusy(allowRunning);
            return Wps.RunSta(timeoutMs, delegate ()
            {
                var pages = new List<object>();
                dynamic app = null;
                dynamic pres = null;
                var ownership = new ComOwnership();
                try
                {
                    app = Wps.Create("KWPP.Application", "WPS 演示");
                    ownership.Capture();
                    try { app.Visible = visible ? -1 : 0; } catch { }
                    try { app.DisplayAlerts = 1; } catch { }
                    pres = app.Presentations.Open(file, -1, 0, visible ? -1 : 0);

                    int count = (int)pres.Slides.Count;
                    if (count <= 0) throw new ToolException("ppt_no_slides", "演示文稿里没有幻灯片（可能还没打开完成）");

                    double widthPt = 960.0;
                    double heightPt = 540.0;
                    try { widthPt = (double)pres.PageSetup.SlideWidth; } catch { }
                    try { heightPt = (double)pres.PageSetup.SlideHeight; } catch { }
                    int width = Math.Max(1, (int)Math.Round(widthPt * dpi / 72.0));
                    int height = Math.Max(1, (int)Math.Round(heightPt * dpi / 72.0));

                    int first = onlyPage > 0 ? onlyPage : 1;
                    int last = onlyPage > 0 ? onlyPage : count;
                    if (first > count || last > count)
                    {
                        throw new ToolException("page_out_of_range",
                            "页码超出范围：请求 " + onlyPage.ToString() + "，共 " + count.ToString() + " 页",
                            new Jso().Add("pageCount", count));
                    }
                    if (onlyPage == 0 && last - first + 1 > 40)
                    {
                        last = first + 39;
                    }

                    for (int i = first; i <= last; i++)
                    {
                        string target = Path.Combine(dir, stem + "-p" + i.ToString("00") + ".png");
                        dynamic slide = pres.Slides.Item(i);
                        slide.Export(target, "PNG", width, height);
                        if (!File.Exists(target))
                        {
                            throw new ToolException("ppt_export_failed", "第 " + i.ToString() + " 页没有导出文件：" + target);
                        }
                        var item = new Jso();
                        item.Add("page", i);
                        item.Add("path", target);
                        item.Add("width", width);
                        item.Add("height", height);
                        item.Add("bytes", new FileInfo(target).Length);
                        pages.Add(item);
                    }
                    return pages;
                }
                finally
                {
                    if (pres != null) { try { pres.Close(); } catch { } }
                    Wps.Quit((object)app);
                    Wps.Release((object)pres, (object)app);
                    ownership.Cleanup();
                }
            });
        }

        public static void ExportPdf(string file, string pdfPath, int timeoutMs, bool allowRunning)
        {
            Wps.EnsureNotBusy(allowRunning);
            Wps.RunSta(timeoutMs, delegate ()
            {
                dynamic app = null;
                dynamic pres = null;
                var ownership = new ComOwnership();
                try
                {
                    app = Wps.Create("KWPP.Application", "WPS 演示");
                    ownership.Capture();
                    try { app.Visible = 0; } catch { }
                    try { app.DisplayAlerts = 1; } catch { }
                    pres = app.Presentations.Open(file, -1, 0, 0);
                    try { pres.SaveAs(pdfPath, 32); } // ppSaveAsPDF
                    catch (Exception ex)
                    {
                        throw new ToolException("ppt_pdf_failed", "演示文稿导出 PDF 失败：" + Wps.Friendly(ex));
                    }
                    return 0;
                }
                finally
                {
                    if (pres != null) { try { pres.Close(); } catch { } }
                    Wps.Quit((object)app);
                    Wps.Release((object)pres, (object)app);
                    ownership.Cleanup();
                }
            });

            if (!File.Exists(pdfPath))
            {
                throw new ToolException("ppt_pdf_failed", "演示文稿导出 PDF 没有产生文件：" + pdfPath);
            }
        }
    }

    /// <summary>WPS 表格：整包导出 PDF（尽力而为，失败不阻断其他交付）。</summary>
    internal static class Excel
    {
        public static void ExportPdf(string file, string pdfPath, int timeoutMs, bool allowRunning)
        {
            Wps.EnsureNotBusy(allowRunning);
            Wps.RunSta(timeoutMs, delegate ()
            {
                dynamic app = null;
                dynamic book = null;
                var ownership = new ComOwnership();
                try
                {
                    app = Wps.Create("KET.Application", "WPS 表格");
                    ownership.Capture();
                    try { app.Visible = false; } catch { }
                    try { app.DisplayAlerts = false; } catch { }
                    book = app.Workbooks.Open(file, 0, true);
                    object missing = Type.Missing;
                    try
                    {
                        book.ExportAsFixedFormat(0, pdfPath, missing, missing, missing, missing, missing, missing, missing);
                    }
                    catch (Exception ex)
                    {
                        throw new ToolException("excel_pdf_failed", "表格导出 PDF 失败：" + Wps.Friendly(ex));
                    }
                    return 0;
                }
                finally
                {
                    if (book != null) { try { book.Close(false); } catch { } }
                    Wps.Quit((object)app);
                    Wps.Release((object)book, (object)app);
                    ownership.Cleanup();
                }
            });

            if (!File.Exists(pdfPath))
            {
                throw new ToolException("excel_pdf_failed", "表格导出 PDF 没有产生文件：" + pdfPath);
            }
        }
    }
}
