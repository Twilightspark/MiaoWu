using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using MiaoWu.App.Mvvm;
using MiaoWu.App.Services;
using MiaoWu.Core.Conversations;
using MiaoWu.Core.Skills;

namespace MiaoWu.App.ViewModels
{
    /// <summary>能力市场列表里的一条技能。开关只改当前工作区的启用状态，不改变行顺序。</summary>
    public sealed class SkillRowViewModel : ObservableObject
    {
        private readonly MarketViewModel _owner;

        public SkillRowViewModel(MarketViewModel owner, SkillDescriptor model)
        {
            _owner = owner;
            Model = model;
            ToggleEnabledCommand = new RelayCommand(() => _owner.ToggleEnabled(this));
        }

        public SkillDescriptor Model { get; }

        public string Key
        {
            get { return Model.Key; }
        }

        public string Name
        {
            get { return Model.Name; }
        }

        public string Description
        {
            get { return Model.Description; }
        }

        public string SourceLabel
        {
            get { return Model.Source == SkillSource.Workspace ? "工作区" : "用户级"; }
        }

        public bool IsEnabled
        {
            get { return Model.IsEnabled; }
        }

        public bool IsValid
        {
            get { return Model.IsValid; }
        }

        public bool CanToggle
        {
            get { return _owner.HasWorkspace && Model.IsValid; }
        }

        public bool HasWarning
        {
            get { return !Model.IsValid || (Model.Diagnostics != null && Model.Diagnostics.Count > 0); }
        }

        public string Subtitle
        {
            get
            {
                if (!Model.IsValid)
                {
                    return "无法加载 · " + (string.IsNullOrWhiteSpace(Model.InvalidReason) ? "读取失败" : Model.InvalidReason);
                }

                string prefix = Model.IsEnabled ? string.Empty : "已停用 · ";
                return prefix + (string.IsNullOrWhiteSpace(Model.Description) ? "没有描述" : Model.Description);
            }
        }

        public string AutomationName
        {
            get
            {
                string state = !Model.IsValid ? "无法加载" : (Model.IsEnabled ? "已启用" : "已停用");
                return Model.Name + "，" + state + "，" + SourceLabel + "技能，" + Description;
            }
        }

        public RelayCommand ToggleEnabledCommand { get; }

        public void Refresh()
        {
            OnPropertyChanged(nameof(IsEnabled));
            OnPropertyChanged(nameof(Subtitle));
            OnPropertyChanged(nameof(AutomationName));
            OnPropertyChanged(nameof(CanToggle));
            OnPropertyChanged(nameof(HasWarning));
        }
    }

    /// <summary>详情页签。</summary>
    public sealed class MarketTabOption
    {
        public MarketTabOption(int index, string label)
        {
            Index = index;
            Label = label;
        }

        public int Index { get; }

        public string Label { get; }
    }

    /// <summary>「包含文件」的一行。</summary>
    public sealed class SkillFileRowViewModel
    {
        public SkillFileRowViewModel(SkillFileEntry entry)
        {
            RelativePath = entry.RelativePath;
            FullPath = entry.FullPath;
            SizeText = MarketViewModel.FormatSize(entry.SizeBytes);
        }

        public string RelativePath { get; }

        public string FullPath { get; }

        public string SizeText { get; }
    }

    /// <summary>技能详情：概览与只读正文。所有写动作都回到 <see cref="MarketViewModel"/>。</summary>
    public sealed class SkillDetailViewModel : ObservableObject
    {
        private static readonly MarketTabOption[] TabOptions =
        {
            new MarketTabOption(0, "概览"),
            new MarketTabOption(1, "正文")
        };

        private readonly MarketViewModel _owner;
        private MarketTabOption _selectedTab = TabOptions[0];
        private string _bodyText;
        private string _bodyError;
        private bool _bodyLoaded;

        public SkillDetailViewModel(MarketViewModel owner, SkillRowViewModel row)
        {
            _owner = owner;
            Row = row;

            ToggleEnabledCommand = new RelayCommand(() => _owner.ToggleEnabled(Row));
            OpenFolderCommand = new RelayCommand(() => _owner.OpenFolder(Row));
            OpenInEditorCommand = new RelayCommand(() => _owner.OpenInEditor(Row));
            CopyPathCommand = new RelayCommand(() => _owner.CopyPath(Row));
            OpenFileCommand = new RelayCommand(parameter => _owner.OpenFile(parameter as SkillFileRowViewModel));
        }

        public SkillRowViewModel Row { get; }

        public SkillDescriptor Model
        {
            get { return Row.Model; }
        }

        public string Name
        {
            get { return Model.Name; }
        }

        public string Description
        {
            get
            {
                return string.IsNullOrWhiteSpace(Model.Description)
                    ? "没有描述。模型无法判断什么时候加载这个技能。"
                    : Model.Description;
            }
        }

        public bool HasDescription
        {
            get { return !string.IsNullOrWhiteSpace(Model.Description); }
        }

        public string SourceLabel
        {
            get { return Model.Source == SkillSource.Workspace ? "工作区技能" : "用户级技能"; }
        }

        public string LocationText
        {
            get { return _owner.DisplayPath(Model.BodyPath); }
        }

        public string UpdatedText
        {
            get { return Model.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture); }
        }

        public string SizeText
        {
            get
            {
                string size = MarketViewModel.FormatSize(Model.SizeBytes);
                return Model.LineCount > 0
                    ? size + " · " + Model.LineCount.ToString(CultureInfo.InvariantCulture) + " 行"
                    : size;
            }
        }

        public string EnabledHint
        {
            get { return "停用后模型看不到这个技能，文件不会被删除。"; }
        }

        public bool IsEnabled
        {
            get { return Model.IsEnabled; }
        }

        public bool CanToggle
        {
            get { return _owner.HasWorkspace && Model.IsValid; }
        }

        public IReadOnlyList<MarketTabOption> Tabs
        {
            get { return TabOptions; }
        }

        public MarketTabOption SelectedTab
        {
            get { return _selectedTab; }
            set
            {
                if (value == null || ReferenceEquals(_selectedTab, value))
                {
                    return;
                }

                _selectedTab = value;
                OnPropertyChanged(nameof(SelectedTab));
                OnPropertyChanged(nameof(IsOverview));
                OnPropertyChanged(nameof(IsBody));

                if (value.Index == 1)
                {
                    EnsureBody();
                }
            }
        }

        public bool IsOverview
        {
            get { return _selectedTab.Index == 0; }
        }

        public bool IsBody
        {
            get { return _selectedTab.Index == 1; }
        }

        public string BodyText
        {
            get
            {
                EnsureBody();
                return _bodyText ?? string.Empty;
            }
        }

        public string BodyError
        {
            get
            {
                EnsureBody();
                return _bodyError ?? string.Empty;
            }
        }

        public bool HasBodyError
        {
            get { return !string.IsNullOrEmpty(BodyError); }
        }

        public IReadOnlyList<SkillFileRowViewModel> Files
        {
            get
            {
                if (Model.Files == null)
                {
                    return new SkillFileRowViewModel[0];
                }

                return Model.Files
                    .Take(5)
                    .Select(entry => new SkillFileRowViewModel(entry))
                    .ToList();
            }
        }

        public int ExtraFileCount
        {
            get
            {
                if (Model.Files == null || Model.Files.Count <= 5)
                {
                    return 0;
                }

                return Model.Files.Count - 5;
            }
        }

        public bool HasExtraFiles
        {
            get { return ExtraFileCount > 0; }
        }

        public string ExtraFilesText
        {
            get { return "还有 " + ExtraFileCount + " 个文件"; }
        }

        public bool HasFiles
        {
            get { return Files.Count > 0; }
        }

        public IReadOnlyList<SkillDiagnostic> Diagnostics
        {
            get
            {
                var list = new List<SkillDiagnostic>();
                if (!Model.IsValid)
                {
                    list.Add(new SkillDiagnostic(
                        SkillDiagnosticLevel.Error,
                        "skill.invalid",
                        "无法加载：" + (string.IsNullOrWhiteSpace(Model.InvalidReason) ? "读取失败" : Model.InvalidReason),
                        Model.BodyPath));
                }

                if (Model.Diagnostics != null)
                {
                    list.AddRange(Model.Diagnostics);
                }

                return list;
            }
        }

        public bool HasDiagnostics
        {
            get { return Diagnostics.Count > 0; }
        }

        public RelayCommand ToggleEnabledCommand { get; }

        public RelayCommand OpenFolderCommand { get; }

        public RelayCommand OpenInEditorCommand { get; }

        public RelayCommand CopyPathCommand { get; }

        public RelayCommand OpenFileCommand { get; }

        /// <summary>开关在别处被切换后刷新绑定。</summary>
        public void RefreshEnabled()
        {
            OnPropertyChanged(nameof(IsEnabled));
            OnPropertyChanged(nameof(CanToggle));
        }

        private void EnsureBody()
        {
            if (_bodyLoaded)
            {
                return;
            }

            _bodyLoaded = true;
            try
            {
                _bodyText = _owner.ReadBody(Row);
            }
            catch (Exception exception)
            {
                _bodyError = "正文读不出来：" + exception.Message;
            }

            OnPropertyChanged(nameof(BodyText));
            OnPropertyChanged(nameof(BodyError));
            OnPropertyChanged(nameof(HasBodyError));
        }
    }

    /// <summary>
    /// 能力市场页面：工作区技能的目录、启用状态与详情。没有范围切换、没有筛选 chip；
    /// 用户级技能与工作区技能一起列出，靠来源标签区分。导入以文件夹为单位。
    /// </summary>
    public sealed class MarketViewModel : ObservableObject, IDisposable
    {
        private readonly ISkillCatalogService _catalog;
        private readonly Func<string> _workspaceRoot;
        private readonly Func<string> _workspaceName;
        private readonly IFolderPicker _folderPicker;
        private readonly IClipboardService _clipboard;
        private readonly Action<string> _openPath;
        private readonly Action<string> _openFolder;

        private readonly List<SkillRowViewModel> _all = new List<SkillRowViewModel>();
        private IReadOnlyList<SkillDiagnostic> _catalogDiagnostics;
        private SkillRowViewModel _selected;
        private SkillDetailViewModel _detail;
        private string _searchText = string.Empty;
        private string _statusMessage = string.Empty;
        private bool _isOpen;
        private bool _hasWorkspace;
        private bool _disposed;

        public MarketViewModel(
            ISkillCatalogService catalog,
            Func<string> workspaceRoot,
            Func<string> workspaceName,
            IFolderPicker folderPicker,
            IClipboardService clipboard,
            Action<string> openPath,
            Action<string> openFolder)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _workspaceRoot = workspaceRoot ?? (() => null);
            _workspaceName = workspaceName ?? (() => null);
            _folderPicker = folderPicker;
            _clipboard = clipboard;
            _openPath = openPath;
            _openFolder = openFolder;

            Items = new ObservableCollection<SkillRowViewModel>();

            ImportCommand = new RelayCommand(Import, () => HasWorkspace);
            ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty);
            RefreshCommand = new RelayCommand(() => Reload(Selected == null ? null : Selected.Key));
            CloseCommand = new RelayCommand(Close);
            CollapseCommand = new RelayCommand(() => Selected = null, () => Selected != null);
            OpenSkillsFolderCommand = new RelayCommand(OpenWorkspaceSkillsFolder, () => HasWorkspace);
            OpenUserSkillsFolderCommand = new RelayCommand(OpenUserSkillsFolder);
            EscapeCommand = new RelayCommand(Escape);

            _catalogDiagnostics = new List<SkillDiagnostic>();
        }

        public ObservableCollection<SkillRowViewModel> Items { get; }

        public bool IsOpen
        {
            get { return _isOpen; }
            private set { SetProperty(ref _isOpen, value); }
        }

        public bool HasWorkspace
        {
            get { return _hasWorkspace; }
        }

        public string WorkspaceRoot
        {
            get { return _workspaceRoot(); }
        }

        public string UserSkillsDirectory
        {
            get { return _catalog.UserSkillsDirectory; }
        }

        public string SearchText
        {
            get { return _searchText; }
            set
            {
                if (SetProperty(ref _searchText, value ?? string.Empty))
                {
                    ApplySearch(null);
                    _statusMessage = string.Empty;
                    OnPropertyChanged(nameof(StatusText));
                    OnPropertyChanged(nameof(ShowEmptyImport));
                    OnPropertyChanged(nameof(ShowEmptyClear));
                }
            }
        }

        public SkillRowViewModel Selected
        {
            get { return _selected; }
            set
            {
                if (ReferenceEquals(_selected, value))
                {
                    return;
                }

                _selected = value;
                _detail = value == null ? null : new SkillDetailViewModel(this, value);
                OnPropertyChanged(nameof(Selected));
                OnPropertyChanged(nameof(Detail));
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(IsDetailCollapsed));
                _statusMessage = string.Empty;
                OnPropertyChanged(nameof(StatusText));
                CollapseCommand.RaiseCanExecuteChanged();
            }
        }

        public SkillDetailViewModel Detail
        {
            get { return _detail; }
        }

        public bool HasSelection
        {
            get { return _selected != null; }
        }

        public bool IsDetailCollapsed
        {
            get { return _selected == null; }
        }

        public bool HasNoItems
        {
            get { return Items.Count == 0; }
        }

        public bool ShowEmpty
        {
            get { return Items.Count == 0; }
        }

        public string EmptyTitle
        {
            get
            {
                if (!HasWorkspace)
                {
                    return "先选择一个工作区";
                }

                if (!string.IsNullOrWhiteSpace(_searchText))
                {
                    return "没有匹配的技能";
                }

                return "这个工作区还没有技能";
            }
        }

        public string EmptyText
        {
            get
            {
                if (!HasWorkspace)
                {
                    return "技能按工作区启用；导入技能需要先选择一个工作区。";
                }

                if (!string.IsNullOrWhiteSpace(_searchText))
                {
                    return "没有匹配「" + _searchText.Trim() + "」的技能。清除搜索后再试。";
                }

                return "技能是一份按需读取的说明；模型只在任务匹配时打开它。";
            }
        }

        public bool ShowEmptyImport
        {
            get { return HasWorkspace && string.IsNullOrWhiteSpace(_searchText); }
        }

        public bool ShowEmptyClear
        {
            get { return !string.IsNullOrWhiteSpace(_searchText); }
        }

        public bool ShowEmptyBrand
        {
            get { return !HasWorkspace || string.IsNullOrWhiteSpace(_searchText); }
        }

        public string StatusText
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(_statusMessage))
                {
                    return _statusMessage;
                }

                int enabled = 0;
                int invalid = 0;
                for (int i = 0; i < _all.Count; i++)
                {
                    SkillRowViewModel row = _all[i];
                    if (!row.IsValid || (row.Model.Diagnostics != null && row.Model.Diagnostics.Count > 0))
                    {
                        invalid++;
                    }
                    else if (row.IsEnabled)
                    {
                        enabled++;
                    }
                }

                string workspace = _workspaceName();
                string prefix = string.IsNullOrWhiteSpace(workspace) ? string.Empty : workspace + " · ";
                string text = prefix + _all.Count.ToString(CultureInfo.InvariantCulture) + " 个技能 · "
                    + enabled.ToString(CultureInfo.InvariantCulture) + " 个已启用";
                if (invalid > 0)
                {
                    text += " · " + invalid.ToString(CultureInfo.InvariantCulture) + " 个需处理";
                }

                return text;
            }
        }

        public string LoadError
        {
            get
            {
                if (_catalogDiagnostics == null || _catalogDiagnostics.Count == 0)
                {
                    return string.Empty;
                }

                return _catalogDiagnostics[0].Message;
            }
        }

        public bool HasLoadError
        {
            get { return !string.IsNullOrEmpty(LoadError); }
        }

        public RelayCommand ImportCommand { get; }

        public RelayCommand ClearSearchCommand { get; }

        public RelayCommand RefreshCommand { get; }

        public RelayCommand CloseCommand { get; }

        public RelayCommand CollapseCommand { get; }

        public RelayCommand OpenSkillsFolderCommand { get; }

        public RelayCommand OpenUserSkillsFolderCommand { get; }

        public RelayCommand EscapeCommand { get; }

        /// <summary>打开页面并重新扫描；不自动选中任何技能。</summary>
        public void Open()
        {
            IsOpen = true;
            _statusMessage = string.Empty;
            Selected = null;
            _searchText = string.Empty;
            OnPropertyChanged(nameof(SearchText));
            Reload(null);
        }

        public void Close()
        {
            IsOpen = false;
            _statusMessage = string.Empty;
        }

        /// <summary>重新扫描目录；<paramref name="selectKey"/> 指定刷新后仍要选中的技能。</summary>
        public void Reload(string selectKey)
        {
            if (_disposed)
            {
                return;
            }

            string root = _workspaceRoot();
            _hasWorkspace = !string.IsNullOrWhiteSpace(root);

            SkillCatalog catalog = _catalog.Load(root);
            _catalogDiagnostics = catalog.Diagnostics ?? new SkillDiagnostic[0];

            string keep = selectKey ?? (Selected == null ? null : Selected.Key);
            _all.Clear();
            for (int i = 0; i < catalog.Skills.Count; i++)
            {
                SkillDescriptor skill = catalog.Skills[i];
                if (skill != null)
                {
                    _all.Add(new SkillRowViewModel(this, skill));
                }
            }

            ApplySearch(keep);
            OnPropertyChanged(nameof(HasWorkspace));
            OnPropertyChanged(nameof(HasNoItems));
            OnPropertyChanged(nameof(ShowEmpty));
            OnPropertyChanged(nameof(EmptyTitle));
            OnPropertyChanged(nameof(EmptyText));
            OnPropertyChanged(nameof(ShowEmptyImport));
            OnPropertyChanged(nameof(ShowEmptyClear));
            OnPropertyChanged(nameof(ShowEmptyBrand));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(LoadError));
            OnPropertyChanged(nameof(HasLoadError));
            OnPropertyChanged(nameof(UserSkillsDirectory));
            ImportCommand.RaiseCanExecuteChanged();
            OpenSkillsFolderCommand.RaiseCanExecuteChanged();
            CollapseCommand.RaiseCanExecuteChanged();
        }

        /// <summary>切换当前工作区的启用状态；不重排列表。</summary>
        public void ToggleEnabled(SkillRowViewModel row)
        {
            if (row == null || !HasWorkspace || !row.Model.IsValid)
            {
                return;
            }

            string root = _workspaceRoot();
            bool enable = !row.Model.IsEnabled;
            if (!_catalog.SetEnabled(root, row.Model.Name, enable))
            {
                _statusMessage = "启用状态保存失败。";
                row.Refresh();
                if (_detail != null && ReferenceEquals(_detail.Row, row))
                {
                    _detail.RefreshEnabled();
                }

                OnPropertyChanged(nameof(StatusText));
                return;
            }

            row.Model.IsEnabled = enable;
            row.Refresh();
            if (_detail != null && ReferenceEquals(_detail.Row, row))
            {
                _detail.RefreshEnabled();
            }

            _statusMessage = enable ? "已启用「" + row.Model.Name + "」。" : "已停用「" + row.Model.Name + "」。";
            OnPropertyChanged(nameof(StatusText));
        }

        /// <summary>导入 skill 文件夹；成功后选中新技能。</summary>
        public void Import()
        {
            if (!HasWorkspace)
            {
                _statusMessage = "先选择一个工作区，再导入技能。";
                OnPropertyChanged(nameof(StatusText));
                return;
            }

            string folder = _folderPicker == null
                ? null
                : _folderPicker.PickFolder("选择技能文件夹（文件夹里要有 SKILL.md）", _workspaceRoot());
            if (string.IsNullOrWhiteSpace(folder))
            {
                return;
            }

            SkillImportResult result = _catalog.Import(_workspaceRoot(), folder);
            _statusMessage = result.Message;
            if (result.Status == SkillImportStatus.Imported)
            {
                Reload(result.Name);
                SelectByKey("w:" + (result.Name ?? string.Empty).ToLowerInvariant());
                _statusMessage = result.Message;
            }

            OnPropertyChanged(nameof(StatusText));
        }

        public string ReadBody(SkillRowViewModel row)
        {
            return row == null ? string.Empty : _catalog.ReadBody(row.Model);
        }

        public string DisplayPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            string root = _workspaceRoot();
            if (string.IsNullOrWhiteSpace(root))
            {
                return path;
            }

            try
            {
                string normalizedRoot = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string normalizedPath = Path.GetFullPath(path);
                if (normalizedPath.StartsWith(
                    normalizedRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return normalizedPath.Substring(normalizedRoot.Length + 1);
                }
            }
            catch (Exception)
            {
            }

            return path;
        }

        public void OpenFolder(SkillRowViewModel row)
        {
            if (row == null)
            {
                return;
            }

            string folder = !string.IsNullOrWhiteSpace(row.Model.DirectoryPath)
                ? row.Model.DirectoryPath
                : Path.GetDirectoryName(row.Model.BodyPath);
            if (!string.IsNullOrWhiteSpace(folder) && _openFolder != null)
            {
                _openFolder(folder);
            }
        }

        public void OpenInEditor(SkillRowViewModel row)
        {
            if (row != null && !string.IsNullOrWhiteSpace(row.Model.BodyPath) && _openPath != null)
            {
                _openPath(row.Model.BodyPath);
            }
        }

        public void OpenFile(SkillFileRowViewModel file)
        {
            if (file != null && !string.IsNullOrWhiteSpace(file.FullPath) && _openPath != null)
            {
                _openPath(file.FullPath);
            }
        }

        public void CopyPath(SkillRowViewModel row)
        {
            if (row == null || _clipboard == null)
            {
                return;
            }

            if (_clipboard.SetText(row.Model.BodyPath))
            {
                _statusMessage = "已复制路径。";
                OnPropertyChanged(nameof(StatusText));
            }
        }

        public void OpenWorkspaceSkillsFolder()
        {
            string root = _workspaceRoot();
            if (string.IsNullOrWhiteSpace(root))
            {
                return;
            }

            string directory = AgentWorkspaceLayout.GetSkillsDirectory(root);
            Directory.CreateDirectory(directory);
            if (_openFolder != null)
            {
                _openFolder(directory);
            }
        }

        public void OpenUserSkillsFolder()
        {
            string directory = _catalog.UserSkillsDirectory;
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            Directory.CreateDirectory(directory);
            if (_openFolder != null)
            {
                _openFolder(directory);
            }
        }

        /// <summary>Esc：详情打开时先收起详情，再按一次关闭页面。</summary>
        public void Escape()
        {
            if (!IsOpen)
            {
                return;
            }

            if (Selected != null)
            {
                Selected = null;
                return;
            }

            Close();
        }

        /// <summary>当工作区变化后保持页面打开并重新扫描。</summary>
        public void OnWorkspaceChanged()
        {
            if (!IsOpen)
            {
                return;
            }

            Selected = null;
            Reload(null);
        }

        public void Dispose()
        {
            _disposed = true;
        }

        public static string FormatSize(long bytes)
        {
            if (bytes < 1024)
            {
                return bytes.ToString(CultureInfo.InvariantCulture) + " B";
            }

            double value = bytes / 1024.0;
            if (value < 1024)
            {
                return value.ToString("0.#", CultureInfo.InvariantCulture) + " KB";
            }

            return (value / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
        }

        private void ApplySearch(string keepKey)
        {
            string query = (_searchText ?? string.Empty).Trim();
            var visible = new List<SkillRowViewModel>();
            for (int i = 0; i < _all.Count; i++)
            {
                SkillRowViewModel row = _all[i];
                if (Matches(row, query))
                {
                    visible.Add(row);
                }
            }

            Items.Clear();
            for (int i = 0; i < visible.Count; i++)
            {
                Items.Add(visible[i]);
            }

            OnPropertyChanged(nameof(HasNoItems));
            OnPropertyChanged(nameof(ShowEmpty));
            OnPropertyChanged(nameof(EmptyTitle));
            OnPropertyChanged(nameof(EmptyText));
            OnPropertyChanged(nameof(StatusText));

            SkillRowViewModel target = null;
            if (!string.IsNullOrEmpty(keepKey))
            {
                for (int i = 0; i < Items.Count; i++)
                {
                    if (string.Equals(Items[i].Key, keepKey, StringComparison.OrdinalIgnoreCase))
                    {
                        target = Items[i];
                        break;
                    }
                }
            }

            if (!ReferenceEquals(_selected, target))
            {
                Selected = target;
            }
        }

        private void SelectByKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            for (int i = 0; i < Items.Count; i++)
            {
                if (string.Equals(Items[i].Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    Selected = Items[i];
                    return;
                }
            }
        }

        private static bool Matches(SkillRowViewModel row, string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return true;
            }

            string needle = query.ToLowerInvariant();
            if ((row.Name ?? string.Empty).ToLowerInvariant().Contains(needle))
            {
                return true;
            }

            if ((row.Description ?? string.Empty).ToLowerInvariant().Contains(needle))
            {
                return true;
            }

            return (row.Model.BodyPath ?? string.Empty).ToLowerInvariant().Contains(needle);
        }
    }
}
