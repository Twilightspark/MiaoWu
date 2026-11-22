using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MiaoWu.Core.Input;

namespace MiaoWu.App.Controls
{
    /// <summary>
    /// 输入框正文编辑器：RichTextBox + 内联引用 chip。
    ///
    /// 三个投影分开取，和 DSH 的 DecoratorNode 约定一致：
    /// - 显示态：图标（图片给缩略图）+ 文件名，圆角浅 accent 底；
    /// - 剪贴板态 / 草稿态：chip 序列化成 <c>@path</c>，与用户手打的 mention 同形；
    /// - 检测态：由 <see cref="PlainText"/> 提供，chip 的文本长度参与光标换算。
    /// </summary>
    public sealed class ComposerEditor : RichTextBox
    {
        /// <summary>草稿纯文本。chip 折算成它的 mention 文本，图片同理。</summary>
        public static readonly DependencyProperty PlainTextProperty = DependencyProperty.Register(
            "PlainText",
            typeof(string),
            typeof(ComposerEditor),
            new FrameworkPropertyMetadata(
                string.Empty,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnPlainTextChanged));

        private readonly Dictionary<string, ComposerMention> _mentions =
            new Dictionary<string, ComposerMention>(StringComparer.Ordinal);

        private List<ComposerMention> _matchOrder = new List<ComposerMention>();

        private bool _rebuilding;
        private bool _syncingPlainText;

        public ComposerEditor()
        {
            AcceptsTab = false;
            IsDocumentEnabled = true;
            AllowDrop = true;
            BorderThickness = new Thickness(0);
            Background = Brushes.Transparent;
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            Document.LineHeight = 22;

            TextChanged += OnDocumentTextChanged;
            SelectionChanged += OnSelectionChanged;

            // 复制 / 剪切 / 粘贴都走纯文本：chip 按 mention 文本进出，避免把内联控件
            // 当成富文本粘贴回来，产生没有 Tag 的“假 chip”。
            CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, OnCopyCommand, OnCanCopyCommand));
            CommandBindings.Add(new CommandBinding(ApplicationCommands.Cut, OnCutCommand, OnCanCopyCommand));
            CommandBindings.Add(new CommandBinding(ApplicationCommands.Paste, OnPasteCommand));
        }

        /// <summary>用户编辑（或 chip 增删）后触发；光标移动不触发。</summary>
        public event EventHandler DraftChanged;

        /// <summary>光标 / 选区变化；供触发检测重新 track。</summary>
        public event EventHandler CaretMoved;

        /// <summary>用户把文件拖进输入框或粘贴文件 / 图片；参数是绝对路径。</summary>
        public event EventHandler<ComposerPathsEventArgs> ExternalPathsDropped;

        protected override void OnInitialized(EventArgs e)
        {
            base.OnInitialized(e);

            // 文档排版参数统一取 Tokens.xaml 里的正文行高与字体，避免和消息流两套字号。
            Document.LineHeight = FindDouble("Font.Body.Line", 22);
            if (TryFindResource("Font.Family") is FontFamily family)
            {
                Document.FontFamily = family;
            }

            Document.FontSize = FindDouble("Font.Body.Size", 13);
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            // 富文本默认带 5px 的 PagePadding，且模板套用时 WPF 会把文档的 PagePadding 再写回 5。
            // 这里清零后，光标与正文落在输入卡的内边距上，占位提示再往里 2px：
            // 两者既不重叠，打字时也不会出现“文字比提示右移几像素”的跳动。
            Document.PagePadding = new Thickness(0);
        }

        public string PlainText
        {
            get { return (string)GetValue(PlainTextProperty); }
            set { SetValue(PlainTextProperty, value ?? string.Empty); }
        }

        /// <summary>当前文档里的引用（按出现顺序）。</summary>
        public IReadOnlyList<ComposerMention> Mentions
        {
            get
            {
                var list = new List<ComposerMention>();
                foreach (Block block in Document.Blocks)
                {
                    var paragraph = block as Paragraph;
                    if (paragraph == null)
                    {
                        continue;
                    }

                    foreach (Inline inline in paragraph.Inlines)
                    {
                        var container = inline as InlineUIContainer;
                        if (container == null)
                        {
                            continue;
                        }

                        var mention = container.Tag as ComposerMention;
                        if (mention != null)
                        {
                            list.Add(mention);
                        }
                    }
                }

                return list;
            }
        }

        /// <summary>纯文本光标偏移；越界时夹到合法范围。</summary>
        public int CaretOffset
        {
            get
            {
                int offset = GetOffset(CaretPosition);
                int length = (PlainText ?? string.Empty).Length;
                return offset < 0 ? 0 : (offset > length ? length : offset);
            }
        }

        /// <summary>紧邻光标左侧的文本，最多 <paramref name="max"/> 个字符。</summary>
        public string TextBeforeCaret(int max)
        {
            string text = PlainText ?? string.Empty;
            int caret = CaretOffset;
            int start = Math.Max(0, caret - Math.Max(0, max));
            return text.Substring(start, caret - start);
        }

        /// <summary>把草稿整体换成纯文本；已有 chip 会被拆回 mention 文本（已知引用仍渲染成 chip）。</summary>
        public void SetPlainText(string text)
        {
            if (!string.Equals(text ?? string.Empty, PlainText ?? string.Empty, StringComparison.Ordinal))
            {
                PlainText = text ?? string.Empty;
            }
        }

        /// <summary>清空正文与引用（发送成功后调用）。</summary>
        public void ClearAll()
        {
            _rebuilding = true;
            try
            {
                Document.Blocks.Clear();
                Document.Blocks.Add(CreateParagraph(string.Empty));
            }
            finally
            {
                _rebuilding = false;
            }

            SyncPlainTextFromDocument();
            MoveCaretToEnd();
        }

        /// <summary>
        /// 用引用替换当前触发 token（<c>@que</c> → chip + 一个分隔空格）。
        /// </summary>
        public void InsertMention(TriggerHit hit, ComposerMention mention)
        {
            if (hit == null || mention == null || string.IsNullOrEmpty(mention.Text))
            {
                return;
            }

            RegisterMention(mention);
            TextPointer start = GetPointerAtOffset(hit.Start);
            TextPointer end = GetPointerAtOffset(hit.End);
            if (start == null || end == null || start.CompareTo(end) > 0)
            {
                return;
            }

            try
            {
                new TextRange(start, end).Text = string.Empty;
            }
            catch (Exception exception)
            {
                Debug.WriteLine("composer.replace-token: " + exception.Message);
                return;
            }

            var container = new InlineUIContainer(CreateChip(mention), start)
            {
                BaselineAlignment = BaselineAlignment.Center,
                Tag = mention
            };

            TextPointer after = container.ElementEnd;
            after.InsertTextInRun(" ");
            CaretPosition = after.GetPositionAtOffset(1, LogicalDirection.Forward) ?? after;
        }

        /// <summary>
        /// 把触发 token 换成纯文本（目录下钻：<c>@"dir/</c>，引号保持打开）。
        /// </summary>
        public void ReplaceHit(TriggerHit hit, string text)
        {
            if (hit == null)
            {
                return;
            }

            TextPointer start = GetPointerAtOffset(hit.Start);
            TextPointer end = GetPointerAtOffset(hit.End);
            if (start == null || end == null)
            {
                return;
            }

            try
            {
                new TextRange(start, end).Text = text ?? string.Empty;
                TextPointer caret = GetPointerAtOffset(hit.Start + (text ?? string.Empty).Length);
                if (caret != null)
                {
                    CaretPosition = caret;
                }
            }
            catch (Exception exception)
            {
                Debug.WriteLine("composer.replace-hit: " + exception.Message);
            }
        }

        /// <summary>在光标处插入一个引用 chip（文件选择器 / 拖拽 / 粘贴图片走这里）。</summary>
        public void InsertMentionAtCaret(ComposerMention mention)
        {
            if (mention == null || string.IsNullOrEmpty(mention.Text))
            {
                return;
            }

            RegisterMention(mention);
            TextPointer caret = CaretPosition;
            if (caret == null)
            {
                return;
            }

            var container = new InlineUIContainer(CreateChip(mention), caret)
            {
                BaselineAlignment = BaselineAlignment.Center,
                Tag = mention
            };

            TextPointer afterChip = container.ElementEnd;
            afterChip.InsertTextInRun(" ");
            CaretPosition = afterChip.GetPositionAtOffset(1, LogicalDirection.Forward) ?? afterChip;
        }

        /// <summary>直接插入一段文本（例如没有引用语义的路径）。</summary>
        public void InsertTextAtCaret(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = normalized.Split('\n');
            TextPointer caret = CaretPosition;
            if (caret == null)
            {
                return;
            }

            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                {
                    caret = caret.InsertParagraphBreak();
                }

                if (lines[i].Length > 0)
                {
                    int length = lines[i].Length;
                    caret.InsertTextInRun(lines[i]);
                    caret = caret.GetPositionAtOffset(length, LogicalDirection.Forward) ?? caret;
                }
            }

            CaretPosition = caret;
        }

        /// <summary>focus 后把光标放到末尾（发送失败恢复草稿时使用）。</summary>
        public void MoveCaretToEnd()
        {
            Paragraph last = Document.Blocks.OfType<Paragraph>().LastOrDefault();
            if (last != null)
            {
                CaretPosition = last.ContentEnd;
            }
        }

        // ---------------------------------------------------------------
        // 文档 ↔ 纯文本
        // ---------------------------------------------------------------

        private static void OnPlainTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var editor = d as ComposerEditor;
            if (editor == null || editor._syncingPlainText)
            {
                return;
            }

            string incoming = e.NewValue as string ?? string.Empty;
            if (string.Equals(incoming, editor.SerializeDocument(), StringComparison.Ordinal))
            {
                return;
            }

            editor.Rebuild(incoming);
        }

        private void OnDocumentTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_rebuilding)
            {
                return;
            }

            SyncPlainTextFromDocument();
            EventHandler handler = DraftChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void OnSelectionChanged(object sender, RoutedEventArgs e)
        {
            if (_rebuilding)
            {
                return;
            }

            EventHandler handler = CaretMoved;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void SyncPlainTextFromDocument()
        {
            string text = SerializeDocument();
            if (string.Equals(text, PlainText ?? string.Empty, StringComparison.Ordinal))
            {
                return;
            }

            _syncingPlainText = true;
            try
            {
                SetCurrentValue(PlainTextProperty, text);
            }
            finally
            {
                _syncingPlainText = false;
            }
        }

        /// <summary>把整份文档序列化成草稿文本；空文档返回空串。</summary>
        public string SerializeDocument()
        {
            var builder = new System.Text.StringBuilder();
            bool first = true;
            foreach (Block block in Document.Blocks)
            {
                var paragraph = block as Paragraph;
                if (paragraph == null)
                {
                    continue;
                }

                if (!first)
                {
                    builder.Append('\n');
                }

                first = false;
                foreach (Inline inline in paragraph.Inlines)
                {
                    builder.Append(SerializeInline(inline));
                }
            }

            return builder.ToString();
        }

        private string SerializeInline(Inline inline)
        {
            var run = inline as Run;
            if (run != null)
            {
                return run.Text ?? string.Empty;
            }

            var lineBreak = inline as LineBreak;
            if (lineBreak != null)
            {
                return "\n";
            }

            var container = inline as InlineUIContainer;
            if (container != null)
            {
                var mention = container.Tag as ComposerMention;
                return mention == null ? string.Empty : mention.Text ?? string.Empty;
            }

            return string.Empty;
        }

        private void Rebuild(string text)
        {
            _rebuilding = true;
            try
            {
                Document.Blocks.Clear();
                string normalized = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
                string[] lines = normalized.Split('\n');
                foreach (string line in lines)
                {
                    Paragraph paragraph = CreateParagraph(line);
                    Document.Blocks.Add(paragraph);
                }

                if (Document.Blocks.Count == 0)
                {
                    Document.Blocks.Add(CreateParagraph(string.Empty));
                }
            }
            finally
            {
                _rebuilding = false;
            }

            MoveCaretToEnd();
        }

        private Paragraph CreateParagraph(string line)
        {
            var paragraph = new Paragraph
            {
                Margin = new Thickness(0),
                LineHeight = Document.LineHeight
            };

            if (string.IsNullOrEmpty(line))
            {
                return paragraph;
            }

            int index = 0;
            while (index < line.Length)
            {
                ComposerMention mention = MatchMentionAt(line, index);
                if (mention != null)
                {
                    paragraph.Inlines.Add(new InlineUIContainer(CreateChip(mention))
                    {
                        BaselineAlignment = BaselineAlignment.Center,
                        Tag = mention
                    });
                    index += mention.Text.Length;
                    continue;
                }

                int next = index + 1;
                while (next < line.Length && MatchMentionAt(line, next) == null)
                {
                    next++;
                }

                paragraph.Inlines.Add(new Run(line.Substring(index, next - index)));
                index = next;
            }

            return paragraph;
        }

        private ComposerMention MatchMentionAt(string line, int index)
        {
            foreach (ComposerMention mention in _matchOrder)
            {
                string token = mention.Text;
                if (token == null || token.Length == 0)
                {
                    continue;
                }

                if (index + token.Length <= line.Length
                    && string.CompareOrdinal(line, index, token, 0, token.Length) == 0)
                {
                    return mention;
                }
            }

            return null;
        }

        private void RegisterMention(ComposerMention mention)
        {
            if (mention == null || string.IsNullOrEmpty(mention.Text))
            {
                return;
            }

            _mentions[mention.Text] = mention;
            _matchOrder = _mentions.Values
                .OrderByDescending(item => item.Text.Length)
                .ToList();
        }

        // ---------------------------------------------------------------
        // 光标 ↔ 偏移
        // ---------------------------------------------------------------

        private int GetOffset(TextPointer position)
        {
            if (position == null)
            {
                return 0;
            }

            int offset = 0;
            bool first = true;
            foreach (Block block in Document.Blocks)
            {
                if (!first)
                {
                    offset += 1;
                }

                first = false;
                var paragraph = block as Paragraph;
                if (paragraph == null)
                {
                    continue;
                }

                if (position.CompareTo(paragraph.ContentStart) <= 0)
                {
                    return offset;
                }

                int inner = GetParagraphOffset(paragraph, position);
                if (inner >= 0)
                {
                    return offset + inner;
                }

                offset += SerializeParagraph(paragraph).Length;
            }

            return offset;
        }

        private string SerializeParagraph(Paragraph paragraph)
        {
            var builder = new System.Text.StringBuilder();
            foreach (Inline inline in paragraph.Inlines)
            {
                builder.Append(SerializeInline(inline));
            }

            return builder.ToString();
        }

        /// <summary>返回光标在段落内的偏移；不在此段落内返回 -1。</summary>
        private int GetParagraphOffset(Paragraph paragraph, TextPointer position)
        {
            int offset = 0;
            foreach (Inline inline in paragraph.Inlines)
            {
                TextPointer start = inline.ContentStart;
                TextPointer end = inline.ContentEnd;

                if (position.CompareTo(start) <= 0)
                {
                    return offset;
                }

                if (position.CompareTo(end) <= 0)
                {
                    var run = inline as Run;
                    if (run != null)
                    {
                        string prefix = new TextRange(run.ContentStart, position).Text ?? string.Empty;
                        return offset + prefix.Length;
                    }

                    return offset;
                }

                offset += SerializeInline(inline).Length;
            }

            return paragraph.ContentEnd.CompareTo(position) >= 0 ? offset : -1;
        }

        private TextPointer GetPointerAtOffset(int offset)
        {
            if (offset < 0)
            {
                offset = 0;
            }

            int remaining = offset;
            bool first = true;
            foreach (Block block in Document.Blocks)
            {
                if (!first)
                {
                    if (remaining <= 0)
                    {
                        return block.ContentStart;
                    }

                    remaining -= 1;
                }

                first = false;
                var paragraph = block as Paragraph;
                if (paragraph == null)
                {
                    continue;
                }

                TextPointer pointer = GetParagraphPointer(paragraph, ref remaining);
                if (pointer != null)
                {
                    return pointer;
                }
            }

            Paragraph last = Document.Blocks.OfType<Paragraph>().LastOrDefault();
            return last == null ? null : last.ContentEnd;
        }

        /// <summary>在段落内寻找偏移；命中时清空 remaining 并返回指针，未命中则扣减剩余量。</summary>
        private TextPointer GetParagraphPointer(Paragraph paragraph, ref int remaining)
        {
            foreach (Inline inline in paragraph.Inlines)
            {
                int length = SerializeInline(inline).Length;
                if (remaining <= length)
                {
                    if (remaining == 0)
                    {
                        return inline.ContentStart;
                    }

                    var run = inline as Run;
                    if (run != null)
                    {
                        TextPointer inside = run.ContentStart.GetPositionAtOffset(remaining);
                        return inside ?? run.ContentEnd;
                    }

                    return inline.ContentEnd;
                }

                remaining -= length;
            }

            if (remaining <= 0)
            {
                return paragraph.ContentEnd;
            }

            remaining -= 0;
            return null;
        }

        // ---------------------------------------------------------------
        // chip 视觉
        // ---------------------------------------------------------------

        private FrameworkElement CreateChip(ComposerMention mention)
        {
            var border = new Border
            {
                Background = FindBrush("Brush.AccentSoft"),
                BorderThickness = new Thickness(0),
                CornerRadius = FindCornerRadius("Radius.Button"),
                Padding = new Thickness(5, 1, 7, 1),
                Margin = new Thickness(1, 0, 1, 0),
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true,
                ToolTip = string.IsNullOrEmpty(mention.ParentPath)
                    ? mention.RelativePath
                    : mention.ParentPath + "/" + mention.DisplayName
            };

            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            FrameworkElement icon = CreateChipIcon(mention);
            panel.Children.Add(icon);

            var label = new TextBlock
            {
                Text = string.IsNullOrEmpty(mention.DisplayName) ? mention.RelativePath : mention.DisplayName,
                FontSize = FindDouble("Font.Chip.Size", 11.5),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 180
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Accent");
            panel.Children.Add(label);

            border.Child = panel;
            return border;
        }

        private FrameworkElement CreateChipIcon(ComposerMention mention)
        {
            if (mention.Kind == ComposerMentionKind.Image && !string.IsNullOrEmpty(mention.FullPath))
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    bitmap.DecodePixelWidth = 32;
                    bitmap.UriSource = new Uri(mention.FullPath, UriKind.Absolute);
                    bitmap.EndInit();
                    bitmap.Freeze();

                    return new Image
                    {
                        Source = bitmap,
                        Width = 16,
                        Height = 16,
                        Stretch = Stretch.UniformToFill,
                        Margin = new Thickness(0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                }
                catch (Exception exception)
                {
                    Debug.WriteLine("composer.chip-image: " + exception.Message);
                }
            }

            string key;
            switch (mention.Kind)
            {
                case ComposerMentionKind.Folder:
                    key = "Ico.Folder";
                    break;
                case ComposerMentionKind.Image:
                    key = "Ico.Image";
                    break;
                default:
                    key = "Ico.File";
                    break;
            }

            var path = new System.Windows.Shapes.Path
            {
                Data = (Geometry)TryFindResource(key),
                Width = 12,
                Height = 12,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center
            };
            path.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Brush.Accent");
            return path;
        }

        private Brush FindBrush(string key)
        {
            var brush = TryFindResource(key) as Brush;
            return brush ?? Brushes.Transparent;
        }

        private CornerRadius FindCornerRadius(string key)
        {
            object value = TryFindResource(key);
            return value is CornerRadius ? (CornerRadius)value : new CornerRadius(6);
        }

        private double FindDouble(string key, double fallback)
        {
            object value = TryFindResource(key);
            return value is double ? (double)value : fallback;
        }

        // ---------------------------------------------------------------
        // 复制 / 剪切 / 粘贴：只在纯文本层进出
        // ---------------------------------------------------------------

        private void OnCanCopyCommand(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = !Selection.IsEmpty;
            e.Handled = true;
        }

        private void OnCopyCommand(object sender, ExecutedRoutedEventArgs e)
        {
            e.Handled = true;
            string text = GetSelectionPlainText();
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            try
            {
                Clipboard.SetText(text);
            }
            catch (Exception exception)
            {
                Debug.WriteLine("composer.copy: " + exception.Message);
            }
        }

        private void OnCutCommand(object sender, ExecutedRoutedEventArgs e)
        {
            e.Handled = true;
            string text = GetSelectionPlainText();
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            try
            {
                Clipboard.SetText(text);
            }
            catch (Exception exception)
            {
                Debug.WriteLine("composer.cut: " + exception.Message);
            }

            Selection.Text = string.Empty;
        }

        private void OnPasteCommand(object sender, ExecutedRoutedEventArgs e)
        {
            e.Handled = true;

            try
            {
                if (Clipboard.ContainsFileDropList())
                {
                    var paths = new List<string>();
                    foreach (string path in Clipboard.GetFileDropList())
                    {
                        if (!string.IsNullOrWhiteSpace(path))
                        {
                            paths.Add(path);
                        }
                    }

                    if (paths.Count > 0)
                    {
                        EventHandler<ComposerPathsEventArgs> dropHandler = ExternalPathsDropped;
                        if (dropHandler != null)
                        {
                            dropHandler(this, new ComposerPathsEventArgs(paths));
                        }

                        return;
                    }
                }

                if (Clipboard.ContainsText())
                {
                    InsertTextAtCaret(Clipboard.GetText());
                }
            }
            catch (Exception exception)
            {
                Debug.WriteLine("composer.paste: " + exception.Message);
            }
        }

        private string GetSelectionPlainText()
        {
            if (Selection == null || Selection.IsEmpty)
            {
                return string.Empty;
            }

            int start = GetOffset(Selection.Start);
            int end = GetOffset(Selection.End);
            if (end < start)
            {
                int swap = start;
                start = end;
                end = swap;
            }

            string text = PlainText ?? string.Empty;
            start = Math.Max(0, Math.Min(start, text.Length));
            end = Math.Max(0, Math.Min(end, text.Length));
            return end <= start ? string.Empty : text.Substring(start, end - start);
        }

        // ---------------------------------------------------------------
        // 拖拽 / 粘贴
        // ---------------------------------------------------------------

        protected override void OnPreviewDragOver(DragEventArgs e)
        {
            if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
                return;
            }

            base.OnPreviewDragOver(e);
        }

        protected override void OnPreviewDrop(DragEventArgs e)
        {
            if (e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                base.OnPreviewDrop(e);
                return;
            }

            string[] paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths == null || paths.Length == 0)
            {
                return;
            }

            e.Handled = true;
            EventHandler<ComposerPathsEventArgs> handler = ExternalPathsDropped;
            if (handler != null)
            {
                handler(this, new ComposerPathsEventArgs(paths));
            }

            Focus();
        }
    }

    /// <summary>拖拽 / 粘贴进来的绝对路径。</summary>
    public sealed class ComposerPathsEventArgs : EventArgs
    {
        public ComposerPathsEventArgs(IReadOnlyList<string> paths)
        {
            Paths = paths ?? new List<string>();
        }

        public IReadOnlyList<string> Paths { get; private set; }
    }
}
