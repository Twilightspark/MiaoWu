using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MiaowuOffice
{
    /// <summary>
    /// 可选依赖：把 PDF 渲染成 PNG 需要 exe 同目录下的 pdfium.dll（BSD-3-Clause）。
    /// 没有这个 DLL 时 Word/Excel 只能交付 PDF，`render` 会在 warnings 里说明。
    /// </summary>
    internal static class Pdfium
    {
        private const string Dll = "pdfium.dll";

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDF_InitLibrary();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDF_DestroyLibrary();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDF_LoadDocument(byte[] filePath, byte[] password);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDF_CloseDocument(IntPtr document);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDF_GetPageCount(IntPtr document);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDF_LoadPage(IntPtr document, int pageIndex);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDF_ClosePage(IntPtr page);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern float FPDF_GetPageWidthF(IntPtr page);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern float FPDF_GetPageHeightF(IntPtr page);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDFBitmap_Create(int width, int height, int alpha);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDFBitmap_GetStride(IntPtr bitmap);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDFBitmap_Destroy(IntPtr bitmap);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint FPDF_GetLastError();

        private const int FPDF_ANNOT = 0x01;
        private const int FPDF_LCD_TEXT = 0x02;

        public static string DllPath()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string candidate = Path.Combine(baseDir, "pdfium.dll");
                if (File.Exists(candidate)) return candidate;
                string env = Environment.GetEnvironmentVariable("MIAOWU_OFFICE_PDFIUM");
                if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
            }
            catch { }
            return null;
        }

        public static bool Available()
        {
            return DllPath() != null;
        }

        /// <summary>把 PDF 的指定页（0 表示全部）渲染成 PNG。返回 {ok, pages:[{page,path,width,height,bytes}], error}。</summary>
        public static Jso Render(string pdfPath, string dir, string stem, int onlyPage, int dpi)
        {
            var result = new Jso();
            var pages = new List<object>();
            result.Add("pages", pages);

            string dll = DllPath();
            if (dll == null)
            {
                result.Add("ok", false);
                result.Add("error", new Jso().Add("code", "pdfium_missing")
                    .Add("message", "exe 同目录没有 pdfium.dll，无法把 PDF 转成 PNG"));
                return result;
            }

            IntPtr document = IntPtr.Zero;
            try
            {
                FPDF_InitLibrary();
            }
            catch (Exception ex)
            {
                result.Add("ok", false);
                result.Add("error", new Jso().Add("code", "pdfium_load_failed")
                    .Add("message", "加载 pdfium.dll 失败（可能是 32 位 DLL 配 64 位程序）：" + ex.Message)
                    .Add("path", dll));
                return result;
            }

            try
            {
                byte[] pathBytes = Encoding.UTF8.GetBytes(pdfPath + "\0");
                document = FPDF_LoadDocument(pathBytes, null);
                if (document == IntPtr.Zero)
                {
                    result.Add("ok", false);
                    result.Add("error", new Jso().Add("code", "pdf_open_failed")
                        .Add("message", "pdfium 打开 PDF 失败，错误码 " + FPDF_GetLastError().ToString())
                        .Add("path", pdfPath));
                    return result;
                }

                int pageCount = FPDF_GetPageCount(document);
                result.Add("pageCount", pageCount);

                int first = onlyPage > 0 ? onlyPage : 1;
                int last = onlyPage > 0 ? onlyPage : pageCount;
                if (first > pageCount || last > pageCount)
                {
                    result.Add("ok", false);
                    result.Add("error", new Jso().Add("code", "page_out_of_range")
                        .Add("message", "页码超出范围：请求 " + onlyPage.ToString() + "，共 " + pageCount.ToString() + " 页"));
                    return result;
                }
                if (last - first + 1 > 30 && onlyPage == 0)
                {
                    last = first + 29;
                    result.Add("truncated", true);
                    result.Add("warning", "一次最多转 30 页，已截断；需要后面几页请用 --page 指定");
                }

                for (int pageNo = first; pageNo <= last; pageNo++)
                {
                    IntPtr page = FPDF_LoadPage(document, pageNo - 1);
                    if (page == IntPtr.Zero)
                    {
                        pages.Add(new Jso().Add("page", pageNo).Add("error", "load_page_failed"));
                        continue;
                    }
                    IntPtr bitmap = IntPtr.Zero;
                    try
                    {
                        float widthPt = FPDF_GetPageWidthF(page);
                        float heightPt = FPDF_GetPageHeightF(page);
                        int width = Math.Max(1, (int)Math.Round(widthPt * dpi / 72.0));
                        int height = Math.Max(1, (int)Math.Round(heightPt * dpi / 72.0));
                        if ((long)width * height > 40000000L)
                        {
                            pages.Add(new Jso().Add("page", pageNo).Add("error", "page_too_large")
                                .Add("width", width).Add("height", height));
                            continue;
                        }

                        bitmap = FPDFBitmap_Create(width, height, 0); // 0 = 不透明 BGRx
                        if (bitmap == IntPtr.Zero)
                        {
                            pages.Add(new Jso().Add("page", pageNo).Add("error", "bitmap_create_failed"));
                            continue;
                        }
                        FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
                        FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, FPDF_ANNOT | FPDF_LCD_TEXT);

                        string file = Path.Combine(dir, stem + "-p" + pageNo.ToString("00") + ".png");
                        SaveBitmap(bitmap, width, height, file);

                        var item = new Jso();
                        item.Add("page", pageNo);
                        item.Add("path", file);
                        item.Add("width", width);
                        item.Add("height", height);
                        item.Add("bytes", new FileInfo(file).Length);
                        pages.Add(item);
                    }
                    finally
                    {
                        if (bitmap != IntPtr.Zero) FPDFBitmap_Destroy(bitmap);
                        FPDF_ClosePage(page);
                    }
                }

                result.Add("ok", pages.Count > 0);
                if (pages.Count == 0) result.Add("error", new Jso().Add("code", "pdf_render_empty").Add("message", "没有渲染出任何页面"));
                return result;
            }
            catch (Exception ex)
            {
                result.Add("ok", false);
                result.Add("error", new Jso().Add("code", "pdf_render_failed").Add("message", ex.Message));
                return result;
            }
            finally
            {
                if (document != IntPtr.Zero) FPDF_CloseDocument(document);
                try { FPDF_DestroyLibrary(); } catch { }
            }
        }

        private static void SaveBitmap(IntPtr bitmap, int width, int height, string file)
        {
            IntPtr buffer = FPDFBitmap_GetBuffer(bitmap);
            int stride = FPDFBitmap_GetStride(bitmap);
            using (var image = new Bitmap(width, height, PixelFormat.Format32bppRgb))
            {
                var rect = new Rectangle(0, 0, width, height);
                BitmapData data = image.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                try
                {
                    int rowBytes = width * 4;
                    var row = new byte[rowBytes];
                    for (int y = 0; y < height; y++)
                    {
                        IntPtr source = new IntPtr(buffer.ToInt64() + (long)y * stride);
                        Marshal.Copy(source, row, 0, rowBytes);
                        Marshal.Copy(row, 0, new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), rowBytes);
                    }
                }
                finally
                {
                    image.UnlockBits(data);
                }
                image.Save(file, ImageFormat.Png);
            }
        }
    }
}
