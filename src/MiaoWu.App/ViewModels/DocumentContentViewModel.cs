using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MiaoWu.App.Mvvm;
using MiaoWu.App.Resources;
using MiaoWu.Core.Files;
using MiaoWu.Core.Text;

namespace MiaoWu.App.ViewModels
{
    /// <summary>
    /// 中栏文档内容基类。加载、错误、截断、查找状态都在这里统一表达，
    /// 具体渲染由子类与对应的 DataTemplate 负责。
    /// </summary>
    public abstract class DocumentContentViewModel : ObservableObject
    {
        private bool _isLoading;
        private string _errorMessage;
        private string _metaText;
        private bool _isTruncated;
        private string _truncationReason;
        private double _scrollOffset;

        public bool IsLoading
        {
            get { return _isLoading; }
            protected set { SetProperty(ref _isLoading, value); }
        }

        public string ErrorMessage
        {
            get { return _errorMessage; }
            protected set
            {
                if (SetProperty(ref _errorMessage, value))
                {
                    OnPropertyChanged(nameof(HasError));
                }
            }
        }

        public bool HasError
        {
            get { return !string.IsNullOrEmpty(_errorMessage); }
        }

        public string MetaText
        {
            get { return _metaText; }
            protected set { SetProperty(ref _metaText, value); }
        }

        public bool IsTruncated
        {
            get { return _isTruncated; }
            protected set { SetProperty(ref _isTruncated, value); }
        }

        public string TruncationReason
        {
            get { return _truncationReason; }
            protected set { SetProperty(ref _truncationReason, value); }
        }

        /// <summary>由视图回写，切标签/重启时恢复。值不参与查找。</summary>
        public double ScrollOffset
        {
            get { return _scrollOffset; }
            set
            {
                double normalized = double.IsNaN(value) || value < 0 ? 0 : value;
                SetProperty(ref _scrollOffset, normalized);
            }
        }

        public virtual bool CanFind
        {
            get { return false; }
        }

        public virtual int Find(string query)
        {
            return 0;
        }

        public virtual void GoToFindMatch(int index)
        {
        }

        public virtual void ClearFind()
        {
        }
    }

    /// <summary>加载中的占位内容：中栏显示单行“正在读取…”，不用骨架屏。</summary>
    public sealed class LoadingDocumentContent : DocumentContentViewModel
    {
        public LoadingDocumentContent()
        {
            IsLoading = true;
            MetaText = Strings.Loading;
        }

        public LoadingDocumentContent(string metaText)
        {
            IsLoading = true;
            MetaText = metaText;
        }
    }

    /// <summary>打开失败：栏位内错误卡，不弹全局对话框。</summary>
    public sealed class ErrorDocumentContent : DocumentContentViewModel
    {
        public ErrorDocumentContent(string message)
        {
            ErrorMessage = string.IsNullOrWhiteSpace(message)
                ? Strings.FileMissingError
                : message;
            MetaText = string.Empty;
        }
    }

    /// <summary>二进制拒绝：保留文件名与大小，说明为什么不能预览。</summary>
    public sealed class BinaryDocumentContent : DocumentContentViewModel
    {
        public BinaryDocumentContent(string filePath, long byteLength)
        {
            FileName = Path.GetFileName(filePath);
            FilePath = filePath;
            SizeText = FileSizeFormatter.Format(byteLength);
            MetaText = DocumentMetaFormatter.Format("二进制", TextNewLineStyle.None, byteLength, 0);
        }

        public string FileName { get; }

        public string FilePath { get; }

        public string SizeText { get; }
    }

    /// <summary>图片预览：按显示宽度解码，避免大图占满内存。</summary>
    public sealed class ImageDocumentViewModel : DocumentContentViewModel
    {
        public ImageDocumentViewModel(string filePath)
        {
            FilePath = filePath;
            FileName = Path.GetFileName(filePath);

            var info = new FileInfo(filePath);
            long length = info.Exists ? info.Length : 0;

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(filePath, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bitmap.DecodePixelWidth = 1280;
                bitmap.EndInit();
                bitmap.Freeze();

                Image = bitmap;
                ImageWidth = bitmap.PixelWidth;
                ImageHeight = bitmap.PixelHeight;
                ImageInfo = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} × {1} · {2}",
                    ImageWidth,
                    ImageHeight,
                    FileSizeFormatter.Format(length));
                MetaText = DocumentMetaFormatter.Format("图片", TextNewLineStyle.None, length, 0);
            }
            catch (Exception ex)
            {
                ErrorMessage = "图片解码失败：" + ex.Message;
                MetaText = string.Empty;
            }
        }

        public string FilePath { get; }

        public string FileName { get; }

        public ImageSource Image { get; private set; }

        public int ImageWidth { get; private set; }

        public int ImageHeight { get; private set; }

        public string ImageInfo { get; private set; }
    }

    /// <summary>一行统一行模型：文本 / 代码 / diff / 命令输出共用。数字列是否显示由内容决定。</summary>
    public sealed class DocumentLineViewModel : ObservableObject
    {
        private bool _isFindMatch;

        public DocumentLineViewModel(
            string text,
            DiffLineKind? kind,
            int? oldNumber,
            int? newNumber,
            bool showNumbers,
            bool isMonospace,
            double lineHeight)
        {
            Text = text ?? string.Empty;
            Kind = kind;
            ShowNumbers = showNumbers;
            IsMonospace = isMonospace;
            LineHeight = lineHeight;
            OldNumberText = oldNumber.HasValue ? oldNumber.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
            NewNumberText = newNumber.HasValue ? newNumber.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
        }

        public string Text { get; }

        public DiffLineKind? Kind { get; }

        public bool ShowNumbers { get; }

        /// <summary>代码 / diff 用 Consolas 12/19；正文用微软雅黑 13/22 并换行。</summary>
        public bool IsMonospace { get; }

        public double LineHeight { get; }

        public string OldNumberText { get; }

        public string NewNumberText { get; }

        public bool IsProse
        {
            get { return !IsMonospace; }
        }

        public bool IsAdded
        {
            get { return Kind == DiffLineKind.Added; }
        }

        public bool IsRemoved
        {
            get { return Kind == DiffLineKind.Removed; }
        }

        public bool IsHeaderLine
        {
            get { return Kind == DiffLineKind.FileHeader || Kind == DiffLineKind.HunkHeader; }
        }

        public bool IsNoNewline
        {
            get { return Kind == DiffLineKind.NoNewline; }
        }

        public string Sign
        {
            get
            {
                if (Kind == DiffLineKind.Added)
                {
                    return "+";
                }

                if (Kind == DiffLineKind.Removed)
                {
                    return "-";
                }

                return string.Empty;
            }
        }

        public bool IsFindMatch
        {
            get { return _isFindMatch; }
            set { SetProperty(ref _isFindMatch, value); }
        }
    }

    /// <summary>diff / 命令输出：统一行号列、+/− 配色、等宽文本与虚拟化。</summary>
    public sealed class LineDocumentViewModel : DocumentContentViewModel
    {
        private readonly List<int> _matchLineIndexes = new List<int>();
        private DocumentLineViewModel _currentFindLine;

        public LineDocumentViewModel(
            IEnumerable<DocumentLineViewModel> lines,
            bool showNumbers,
            string metaText,
            string headerCommand = null,
            string exitCodeText = null,
            string durationText = null,
            bool isTruncated = false,
            string truncationReason = null)
        {
            Lines = new ObservableCollection<DocumentLineViewModel>(lines ?? new DocumentLineViewModel[0]);
            ShowNumbers = showNumbers;
            MetaText = metaText;
            HeaderCommand = headerCommand;
            ExitCodeText = exitCodeText;
            DurationText = durationText;
            IsTruncated = isTruncated;
            TruncationReason = truncationReason;
        }

        public ObservableCollection<DocumentLineViewModel> Lines { get; }

        public bool ShowNumbers { get; }

        public string HeaderCommand { get; }

        public string ExitCodeText { get; }

        public string DurationText { get; }

        public bool HasHeader
        {
            get { return !string.IsNullOrEmpty(HeaderCommand); }
        }

        public DocumentLineViewModel CurrentFindLine
        {
            get { return _currentFindLine; }
            private set { SetProperty(ref _currentFindLine, value); }
        }

        public override bool CanFind
        {
            get { return Lines.Count > 0; }
        }

        public override int Find(string query)
        {
            ClearFind();
            _matchLineIndexes.Clear();

            if (string.IsNullOrWhiteSpace(query))
            {
                return 0;
            }

            for (int index = 0; index < Lines.Count; index++)
            {
                if (Lines[index].Text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _matchLineIndexes.Add(index);
                }
            }

            return _matchLineIndexes.Count;
        }

        public override void GoToFindMatch(int index)
        {
            if (_currentFindLine != null)
            {
                _currentFindLine.IsFindMatch = false;
                _currentFindLine = null;
            }

            if (index < 0 || index >= _matchLineIndexes.Count)
            {
                return;
            }

            _currentFindLine = Lines[_matchLineIndexes[index]];
            _currentFindLine.IsFindMatch = true;
        }

        public override void ClearFind()
        {
            foreach (DocumentLineViewModel line in Lines)
            {
                line.IsFindMatch = false;
            }

            CurrentFindLine = null;
            _matchLineIndexes.Clear();
        }

        public static LineDocumentViewModel FromDiff(DiffDocument document)
        {
            var lines = new List<DocumentLineViewModel>();
            foreach (DiffLine line in document.Lines)
            {
                lines.Add(new DocumentLineViewModel(
                    line.Text,
                    line.Kind,
                    line.OldLineNumber,
                    line.NewLineNumber,
                    true,
                    true,
                    19));
            }

            string meta = string.Format(
                CultureInfo.InvariantCulture,
                "统一 diff · +{0} −{1}",
                document.AddedCount,
                document.RemovedCount);

            return new LineDocumentViewModel(lines, true, meta);
        }

        public static LineDocumentViewModel FromCommand(CommandOutputSample sample)
        {
            string[] rawLines = (sample.Output ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split('\n');

            var lines = new List<DocumentLineViewModel>(rawLines.Length);
            foreach (string raw in rawLines)
            {
                lines.Add(new DocumentLineViewModel(raw, null, null, null, false, true, 19));
            }

            string meta = string.Format(
                CultureInfo.InvariantCulture,
                "命令输出 · {0} 行",
                lines.Count);

            return new LineDocumentViewModel(
                lines,
                false,
                meta,
                sample.CommandText,
                Strings.ExitCode(sample.ExitCode),
                Strings.Duration(sample.DurationText));
        }

        /// <summary>
        /// 文本预览：正文按 13/22 换行；代码按 Consolas 12/19 不换行、可横向滚动。
        /// 每行一个虚拟化项，5000 行也不会一次性排版。
        /// </summary>
        public static LineDocumentViewModel FromText(FilePreview preview, bool isCode)
        {
            string content = preview == null ? string.Empty : preview.Content ?? string.Empty;
            string normalized = content.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] rawLines = normalized.Length == 0 ? new string[0] : normalized.Split('\n');

            int count = rawLines.Length;
            if (count > 0 && rawLines[count - 1].Length == 0 && normalized.EndsWith("\n", StringComparison.Ordinal))
            {
                count--;
            }

            double lineHeight = isCode ? 19 : 22;
            var lines = new List<DocumentLineViewModel>(count);
            for (int index = 0; index < count; index++)
            {
                lines.Add(new DocumentLineViewModel(
                    rawLines[index],
                    null,
                    null,
                    null,
                    false,
                    isCode,
                    lineHeight));
            }

            return new LineDocumentViewModel(
                lines,
                false,
                preview == null ? string.Empty : preview.MetaText,
                null,
                null,
                null,
                preview != null && preview.IsTruncated,
                preview == null ? null : preview.TruncationReason);
        }
    }
}
