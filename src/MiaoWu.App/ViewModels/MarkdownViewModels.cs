using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using MiaoWu.App.Mvvm;
using MiaoWu.App.Resources;
using MiaoWu.App.Services;
using MiaoWu.Core.Files;
using MiaoWu.Core.Markdown;
using MiaoWu.Core.Text;

namespace MiaoWu.App.ViewModels
{
    /// <summary>Markdown 块级渲染模型。每个块对应 DocumentTemplates.xaml 中的一个模板。</summary>
    public abstract class MarkdownBlockViewModel : ObservableObject
    {
        private bool _isFindMatch;

        public abstract string PlainText { get; }

        public abstract Thickness Margin { get; }

        public bool IsFindMatch
        {
            get { return _isFindMatch; }
            set { SetProperty(ref _isFindMatch, value); }
        }
    }

    public sealed class MarkdownHeadingViewModel : MarkdownBlockViewModel
    {
        private readonly Thickness _margin;

        public MarkdownHeadingViewModel(MarkdownHeading heading, bool isFirst, bool isH3)
        {
            Level = heading.Level;
            Inlines = heading.Inlines;
            IsH3Style = isH3;
            _margin = isFirst
                ? new Thickness(0, 0, 0, 10)
                : (Level == 1
                    ? new Thickness(0, 24, 0, 10)
                    : new Thickness(0, 20, 0, 8));

            string text = string.Empty;
            foreach (MarkdownInline inline in heading.Inlines)
            {
                text += inline.Text;
            }

            PlainText = text;
        }

        public int Level { get; }

        public IReadOnlyList<MarkdownInline> Inlines { get; }

        /// <summary>H4–H6 夹到 H3 后仍显示 H3 字号，不再继续放大。</summary>
        public bool IsH3Style { get; }

        public override string PlainText { get; }

        public override Thickness Margin
        {
            get { return _margin; }
        }
    }

    public sealed class MarkdownParagraphViewModel : MarkdownBlockViewModel
    {
        public MarkdownParagraphViewModel(MarkdownParagraph paragraph)
        {
            Inlines = paragraph.Inlines;

            string text = string.Empty;
            foreach (MarkdownInline inline in paragraph.Inlines)
            {
                text += inline.Text;
            }

            PlainText = text;
        }

        public IReadOnlyList<MarkdownInline> Inlines { get; }

        public override string PlainText { get; }

        public override Thickness Margin
        {
            get { return new Thickness(0, 0, 0, 10); }
        }
    }

    public sealed class MarkdownListItemViewModel
    {
        public MarkdownListItemViewModel(MarkdownListItem item)
        {
            Marker = item.Marker;
            Level = item.Level;
            Inlines = item.Inlines;
            Indent = new Thickness(item.Level * 16, 0, 0, 0);

            string text = string.Empty;
            foreach (MarkdownInline inline in item.Inlines)
            {
                text += inline.Text;
            }

            PlainText = text;
        }

        public string Marker { get; }

        public int Level { get; }

        public IReadOnlyList<MarkdownInline> Inlines { get; }

        public Thickness Indent { get; }

        public string PlainText { get; }
    }

    public sealed class MarkdownListViewModel : MarkdownBlockViewModel
    {
        public MarkdownListViewModel(MarkdownList list)
        {
            Items = new ObservableCollection<MarkdownListItemViewModel>();
            string text = string.Empty;

            foreach (MarkdownListItem item in list.Items)
            {
                Items.Add(new MarkdownListItemViewModel(item));
                text += item.Marker + " " + Items[Items.Count - 1].PlainText + "\n";
            }

            PlainText = text;
        }

        public ObservableCollection<MarkdownListItemViewModel> Items { get; }

        public override string PlainText { get; }

        public override Thickness Margin
        {
            get { return new Thickness(0, 0, 0, 10); }
        }
    }

    public sealed class MarkdownQuoteViewModel : MarkdownBlockViewModel
    {
        public MarkdownQuoteViewModel(MarkdownQuote quote, IClipboardService clipboard)
        {
            Blocks = new ObservableCollection<MarkdownBlockViewModel>();
            string text = string.Empty;

            foreach (MarkdownBlock block in quote.Blocks)
            {
                MarkdownBlockViewModel blockViewModel = MarkdownViewModelFactory.Create(block, clipboard, false);
                if (blockViewModel == null)
                {
                    continue;
                }

                Blocks.Add(blockViewModel);
                text += blockViewModel.PlainText + "\n";
            }

            PlainText = text;
        }

        public ObservableCollection<MarkdownBlockViewModel> Blocks { get; }

        public override string PlainText { get; }

        public override Thickness Margin
        {
            get { return new Thickness(0, 0, 0, 10); }
        }
    }

    public sealed class MarkdownCodeViewModel : MarkdownBlockViewModel
    {
        public MarkdownCodeViewModel(MarkdownCodeBlock code, IClipboardService clipboard)
        {
            Language = code.Language;
            Text = code.Text;
            HasLanguage = !string.IsNullOrEmpty(code.Language);
            CopyCommand = new RelayCommand(() => Copy(clipboard));
            PlainText = code.Text ?? string.Empty;
        }

        public string Language { get; }

        public bool HasLanguage { get; }

        public string Text { get; }

        public RelayCommand CopyCommand { get; }

        public string CopyLabel
        {
            get { return IsCopied ? Strings.Copied : Strings.CopyCode; }
        }

        private bool IsCopied { get; set; }

        public override string PlainText { get; }

        public override Thickness Margin
        {
            get { return new Thickness(0, 0, 0, 12); }
        }

        private void Copy(IClipboardService clipboard)
        {
            if (clipboard == null)
            {
                return;
            }

            if (clipboard.SetText(Text))
            {
                IsCopied = true;
                OnPropertyChanged(nameof(CopyLabel));
            }
        }
    }

    public sealed class MarkdownTableCellViewModel
    {
        public MarkdownTableCellViewModel(string text, bool isHeader)
        {
            Text = text ?? string.Empty;
            IsHeader = isHeader;
        }

        public string Text { get; }

        public bool IsHeader { get; }
    }

    public sealed class MarkdownTableViewModel : MarkdownBlockViewModel
    {
        public MarkdownTableViewModel(MarkdownTable table)
        {
            ColumnCount = table.Headers.Count;
            Cells = new ObservableCollection<MarkdownTableCellViewModel>();

            string text = string.Empty;
            foreach (string header in table.Headers)
            {
                Cells.Add(new MarkdownTableCellViewModel(header, true));
                text += header + " ";
            }

            foreach (IReadOnlyList<string> row in table.Rows)
            {
                foreach (string cell in row)
                {
                    Cells.Add(new MarkdownTableCellViewModel(cell, false));
                }
            }

            PlainText = text;
        }

        public int ColumnCount { get; }

        public ObservableCollection<MarkdownTableCellViewModel> Cells { get; }

        public override string PlainText { get; }

        public override Thickness Margin
        {
            get { return new Thickness(0, 0, 0, 12); }
        }
    }

    public sealed class MarkdownRuleViewModel : MarkdownBlockViewModel
    {
        public override string PlainText
        {
            get { return string.Empty; }
        }

        public override Thickness Margin
        {
            get { return new Thickness(0, 14, 0, 14); }
        }
    }

    /// <summary>
    /// Markdown 文档内容：块级渲染，不用 FlowDocument；正文最大 648 宽、左对齐。
    /// 查找以块为单位，命中块加浅色底并滚动到可见。
    /// </summary>
    public sealed class MarkdownDocumentViewModel : DocumentContentViewModel
    {
        private readonly List<int> _matchBlockIndexes = new List<int>();
        private MarkdownBlockViewModel _currentFindBlock;

        public MarkdownDocumentViewModel(FilePreview preview, MarkdownDocument document, IClipboardService clipboard)
        {
            Blocks = new ObservableCollection<MarkdownBlockViewModel>();
            bool first = true;

            foreach (MarkdownBlock block in document.Blocks)
            {
                MarkdownBlockViewModel blockViewModel = MarkdownViewModelFactory.Create(block, clipboard, first);
                if (blockViewModel == null)
                {
                    continue;
                }

                Blocks.Add(blockViewModel);
                first = false;
            }

            MetaText = preview.MetaText;
            IsTruncated = preview.IsTruncated;
            TruncationReason = preview.TruncationReason;
        }

        public ObservableCollection<MarkdownBlockViewModel> Blocks { get; }

        public MarkdownBlockViewModel CurrentFindBlock
        {
            get { return _currentFindBlock; }
            private set { SetProperty(ref _currentFindBlock, value); }
        }

        public bool IsEmpty
        {
            get { return Blocks.Count == 0; }
        }

        public override bool CanFind
        {
            get { return Blocks.Count > 0; }
        }

        public override int Find(string query)
        {
            ClearFind();
            _matchBlockIndexes.Clear();

            if (string.IsNullOrWhiteSpace(query))
            {
                return 0;
            }

            for (int index = 0; index < Blocks.Count; index++)
            {
                if (Blocks[index].PlainText.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _matchBlockIndexes.Add(index);
                }
            }

            return _matchBlockIndexes.Count;
        }

        public override void GoToFindMatch(int index)
        {
            if (_currentFindBlock != null)
            {
                _currentFindBlock.IsFindMatch = false;
                _currentFindBlock = null;
            }

            if (index < 0 || index >= _matchBlockIndexes.Count)
            {
                return;
            }

            _currentFindBlock = Blocks[_matchBlockIndexes[index]];
            _currentFindBlock.IsFindMatch = true;
        }

        public override void ClearFind()
        {
            foreach (MarkdownBlockViewModel block in Blocks)
            {
                block.IsFindMatch = false;
            }

            CurrentFindBlock = null;
            _matchBlockIndexes.Clear();
        }
    }

    /// <summary>把 Core 的只读 AST 转成 App 的渲染模型。</summary>
    public static class MarkdownViewModelFactory
    {
        public static MarkdownBlockViewModel Create(
            MarkdownBlock block,
            IClipboardService clipboard,
            bool isFirst = false)
        {
            if (block == null)
            {
                return null;
            }

            var heading = block as MarkdownHeading;
            if (heading != null)
            {
                return new MarkdownHeadingViewModel(heading, isFirst, heading.Level > 2);
            }

            var paragraph = block as MarkdownParagraph;
            if (paragraph != null)
            {
                return new MarkdownParagraphViewModel(paragraph);
            }

            var list = block as MarkdownList;
            if (list != null)
            {
                return new MarkdownListViewModel(list);
            }

            var quote = block as MarkdownQuote;
            if (quote != null)
            {
                return new MarkdownQuoteViewModel(quote, clipboard);
            }

            var code = block as MarkdownCodeBlock;
            if (code != null)
            {
                return new MarkdownCodeViewModel(code, clipboard);
            }

            var table = block as MarkdownTable;
            if (table != null && table.Headers.Count > 0)
            {
                return new MarkdownTableViewModel(table);
            }

            if (block is MarkdownRule)
            {
                return new MarkdownRuleViewModel();
            }

            return null;
        }
    }
}
