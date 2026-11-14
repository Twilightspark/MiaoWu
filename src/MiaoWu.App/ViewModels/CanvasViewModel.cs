using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using MiaoWu.App.Mvvm;
using MiaoWu.App.Resources;
using MiaoWu.App.Services;
using MiaoWu.Core.Abstractions;
using MiaoWu.Core.Documents;
using MiaoWu.Core.Files;
using MiaoWu.Core.Markdown;
using MiaoWu.Core.Settings;
using MiaoWu.Core.Text;
using MiaoWu.Core.Workspaces;

namespace MiaoWu.App.ViewModels
{
    /// <summary>
    /// 中栏画布：标签行、操作栏、文本 / Markdown / diff / 命令输出预览与文档内查找。
    /// 标签切换 0ms，不做过渡动画；查找与滚动位置按标签各自保存。
    /// </summary>
    public sealed class CanvasViewModel : ObservableObject
    {
        private readonly IFilePreviewService _previewService;
        private readonly IClipboardService _clipboard;
        private readonly IFilePicker _filePicker;
        private readonly MarkdownAstCache _markdownCache;
        private readonly ObservableCollection<DocumentTabViewModel> _tabs;

        private DocumentTabViewModel _selectedTab;
        private bool _isFindOpen;
        private string _findQuery = string.Empty;
        private string _findSummary = string.Empty;
        private int _findMatchCount;
        private int _findMatchIndex = -1;

        public CanvasViewModel(
            IFilePreviewService previewService,
            IClipboardService clipboard,
            IFilePicker filePicker,
            MarkdownAstCache markdownCache,
            UiSettings settings,
            DateTime now)
        {
            _previewService = previewService ?? throw new ArgumentNullException(nameof(previewService));
            _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
            _filePicker = filePicker ?? throw new ArgumentNullException(nameof(filePicker));
            _markdownCache = markdownCache ?? throw new ArgumentNullException(nameof(markdownCache));

            _tabs = new ObservableCollection<DocumentTabViewModel>();

            SelectTabCommand = new RelayCommand(SelectTabFromParameter);
            CloseTabCommand = new RelayCommand(CloseTabFromParameter);
            CloseSelectedTabCommand = new RelayCommand(
                () => CloseTab(SelectedTab),
                () => SelectedTab != null);
            AddTabCommand = new RelayCommand(OpenFileFromPicker);
            RefreshCommand = new RelayCommand(ReloadSelectedTab, () => SelectedTab != null && SelectedTab.IsFileTab);
            FindCommand = new RelayCommand(ToggleFind, () => CanFind);
            CloseFindCommand = new RelayCommand(CloseFind);
            NextMatchCommand = new RelayCommand(() => MoveMatch(true), () => _findMatchCount > 0);
            PreviousMatchCommand = new RelayCommand(() => MoveMatch(false), () => _findMatchCount > 0);
            AddToConversationCommand = new RelayCommand(
                RaiseAddToConversationRequested,
                () => SelectedTab != null && SelectedTab.IsFileTab);

            Restore(settings);
        }

        public event EventHandler StateChanged;

        public event EventHandler AddToConversationRequested;

        /// <summary>文件标签建立后触发，供左栏把文件树图标变绿。</summary>
        public event EventHandler<FileOpenedEventArgs> FileOpened;

        /// <summary>最后一个同路径标签关闭后触发，左栏取消绿色图标。</summary>
        public event EventHandler<FileOpenedEventArgs> FileClosed;

        /// <summary>关闭标签后让视图把焦点放到相邻标签上。</summary>
        public event EventHandler<TabFocusRequestedEventArgs> TabFocusRequested;

        public ObservableCollection<DocumentTabViewModel> Tabs
        {
            get { return _tabs; }
        }

        public DocumentTabViewModel SelectedTab
        {
            get { return _selectedTab; }
            set
            {
                if (ReferenceEquals(_selectedTab, value))
                {
                    return;
                }

                if (_selectedTab != null)
                {
                    _selectedTab.IsSelected = false;
                }

                _selectedTab = value;

                if (_selectedTab != null)
                {
                    _selectedTab.IsSelected = true;
                }

                OnPropertyChanged();
                OnPropertyChanged(nameof(HasTabs));
                OnPropertyChanged(nameof(IsEmpty));
                OnPropertyChanged(nameof(CanFind));
                OnPropertyChanged(nameof(ShowTruncationNotice));
                OnPropertyChanged(nameof(TruncationText));
                RaiseStateChanged();

                if (IsFindOpen)
                {
                    ApplyFind();
                }

                CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool HasTabs
        {
            get { return _tabs.Count > 0; }
        }

        public bool IsEmpty
        {
            get { return _tabs.Count == 0; }
        }

        public bool CanFind
        {
            get { return _selectedTab != null && _selectedTab.Content != null && _selectedTab.Content.CanFind; }
        }

        public bool IsFindOpen
        {
            get { return _isFindOpen; }
            private set
            {
                if (SetProperty(ref _isFindOpen, value))
                {
                    OnPropertyChanged(nameof(IsFindClosed));
                }
            }
        }

        public bool IsFindClosed
        {
            get { return !_isFindOpen; }
        }

        public string FindQuery
        {
            get { return _findQuery; }
            set
            {
                if (SetProperty(ref _findQuery, value ?? string.Empty))
                {
                    ApplyFind();
                }
            }
        }

        public string FindSummary
        {
            get { return _findSummary; }
            private set { SetProperty(ref _findSummary, value); }
        }

        public bool ShowTruncationNotice
        {
            get
            {
                return _selectedTab != null
                    && _selectedTab.Content != null
                    && _selectedTab.Content.IsTruncated;
            }
        }

        public string TruncationText
        {
            get
            {
                if (_selectedTab == null || _selectedTab.Content == null)
                {
                    return string.Empty;
                }

                return string.IsNullOrEmpty(_selectedTab.Content.TruncationReason)
                    ? Strings.Truncated
                    : _selectedTab.Content.TruncationReason;
            }
        }

        public RelayCommand SelectTabCommand { get; }

        public RelayCommand CloseTabCommand { get; }

        public RelayCommand CloseSelectedTabCommand { get; }

        public RelayCommand AddTabCommand { get; }

        public RelayCommand RefreshCommand { get; }

        public RelayCommand FindCommand { get; }

        public RelayCommand CloseFindCommand { get; }

        public RelayCommand NextMatchCommand { get; }

        public RelayCommand PreviousMatchCommand { get; }

        public RelayCommand AddToConversationCommand { get; }

        /// <summary>从左栏或文件选择器打开文件。已打开时只切换标签。</summary>
        public void OpenFile(string filePath, string displayName)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            DocumentTabViewModel existing = _tabs.FirstOrDefault(
                t => t.IsFileTab
                    && string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                SelectedTab = existing;
                return;
            }

            if (_tabs.Count >= TabStripPolicy.MaxTabs)
            {
                CloseTab(_tabs[0]);
            }

            string title = string.IsNullOrWhiteSpace(displayName)
                ? Path.GetFileName(filePath)
                : displayName;

            DocumentTab model = DocumentTab.CreateFile(filePath, title);
            var tab = new DocumentTabViewModel(
                this,
                model,
                new LoadingDocumentContent(),
                filePath);

            _tabs.Add(tab);
            SelectedTab = tab;
            FileOpened?.Invoke(this, new FileOpenedEventArgs(filePath));

            OnPropertyChanged(nameof(HasTabs));
            OnPropertyChanged(nameof(IsEmpty));
            RaiseStateChanged();

            StartLoad(tab, 0);
        }

        public void OpenDiffSample()
        {
            DocumentTabViewModel existing = _tabs.FirstOrDefault(t => t.Kind == DocumentTabKind.Diff);
            if (existing != null)
            {
                SelectedTab = existing;
                return;
            }

            DocumentTab model = DocumentTab.CreateStatic(
                DocumentTabKind.Diff,
                Strings.DiffTabTitle,
                "p2-diff");
            var tab = new DocumentTabViewModel(
                this,
                model,
                LineDocumentViewModel.FromDiff(SampleDocuments.CreateDiff()),
                Strings.DiffTabTitle);

            _tabs.Add(tab);
            SelectedTab = tab;
            OnPropertyChanged(nameof(HasTabs));
            OnPropertyChanged(nameof(IsEmpty));
            RaiseStateChanged();
        }

        public void OpenCommandSample()
        {
            DocumentTabViewModel existing = _tabs.FirstOrDefault(t => t.Kind == DocumentTabKind.CommandOutput);
            if (existing != null)
            {
                SelectedTab = existing;
                return;
            }

            DocumentTab model = DocumentTab.CreateStatic(
                DocumentTabKind.CommandOutput,
                Strings.CommandOutputTabTitle,
                "p2-command");
            var tab = new DocumentTabViewModel(
                this,
                model,
                LineDocumentViewModel.FromCommand(SampleDocuments.CreateCommand()),
                Strings.CommandOutputTabTitle);

            _tabs.Add(tab);
            SelectedTab = tab;
            OnPropertyChanged(nameof(HasTabs));
            OnPropertyChanged(nameof(IsEmpty));
            RaiseStateChanged();
        }

        public void CloseTab(DocumentTabViewModel tab)
        {
            if (tab == null)
            {
                return;
            }

            int index = _tabs.IndexOf(tab);
            if (index < 0)
            {
                return;
            }

            bool wasSelected = ReferenceEquals(tab, _selectedTab);
            _tabs.RemoveAt(index);

            if (tab.IsFileTab && !_tabs.Any(t => t.IsFileTab
                && string.Equals(t.FilePath, tab.FilePath, StringComparison.OrdinalIgnoreCase)))
            {
                FileClosed?.Invoke(this, new FileOpenedEventArgs(tab.FilePath));
            }

            if (wasSelected)
            {
                int next = TabStripPolicy.GetNextSelectionAfterClose(index, _tabs.Count);
                SelectedTab = next >= 0 ? _tabs[next] : null;

                if (SelectedTab != null)
                {
                    TabFocusRequested?.Invoke(this, new TabFocusRequestedEventArgs(SelectedTab));
                }
            }

            OnPropertyChanged(nameof(HasTabs));
            OnPropertyChanged(nameof(IsEmpty));
            RaiseStateChanged();
        }

        /// <summary>文件被重命名：同步同路径标签，并重新加载预览。</summary>
        public void HandleFileRenamed(string oldPath, string newPath)
        {
            if (string.IsNullOrEmpty(oldPath) || string.IsNullOrEmpty(newPath))
            {
                return;
            }

            foreach (DocumentTabViewModel tab in _tabs.ToList())
            {
                if (!tab.IsFileTab
                    || !string.Equals(tab.FilePath, oldPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                tab.ApplyRename(newPath, Path.GetFileName(newPath));
                tab.ReplaceContent(new LoadingDocumentContent());
                StartLoad(tab, tab.Model.ScrollOffset);
                FileClosed?.Invoke(this, new FileOpenedEventArgs(oldPath));
                FileOpened?.Invoke(this, new FileOpenedEventArgs(newPath));
            }

            RaiseStateChanged();
        }

        /// <summary>文件被删除：关闭所有指向该路径的标签。</summary>
        public void HandleFileDeleted(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return;
            }

            foreach (DocumentTabViewModel tab in _tabs.ToList())
            {
                if (tab.IsFileTab
                    && string.Equals(tab.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    CloseTab(tab);
                }
            }
        }

        public void Save(UiSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            var tabs = new List<DocumentTab>();
            foreach (DocumentTabViewModel tab in _tabs)
            {
                if (tab.Content != null)
                {
                    tab.Model.ScrollOffset = tab.Content.ScrollOffset;
                }

                tabs.Add(tab.Model.Clone());
            }

            settings.OpenTabs = tabs;
            settings.SelectedTabId = _selectedTab == null ? null : _selectedTab.Id;
        }

        private void Restore(UiSettings settings)
        {
            if (settings == null || settings.OpenTabs == null)
            {
                return;
            }

            DocumentTabViewModel selected = null;

            foreach (DocumentTab model in settings.OpenTabs)
            {
                DocumentTabViewModel tab = CreateRestoredTab(model);
                if (tab == null)
                {
                    continue;
                }

                _tabs.Add(tab);

                if (string.Equals(model.Id, settings.SelectedTabId, StringComparison.Ordinal))
                {
                    selected = tab;
                }
            }

            if (selected == null && _tabs.Count > 0)
            {
                selected = _tabs[0];
            }

            SelectedTab = selected;

            if (_tabs.Count == 0)
            {
                OnPropertyChanged(nameof(HasTabs));
                OnPropertyChanged(nameof(IsEmpty));
            }
        }

        private DocumentTabViewModel CreateRestoredTab(DocumentTab model)
        {
            if (model == null)
            {
                return null;
            }

            switch (model.Kind)
            {
                case DocumentTabKind.File:
                    if (string.IsNullOrWhiteSpace(model.FilePath) || !File.Exists(model.FilePath))
                    {
                        return null;
                    }

                    var fileTab = new DocumentTabViewModel(
                        this,
                        model,
                        new LoadingDocumentContent(),
                        model.FilePath);
                    FileOpened?.Invoke(this, new FileOpenedEventArgs(model.FilePath));
                    StartLoad(fileTab, model.ScrollOffset);
                    return fileTab;

                case DocumentTabKind.Diff:
                {
                    var content = LineDocumentViewModel.FromDiff(SampleDocuments.CreateDiff());
                    content.ScrollOffset = model.ScrollOffset;
                    return new DocumentTabViewModel(this, model, content, Strings.DiffTabTitle);
                }

                case DocumentTabKind.CommandOutput:
                {
                    var content = LineDocumentViewModel.FromCommand(SampleDocuments.CreateCommand());
                    content.ScrollOffset = model.ScrollOffset;
                    return new DocumentTabViewModel(this, model, content, Strings.CommandOutputTabTitle);
                }

                default:
                    return null;
            }
        }

        private void StartLoad(DocumentTabViewModel tab, double scrollOffset)
        {
            _ = LoadFileCoreAsync(tab, scrollOffset);
        }

        private async Task LoadFileCoreAsync(DocumentTabViewModel tab, double scrollOffset)
        {
            try
            {
                FilePreview preview = await _previewService.LoadAsync(
                    tab.FilePath,
                    CancellationToken.None);

                DocumentContentViewModel content = await CreateContentAsync(tab.FilePath, preview);
                content.ScrollOffset = scrollOffset;
                tab.ReplaceContent(content);
                tab.NotifyMetaChanged();

                if (ReferenceEquals(tab, _selectedTab))
                {
                    OnPropertyChanged(nameof(SelectedTab));
                    OnPropertyChanged(nameof(CanFind));
                    OnPropertyChanged(nameof(ShowTruncationNotice));
                    OnPropertyChanged(nameof(TruncationText));

                    if (IsFindOpen)
                    {
                        ApplyFind();
                    }
                }

                CommandManager.InvalidateRequerySuggested();
                RaiseStateChanged();
            }
            catch (Exception ex)
            {
                tab.ReplaceContent(new ErrorDocumentContent("读取失败：" + ex.Message));
                tab.NotifyMetaChanged();
            }
        }

        private async Task<DocumentContentViewModel> CreateContentAsync(string filePath, FilePreview preview)
        {
            if (preview == null)
            {
                return new ErrorDocumentContent(Strings.FileMissingError);
            }

            if (preview.HasError)
            {
                return new ErrorDocumentContent(preview.ErrorMessage);
            }

            FilePreviewKind kind = FileTreePolicy.GetPreviewKind(filePath);

            if (kind == FilePreviewKind.Image)
            {
                return new ImageDocumentViewModel(filePath);
            }

            if (preview.IsBinary)
            {
                return new BinaryDocumentContent(filePath, preview.ByteLength);
            }

            if (kind == FilePreviewKind.Markdown)
            {
                try
                {
                    var info = new FileInfo(filePath);
                    MarkdownDocument document;

                    if (preview.Content != null && preview.Content.Length > 256 * 1024)
                    {
                        // 大文档解析放后台线程，UI 只接收结果。
                        document = await Task.Run(() => _markdownCache.GetOrParse(
                            filePath,
                            info.LastWriteTimeUtc,
                            info.Length,
                            preview.Content));
                    }
                    else
                    {
                        document = _markdownCache.GetOrParse(
                            filePath,
                            info.LastWriteTimeUtc,
                            info.Length,
                            preview.Content);
                    }

                    return new MarkdownDocumentViewModel(preview, document, _clipboard);
                }
                catch (Exception ex)
                {
                    return new ErrorDocumentContent("Markdown 解析失败：" + ex.Message);
                }
            }

            return LineDocumentViewModel.FromText(
                preview,
                FileTreePolicy.IsCodeFile(filePath));
        }

        private void ReloadSelectedTab()
        {
            DocumentTabViewModel tab = _selectedTab;
            if (tab == null || !tab.IsFileTab)
            {
                return;
            }

            double offset = tab.Content == null ? 0 : tab.Content.ScrollOffset;
            tab.ReplaceContent(new LoadingDocumentContent(tab.MetaText));
            tab.NotifyMetaChanged();
            OnPropertyChanged(nameof(CanFind));
            OnPropertyChanged(nameof(ShowTruncationNotice));
            OnPropertyChanged(nameof(TruncationText));
            StartLoad(tab, offset);
        }

        private void OpenFileFromPicker()
        {
            string file = _filePicker.PickFile(
                Strings.OpenFileDialogTitle,
                null,
                "文本与 Markdown|*.md;*.markdown;*.txt;*.json;*.cs;*.xaml;*.xml;*.log;*.ps1;*.config;*.yml;*.yaml;*.sql;*.html;*.css;*.js;*.ts|图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件|*.*");

            if (!string.IsNullOrEmpty(file))
            {
                OpenFile(file, Path.GetFileName(file));
            }
        }

        private void SelectTabFromParameter(object parameter)
        {
            var tab = parameter as DocumentTabViewModel;
            if (tab != null)
            {
                SelectedTab = tab;
                return;
            }

            string id = parameter as string;
            if (!string.IsNullOrEmpty(id))
            {
                SelectedTab = _tabs.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));
            }
        }

        private void CloseTabFromParameter(object parameter)
        {
            CloseTab(parameter as DocumentTabViewModel);
        }

        private void ToggleFind()
        {
            if (!CanFind)
            {
                return;
            }

            IsFindOpen = !IsFindOpen;
            if (IsFindOpen)
            {
                ApplyFind();
            }
            else
            {
                CloseFind();
            }
        }

        private void CloseFind()
        {
            IsFindOpen = false;
            FindSummary = string.Empty;
            _findMatchCount = 0;
            _findMatchIndex = -1;

            if (_selectedTab != null && _selectedTab.Content != null)
            {
                _selectedTab.Content.ClearFind();
            }

            FindQuery = string.Empty;
        }

        private void ApplyFind()
        {
            if (_selectedTab == null || _selectedTab.Content == null || !_selectedTab.Content.CanFind)
            {
                _findMatchCount = 0;
                _findMatchIndex = -1;
                FindSummary = string.Empty;
                return;
            }

            _findMatchCount = _selectedTab.Content.Find(FindQuery);
            _findMatchIndex = _findMatchCount > 0 ? 0 : -1;

            if (_findMatchCount > 0)
            {
                _selectedTab.Content.GoToFindMatch(_findMatchIndex);
            }

            UpdateFindSummary();
        }

        private void MoveMatch(bool forward)
        {
            if (_findMatchCount <= 0 || _selectedTab == null || _selectedTab.Content == null)
            {
                return;
            }

            _findMatchIndex = TextSearchPolicy.NextIndex(_findMatchIndex, _findMatchCount, forward);
            _selectedTab.Content.GoToFindMatch(_findMatchIndex);
            UpdateFindSummary();
        }

        private void UpdateFindSummary()
        {
            if (string.IsNullOrWhiteSpace(FindQuery))
            {
                FindSummary = string.Empty;
                return;
            }

            if (_findMatchCount <= 0)
            {
                FindSummary = Strings.FindNoMatches;
                return;
            }

            FindSummary = Strings.FindPosition(_findMatchIndex + 1, _findMatchCount);
        }

        private void RaiseAddToConversationRequested()
        {
            AddToConversationRequested?.Invoke(this, EventArgs.Empty);
        }

        private void RaiseStateChanged()
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public sealed class TabFocusRequestedEventArgs : EventArgs
    {
        public TabFocusRequestedEventArgs(DocumentTabViewModel tab)
        {
            Tab = tab;
        }

        public DocumentTabViewModel Tab { get; }
    }

    public sealed class FileOpenedEventArgs : EventArgs
    {
        public FileOpenedEventArgs(string filePath)
        {
            FilePath = filePath;
        }

        public string FilePath { get; }
    }
}
