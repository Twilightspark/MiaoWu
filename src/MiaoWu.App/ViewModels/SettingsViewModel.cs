using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using MiaoWu.App.Mvvm;
using MiaoWu.App.Services;
using MiaoWu.Core;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Core.Abstractions;
using MiaoWu.Core.Models;
using MiaoWu.Core.Settings;
using MiaoWu.Infrastructure.Providers;

namespace MiaoWu.App.ViewModels
{
    public enum SettingsPage
    {
        General,
        Models,
        About
    }

    public sealed class SettingsNavItem
    {
        public SettingsNavItem(SettingsPage page, string title)
        {
            Page = page;
            Title = title;
        }

        public SettingsPage Page { get; }

        public string Title { get; }
    }

    /// <summary>设置页“关闭窗口时”的选项；三项并列，Ask（每次询问）为默认。</summary>
    public sealed class CloseActionChoice
    {
        public CloseActionChoice(CloseWindowAction action, string title)
        {
            Action = action;
            Title = title;
        }

        public CloseWindowAction Action { get; }

        public string Title { get; }
    }

    /// <summary>
    /// 设置页“工具审批”的选项。Ask 为默认（有风险就弹通知等确认）；Never 只对已判定为
    /// 需要审批的动作自动放行，Blocked 级风险仍然直接拒绝。
    /// </summary>
    public sealed class ApprovalPolicyChoice
    {
        public ApprovalPolicyChoice(ToolApprovalPolicy policy, string title)
        {
            Policy = policy;
            Title = title;
        }

        public ToolApprovalPolicy Policy { get; }

        public string Title { get; }
    }

    /// <summary>
    /// 设置页“目标续跑”的选项。开启后，一轮正常结束且会话目标仍在进行中时，
    /// 自动接一轮 continuation，直到目标完成或达到轮次预算。
    /// </summary>
    public sealed class GoalContinuationChoice
    {
        public GoalContinuationChoice(bool enabled, string title)
        {
            Enabled = enabled;
            Title = title;
        }

        public bool Enabled { get; }

        public string Title { get; }
    }

    /// <summary>预留的外观选项，写入 ui.theme；当前只保存选择，不切换主题。</summary>
    public sealed class ThemeChoice
    {
        public ThemeChoice(string value, string title)
        {
            Value = value;
            Title = title;
        }

        public string Value { get; }

        public string Title { get; }
    }

    /// <summary>预留的语言选项，写入 ui.language；当前只保存选择，不切换语言。</summary>
    public sealed class LanguageChoice
    {
        public LanguageChoice(string value, string title)
        {
            Value = value;
            Title = title;
        }

        public string Value { get; }

        public string Title { get; }
    }

    /// <summary>添加供应商表单的两种入口。</summary>
    public enum ProviderFormMode
    {
        ThirdParty,
        Custom
    }

    public sealed class ProviderFormModeChoice
    {
        public ProviderFormModeChoice(ProviderFormMode mode, string title)
        {
            Mode = mode;
            Title = title;
        }

        public ProviderFormMode Mode { get; }

        public string Title { get; }
    }

    /// <summary>第三方供应商预设；只提供目录与默认模型，密钥单独填写。</summary>
    public sealed class ProviderPresetChoice
    {
        public ProviderPresetChoice(ProviderDefinition definition)
        {
            Definition = definition;
            DisplayName = definition.DisplayName;
            BaseUrl = definition.BaseUrl;
            ModelCountText = definition.Models.Count + " 个模型";
        }

        public ProviderDefinition Definition { get; }

        public string DisplayName { get; }

        public string BaseUrl { get; }

        public string ModelCountText { get; }
    }

    /// <summary>
    /// 设置浮层：通用 / 模型 / 关于三页。通用页即时保存；模型页单列展示
    /// 已配置供应商，点击卡片或虚线添加按钮后在下方展开表单：第三方预设只填
    /// API Key，自定义供应商填写完整字段并手动添加模型卡片。
    /// 不新开 Window；Esc 关闭由 SettingsView 处理。
    /// </summary>
    public sealed class SettingsViewModel : ObservableObject
    {
        private readonly AppServices _app;
        private readonly System.Windows.Threading.Dispatcher _dispatcher;

        private SettingsNavItem _selectedPage;
        private ProviderEditorViewModel _draftProvider;
        private ProviderEditorViewModel _editingProvider;
        private ProviderFormModeChoice _selectedProviderFormMode;
        private ProviderPresetChoice _selectedPreset;
        private string _thirdPartyApiKey;
        private bool _isAddingProvider;
        private bool _isOpen;
        private string _statusText;
        private string _providerStatus;
        private string _providerFormStatus;
        private string _updateStatus;
        private bool _isTesting;
        private bool _isFetchingModels;
        private string _modelFetchStatus;

        public SettingsViewModel(AppServices app)
        {
            _app = app ?? throw new ArgumentNullException(nameof(app));
            _dispatcher = Application.Current == null
                ? System.Windows.Threading.Dispatcher.CurrentDispatcher
                : Application.Current.Dispatcher;

            Pages = new ObservableCollection<SettingsNavItem>
            {
                new SettingsNavItem(SettingsPage.General, "通用"),
                new SettingsNavItem(SettingsPage.Models, "模型"),
                new SettingsNavItem(SettingsPage.About, "关于")
            };

            ProviderEditors = new ObservableCollection<ProviderEditorViewModel>();
            ThemeOptions = new[]
            {
                new ThemeChoice("light", "浅色"),
                new ThemeChoice("dark", "深色"),
                new ThemeChoice("system", "跟随系统")
            };
            LanguageOptions = new[]
            {
                new LanguageChoice("zh-CN", "中文"),
                new LanguageChoice("en-US", "英文")
            };
            // 三个出口并列：询问（默认）/ 退出应用 / 后台运行。
            CloseActionOptions = new[]
            {
                new CloseActionChoice(CloseWindowAction.Ask, "每次询问"),
                new CloseActionChoice(CloseWindowAction.Exit, "退出应用"),
                new CloseActionChoice(CloseWindowAction.MinimizeToTray, "后台运行")
            };
            ApprovalPolicyOptions = new[]
            {
                new ApprovalPolicyChoice(ToolApprovalPolicy.Ask, "每次询问"),
                new ApprovalPolicyChoice(ToolApprovalPolicy.Never, "从不询问")
            };
            GoalContinuationOptions = new[]
            {
                new GoalContinuationChoice(false, "关闭"),
                new GoalContinuationChoice(true, "自动续跑")
            };
            ProviderFormModeOptions = new[]
            {
                new ProviderFormModeChoice(ProviderFormMode.ThirdParty, "第三方供应商"),
                new ProviderFormModeChoice(ProviderFormMode.Custom, "自定义供应商")
            };
            _selectedProviderFormMode = ProviderFormModeOptions[0];

            var presets = new List<ProviderPresetChoice>();
            foreach (ProviderDefinition provider in ProviderPresets.CreateOnboardingProviders())
            {
                presets.Add(new ProviderPresetChoice(provider));
            }

            PresetChoices = presets;

            _selectedPage = Pages[0];

            SelectPageCommand = new RelayCommand(parameter => SelectPage(parameter as SettingsNavItem));
            CloseCommand = new RelayCommand(() => IsOpen = false);
            AddProviderCommand = new RelayCommand(OpenNewProvider);
            EditProviderCommand = new RelayCommand(parameter => OpenProviderEditor(parameter as ProviderEditorViewModel));
            SaveProviderCommand = new RelayCommand(SaveProvider);
            CancelProviderCommand = new RelayCommand(CloseProviderForm);
            RemoveProviderCommand = new RelayCommand(RemoveEditingProvider);
            TestProviderCommand = new RelayCommand(() => FireAndForget(TestProviderAsync()));
            FetchModelsCommand = new RelayCommand(() => FireAndForget(FetchModelsAsync()));
            CheckUpdateCommand = new RelayCommand(ShowUpdatePlaceholder);

            ReloadProviders();
        }

        public ObservableCollection<SettingsNavItem> Pages { get; }

        public ObservableCollection<ProviderEditorViewModel> ProviderEditors { get; }

        public IReadOnlyList<ThemeChoice> ThemeOptions { get; }

        public IReadOnlyList<LanguageChoice> LanguageOptions { get; }

        public IReadOnlyList<CloseActionChoice> CloseActionOptions { get; }

        public IReadOnlyList<ApprovalPolicyChoice> ApprovalPolicyOptions { get; }

        public IReadOnlyList<GoalContinuationChoice> GoalContinuationOptions { get; }

        public IReadOnlyList<ProviderFormModeChoice> ProviderFormModeOptions { get; }

        public IReadOnlyList<ProviderPresetChoice> PresetChoices { get; }

        public IReadOnlyList<string> ApiOptions
        {
            get { return new[] { "openai-completions", "anthropic-messages" }; }
        }

        public RelayCommand SelectPageCommand { get; }

        public RelayCommand CloseCommand { get; }

        public RelayCommand AddProviderCommand { get; }

        public RelayCommand EditProviderCommand { get; }

        public RelayCommand SaveProviderCommand { get; }

        public RelayCommand CancelProviderCommand { get; }

        public RelayCommand RemoveProviderCommand { get; }

        public RelayCommand TestProviderCommand { get; }

        public RelayCommand FetchModelsCommand { get; }

        public RelayCommand CheckUpdateCommand { get; }

        public bool IsOpen
        {
            get { return _isOpen; }
            set
            {
                if (SetProperty(ref _isOpen, value) && value)
                {
                    ReloadProviders();
                    StatusText = null;
                    ProviderFormStatus = null;
                    UpdateStatus = null;
                }
            }
        }

        public SettingsNavItem SelectedPage
        {
            get { return _selectedPage; }
            set
            {
                if (SetProperty(ref _selectedPage, value) && value != null)
                {
                    RaisePageFlags();
                }
            }
        }

        public bool IsGeneralPage
        {
            get { return _selectedPage != null && _selectedPage.Page == SettingsPage.General; }
        }

        public bool IsModelsPage
        {
            get { return _selectedPage != null && _selectedPage.Page == SettingsPage.Models; }
        }

        public bool IsAboutPage
        {
            get { return _selectedPage != null && _selectedPage.Page == SettingsPage.About; }
        }

        public ThemeChoice SelectedTheme
        {
            get
            {
                string current = _app.Settings.Ui.Theme ?? "light";
                foreach (ThemeChoice choice in ThemeOptions)
                {
                    if (string.Equals(choice.Value, current, StringComparison.Ordinal))
                    {
                        return choice;
                    }
                }

                return ThemeOptions[0];
            }
            set
            {
                if (value == null
                    || string.Equals(_app.Settings.Ui.Theme, value.Value, StringComparison.Ordinal))
                {
                    return;
                }

                _app.Settings.Ui.Theme = value.Value;
                OnPropertyChanged();
                SaveSettings();
            }
        }

        public LanguageChoice SelectedLanguage
        {
            get
            {
                string current = _app.Settings.Ui.Language ?? "zh-CN";
                foreach (LanguageChoice choice in LanguageOptions)
                {
                    if (string.Equals(choice.Value, current, StringComparison.Ordinal))
                    {
                        return choice;
                    }
                }

                return LanguageOptions[0];
            }
            set
            {
                if (value == null
                    || string.Equals(_app.Settings.Ui.Language, value.Value, StringComparison.Ordinal))
                {
                    return;
                }

                _app.Settings.Ui.Language = value.Value;
                OnPropertyChanged();
                SaveSettings();
            }
        }

        /// <summary>
        /// 关闭行为选项的选中项。Ask（每次询问）是默认值，配置损坏时由
        /// <c>AgentSettings.Validate</c> 归一为 Ask，因此正常情况下总有一项选中。
        /// </summary>
        public CloseActionChoice SelectedCloseAction
        {
            get
            {
                CloseWindowAction current = _app.Settings.Agent.CloseAction;
                foreach (CloseActionChoice choice in CloseActionOptions)
                {
                    if (choice.Action == current)
                    {
                        return choice;
                    }
                }

                return null;
            }
            set
            {
                if (value == null || _app.Settings.Agent.CloseAction == value.Action)
                {
                    return;
                }

                _app.Settings.Agent.CloseAction = value.Action;
                OnPropertyChanged();
                SaveSettings();
            }
        }

        /// <summary>
        /// 工具审批策略的选中项。保存即生效：会话每次构造工具上下文都会重读该值，
        /// 运行中的一轮也会在下一次工具调用时用上新策略。
        /// </summary>
        public ApprovalPolicyChoice SelectedApprovalPolicy
        {
            get
            {
                ToolApprovalPolicy current = _app.Settings.Agent.ApprovalPolicy;
                foreach (ApprovalPolicyChoice choice in ApprovalPolicyOptions)
                {
                    if (choice.Policy == current)
                    {
                        return choice;
                    }
                }

                return null;
            }
            set
            {
                if (value == null || _app.Settings.Agent.ApprovalPolicy == value.Policy)
                {
                    return;
                }

                _app.Settings.Agent.ApprovalPolicy = value.Policy;
                OnPropertyChanged();
                SaveSettings();
            }
        }

        /// <summary>
        /// 目标自动续跑的选中项。保存即生效：每轮结束判断续跑时都会重读该值，
        /// 关掉它不会打断正在跑的一轮，只是不再自动接下一轮。
        /// </summary>
        public GoalContinuationChoice SelectedGoalContinuation
        {
            get
            {
                bool current = _app.Settings.Agent.GoalAutoContinue;
                foreach (GoalContinuationChoice choice in GoalContinuationOptions)
                {
                    if (choice.Enabled == current)
                    {
                        return choice;
                    }
                }

                return null;
            }
            set
            {
                if (value == null || _app.Settings.Agent.GoalAutoContinue == value.Enabled)
                {
                    return;
                }

                _app.Settings.Agent.GoalAutoContinue = value.Enabled;
                OnPropertyChanged();
                SaveSettings();
            }
        }

        /// <summary>关闭浮层“记住”后由 MainWindowViewModel 调用，刷新选中项。</summary>
        public void NotifyCloseActionChanged()
        {
            OnPropertyChanged(nameof(SelectedCloseAction));
        }

        public string StatusText
        {
            get { return _statusText; }
            private set
            {
                if (SetProperty(ref _statusText, value))
                {
                    OnPropertyChanged(nameof(HasStatus));
                }
            }
        }

        public bool HasStatus
        {
            get { return !string.IsNullOrEmpty(_statusText); }
        }

        public string ProviderStatus
        {
            get { return _providerStatus; }
            private set
            {
                if (SetProperty(ref _providerStatus, value))
                {
                    OnPropertyChanged(nameof(HasProviderStatus));
                }
            }
        }

        public bool HasProviderStatus
        {
            get { return !string.IsNullOrEmpty(_providerStatus); }
        }

        /// <summary>是否是“添加供应商”流程；编辑已有供应商时不显示两个标签。</summary>
        public bool IsAddingProvider
        {
            get { return _isAddingProvider; }
            private set
            {
                if (SetProperty(ref _isAddingProvider, value))
                {
                    OnPropertyChanged(nameof(IsEditingProvider));
                }
            }
        }

        public bool IsEditingProvider
        {
            get { return !_isAddingProvider; }
        }

        public ProviderFormModeChoice SelectedProviderFormMode
        {
            get { return _selectedProviderFormMode; }
            set
            {
                if (value == null || !SetProperty(ref _selectedProviderFormMode, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(IsThirdPartyMode));
                OnPropertyChanged(nameof(IsCustomMode));
            }
        }

        public bool IsThirdPartyMode
        {
            get
            {
                return _selectedProviderFormMode != null
                    && _selectedProviderFormMode.Mode == ProviderFormMode.ThirdParty;
            }
        }

        public bool IsCustomMode
        {
            get
            {
                return _selectedProviderFormMode != null
                    && _selectedProviderFormMode.Mode == ProviderFormMode.Custom;
            }
        }

        /// <summary>第三方供应商标签中选中的预设。</summary>
        public ProviderPresetChoice SelectedPreset
        {
            get { return _selectedPreset; }
            set
            {
                if (value == null || !SetProperty(ref _selectedPreset, value))
                {
                    return;
                }

                ThirdPartyApiKey = null;
                OnPropertyChanged(nameof(ThirdPartyKeyStatus));
            }
        }

        /// <summary>第三方供应商标签中输入的 API Key；不回显已保存的密钥。</summary>
        public string ThirdPartyApiKey
        {
            get { return _thirdPartyApiKey; }
            set
            {
                if (SetProperty(ref _thirdPartyApiKey, value))
                {
                    OnPropertyChanged(nameof(ThirdPartyKeyStatus));
                }
            }
        }

        public string ThirdPartyKeyStatus
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(_thirdPartyApiKey))
                {
                    return "已填写新密钥（保存后 DPAPI 加密）";
                }

                if (_selectedPreset == null)
                {
                    return string.Empty;
                }

                ProviderEditorViewModel existing = ProviderEditors.FirstOrDefault(
                    editor => string.Equals(editor.Id, _selectedPreset.Definition.Id, StringComparison.OrdinalIgnoreCase));
                return existing != null && existing.HasStoredKey
                    ? "已配置（留空保持不变）"
                    : "未配置密钥";
            }
        }

        /// <summary>模型页草稿表单的供应商；null 表示当前没有打开表单。</summary>
        public ProviderEditorViewModel DraftProvider
        {
            get { return _draftProvider; }
            private set
            {
                if (SetProperty(ref _draftProvider, value))
                {
                    OnPropertyChanged(nameof(HasProviderForm));
                    OnPropertyChanged(nameof(ProviderFormTitle));
                }
            }
        }

        public bool HasProviderForm
        {
            get { return _draftProvider != null; }
        }

        public string ProviderFormTitle
        {
            get
            {
                return _editingProvider == null ? "添加供应商" : "编辑供应商";
            }
        }

        public bool CanRemoveProvider
        {
            get { return _editingProvider != null; }
        }

        public string ProviderFormStatus
        {
            get { return _providerFormStatus; }
            private set
            {
                if (SetProperty(ref _providerFormStatus, value))
                {
                    OnPropertyChanged(nameof(HasProviderFormStatus));
                }
            }
        }

        public bool HasProviderFormStatus
        {
            get { return !string.IsNullOrEmpty(_providerFormStatus); }
        }

        public bool IsTesting
        {
            get { return _isTesting; }
            private set
            {
                if (SetProperty(ref _isTesting, value))
                {
                    OnPropertyChanged(nameof(IsNotTesting));
                }
            }
        }

        public bool IsNotTesting
        {
            get { return !_isTesting; }
        }

        /// <summary>正在 GET /models；按钮在此期间禁用，避免重复请求。</summary>
        public bool IsFetchingModels
        {
            get { return _isFetchingModels; }
            private set
            {
                if (SetProperty(ref _isFetchingModels, value))
                {
                    OnPropertyChanged(nameof(CanFetchModels));
                }
            }
        }

        public bool CanFetchModels
        {
            get { return !_isFetchingModels; }
        }

        /// <summary>“自动获取”的结果提示，显示在模型标题下方。</summary>
        public string ModelFetchStatus
        {
            get { return _modelFetchStatus; }
            private set
            {
                if (SetProperty(ref _modelFetchStatus, value))
                {
                    OnPropertyChanged(nameof(HasModelFetchStatus));
                }
            }
        }

        public bool HasModelFetchStatus
        {
            get { return !string.IsNullOrEmpty(_modelFetchStatus); }
        }

        public string UpdateStatus
        {
            get { return _updateStatus; }
            private set
            {
                if (SetProperty(ref _updateStatus, value))
                {
                    OnPropertyChanged(nameof(HasUpdateStatus));
                }
            }
        }

        public bool HasUpdateStatus
        {
            get { return !string.IsNullOrEmpty(_updateStatus); }
        }

        public string AboutLine
        {
            get { return "MiaoWu " + AppInfo.Version + " · 本地优先的智能体工作台"; }
        }

        public void Open()
        {
            IsOpen = true;
            SelectPage(SettingsPage.General);
        }

        public void Close()
        {
            IsOpen = false;
        }

        public void SelectPage(SettingsPage page)
        {
            foreach (SettingsNavItem item in Pages)
            {
                if (item.Page == page)
                {
                    SelectedPage = item;
                    return;
                }
            }
        }

        public void ReloadProviders(string selectedProviderId = null)
        {
            CloseProviderForm();

            ProviderEditors.Clear();
            ProviderCatalog catalog = _app.ProviderConfigStore.Load();
            IReadOnlyDictionary<string, string> keys = _app.Credentials.LoadAll();

            foreach (ProviderDefinition provider in catalog.Providers)
            {
                string key;
                keys.TryGetValue(provider.Id, out key);
                ProviderEditors.Add(new ProviderEditorViewModel(provider, null, !string.IsNullOrEmpty(key)));
            }

            ProviderStatus = null;

            if (selectedProviderId != null)
            {
                ProviderEditorViewModel selected = ProviderEditors.FirstOrDefault(
                    editor => string.Equals(editor.Id, selectedProviderId, StringComparison.OrdinalIgnoreCase));
                if (selected != null)
                {
                    ProviderStatus = "已保存 " + selected.DisplayName + "。";
                }
            }
        }

        private void SelectPage(SettingsNavItem item)
        {
            if (item != null)
            {
                SelectedPage = item;
            }
        }

        private void SaveSettings()
        {
            try
            {
                _app.SaveSettings();
                StatusText = null;
            }
            catch (Exception exception)
            {
                StatusText = "保存失败：" + exception.Message;
            }
        }

        private void OpenNewProvider()
        {
            CloseProviderForm();

            IsAddingProvider = true;
            SelectedProviderFormMode = ProviderFormModeOptions[0];
            SelectedPreset = PresetChoices.Count > 0 ? PresetChoices[0] : null;
            ThirdPartyApiKey = null;
            DraftProvider = new ProviderEditorViewModel(CreateBlankProviderDefinition(), null);
            ProviderFormStatus = null;
            ProviderStatus = null;
            ModelFetchStatus = null;
        }

        private void OpenProviderEditor(ProviderEditorViewModel source)
        {
            if (source == null)
            {
                return;
            }

            ProviderDefinition definition = source.ToDefinition(out string error);
            if (definition == null)
            {
                ProviderStatus = string.IsNullOrEmpty(error) ? "无法打开供应商。" : error;
                return;
            }

            CloseProviderForm();
            IsAddingProvider = false;
            SelectedProviderFormMode = ProviderFormModeOptions[1];
            _editingProvider = source;
            source.IsEditing = true;
            DraftProvider = new ProviderEditorViewModel(definition, null, source.HasStoredKey);
            ProviderFormStatus = null;
            ProviderStatus = null;
            ModelFetchStatus = null;
            OnPropertyChanged(nameof(ProviderFormTitle));
            OnPropertyChanged(nameof(CanRemoveProvider));
        }

        private void CloseProviderForm()
        {
            if (_editingProvider != null)
            {
                _editingProvider.IsEditing = false;
                _editingProvider = null;
            }

            DraftProvider = null;
            _thirdPartyApiKey = null;
            OnPropertyChanged(nameof(ThirdPartyApiKey));
            OnPropertyChanged(nameof(ThirdPartyKeyStatus));
            SetProperty(ref _selectedPreset, null, nameof(SelectedPreset));
            IsAddingProvider = false;
            ProviderFormStatus = null;
            ModelFetchStatus = null;
            IsFetchingModels = false;
            OnPropertyChanged(nameof(CanRemoveProvider));
            OnPropertyChanged(nameof(ProviderFormTitle));
        }

        private static ProviderDefinition CreateBlankProviderDefinition()
        {
            // 新建表单默认没有模型：由“自动获取”或“添加模型”生成，避免空卡片占位。
            return new ProviderDefinition
            {
                Id = string.Empty,
                DisplayName = string.Empty,
                Api = "openai-completions",
                BaseUrl = string.Empty
            };
        }

        private void SaveProvider()
        {
            if (IsAddingProvider && IsThirdPartyMode)
            {
                SaveThirdPartyProvider();
                return;
            }

            SaveCustomProvider();
        }

        private void SaveCustomProvider()
        {
            ProviderEditorViewModel draft = _draftProvider;
            if (draft == null)
            {
                return;
            }

            if (draft.Models.Count == 0)
            {
                ProviderFormStatus = "请至少添加一个模型（可点“自动获取”）。";
                return;
            }

            ProviderDefinition draftDefinition = draft.ToDefinition(out string draftError);
            if (draftDefinition == null)
            {
                ProviderFormStatus = draftError;
                return;
            }

            var catalog = new ProviderCatalog
            {
                ActiveProviderId = _app.Settings.Agent.DefaultProviderId
            };
            var errors = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ProviderEditorViewModel editor in ProviderEditors)
            {
                ProviderDefinition definition;
                if (ReferenceEquals(editor, _editingProvider))
                {
                    definition = draftDefinition;
                }
                else
                {
                    definition = editor.ToDefinition(out string error);
                    if (definition == null)
                    {
                        errors.Add(error);
                        continue;
                    }
                }

                if (!seen.Add(definition.Id))
                {
                    errors.Add("Provider ID 重复：" + definition.Id);
                    continue;
                }

                catalog.Providers.Add(definition);
            }

            if (_editingProvider == null)
            {
                if (!seen.Add(draftDefinition.Id))
                {
                    errors.Add("Provider ID 重复：" + draftDefinition.Id);
                }
                else
                {
                    catalog.Providers.Add(draftDefinition);
                }
            }

            if (errors.Count > 0)
            {
                ProviderFormStatus = string.Join("\n", errors);
                return;
            }

            string previousId = _editingProvider == null ? null : _editingProvider.Id;

            try
            {
                IReadOnlyList<string> validation = _app.SaveProviders(catalog);
                if (validation.Count > 0)
                {
                    ProviderFormStatus = string.Join("\n", validation);
                    return;
                }

                PersistKey(draft, previousId);

                string savedId = draftDefinition.Id;
                CloseProviderForm();
                ReloadProviders(savedId);
            }
            catch (Exception exception)
            {
                ProviderFormStatus = "保存失败：" + exception.Message;
            }
        }

        private void SaveThirdPartyProvider()
        {
            ProviderPresetChoice preset = _selectedPreset;
            if (preset == null)
            {
                ProviderFormStatus = "请选择第三方供应商。";
                return;
            }

            var catalog = new ProviderCatalog
            {
                ActiveProviderId = _app.Settings.Agent.DefaultProviderId
            };
            var errors = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool existing = false;

            foreach (ProviderEditorViewModel editor in ProviderEditors)
            {
                ProviderDefinition definition = editor.ToDefinition(out string error);
                if (definition == null)
                {
                    errors.Add(error);
                    continue;
                }

                if (!seen.Add(definition.Id))
                {
                    errors.Add("Provider ID 重复：" + definition.Id);
                    continue;
                }

                if (string.Equals(definition.Id, preset.Definition.Id, StringComparison.OrdinalIgnoreCase))
                {
                    existing = true;
                }

                catalog.Providers.Add(definition);
            }

            if (!existing)
            {
                if (!seen.Add(preset.Definition.Id))
                {
                    errors.Add("Provider ID 重复：" + preset.Definition.Id);
                }
                else
                {
                    catalog.Providers.Add(preset.Definition);
                }
            }

            if (errors.Count > 0)
            {
                ProviderFormStatus = string.Join("\n", errors);
                return;
            }

            try
            {
                IReadOnlyList<string> validation = _app.SaveProviders(catalog);
                if (validation.Count > 0)
                {
                    ProviderFormStatus = string.Join("\n", validation);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(_thirdPartyApiKey))
                {
                    _app.Credentials.SetKey(preset.Definition.Id, _thirdPartyApiKey);
                }
                else if (!existing)
                {
                    _app.Credentials.Remove(preset.Definition.Id);
                }

                string savedId = preset.Definition.Id;
                string savedName = preset.DisplayName;
                CloseProviderForm();
                ReloadProviders(savedId);
                ProviderStatus = "已保存 " + savedName + "。";
            }
            catch (Exception exception)
            {
                ProviderFormStatus = "保存失败：" + exception.Message;
            }
        }


        private void RemoveEditingProvider()
        {
            ProviderEditorViewModel removed = _editingProvider;
            if (removed == null)
            {
                return;
            }

            var catalog = new ProviderCatalog
            {
                ActiveProviderId = _app.Settings.Agent.DefaultProviderId
            };
            var errors = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ProviderEditorViewModel editor in ProviderEditors)
            {
                if (ReferenceEquals(editor, removed))
                {
                    continue;
                }

                ProviderDefinition definition = editor.ToDefinition(out string error);
                if (definition == null)
                {
                    errors.Add(error);
                    continue;
                }

                if (!seen.Add(definition.Id))
                {
                    errors.Add("Provider ID 重复：" + definition.Id);
                    continue;
                }

                catalog.Providers.Add(definition);
            }

            if (errors.Count > 0)
            {
                ProviderFormStatus = string.Join("\n", errors);
                return;
            }

            try
            {
                IReadOnlyList<string> validation = _app.SaveProviders(catalog);
                if (validation.Count > 0)
                {
                    ProviderFormStatus = string.Join("\n", validation);
                    return;
                }

                _app.Credentials.Remove(removed.Id);
                CloseProviderForm();
                ReloadProviders();
                ProviderStatus = "已移除供应商 " + removed.DisplayName + "。";
            }
            catch (Exception exception)
            {
                ProviderFormStatus = "移除失败：" + exception.Message;
            }
        }

        /// <summary>
        /// 只处理当前草稿的密钥：新输入的 Key 写入 DPAPI；留空时保留已有密钥；
        /// 新建供应商留空则清除；Provider ID 改名时把旧密钥迁移到新 ID。
        /// </summary>
        private void PersistKey(ProviderEditorViewModel draft, string previousId)
        {
            if (!string.IsNullOrWhiteSpace(draft.ApiKey))
            {
                _app.Credentials.SetKey(draft.Id, draft.ApiKey);
            }
            else if (!draft.HasStoredKey)
            {
                _app.Credentials.Remove(draft.Id);
            }
            else if (previousId != null
                && !string.Equals(previousId, draft.Id, StringComparison.OrdinalIgnoreCase))
            {
                string stored = _app.Credentials.GetKey(previousId);
                if (!string.IsNullOrWhiteSpace(stored))
                {
                    _app.Credentials.SetKey(draft.Id, stored);
                }
            }

            if (previousId != null
                && !string.Equals(previousId, draft.Id, StringComparison.OrdinalIgnoreCase))
            {
                _app.Credentials.Remove(previousId);
            }
        }

        private void ShowUpdatePlaceholder()
        {
            UpdateStatus = "检查更新为预留功能，将在后续版本提供。";
        }

        /// <summary>
        /// 从供应商 /models 接口拉取模型，合并进当前草稿（已有 ID 不重复）。
        /// 用草稿里刚输入的 Base URL 与 Key；编辑已有供应商时可回退到已存密钥。
        /// </summary>
        private async Task FetchModelsAsync()
        {
            ProviderEditorViewModel draft = _draftProvider;
            if (draft == null)
            {
                return;
            }

            string baseUrl = (draft.BaseUrl ?? string.Empty).Trim();
            if (baseUrl.Length == 0)
            {
                ModelFetchStatus = "请先填写 Base URL。";
                return;
            }

            string apiKey = draft.ApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                apiKey = _app.Credentials.GetKey(draft.Id);
            }

            if (string.IsNullOrWhiteSpace(apiKey) && _editingProvider != null)
            {
                apiKey = _app.Credentials.GetKey(_editingProvider.Id);
            }

            IsFetchingModels = true;
            ModelFetchStatus = "正在获取模型列表…";
            try
            {
                IReadOnlyList<ModelCatalogEntry> entries;
                using (var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20)))
                {
                    entries = await _app.ListModelsAsync(baseUrl, draft.Api, apiKey, timeout.Token);
                }

                await RunOnUiAsync(() =>
                {
                    if (!ReferenceEquals(_draftProvider, draft))
                    {
                        return;
                    }

                    if (entries.Count == 0)
                    {
                        ModelFetchStatus = "接口没有返回模型。";
                        return;
                    }

                    int added = draft.MergeModels(entries);
                    ModelFetchStatus = added == 0
                        ? "已获取 " + entries.Count + " 个模型，均已存在。"
                        : "已获取 " + entries.Count + " 个模型，新增 " + added + " 个。";
                });
            }
            catch (OperationCanceledException)
            {
                await RunOnUiAsync(() => ModelFetchStatus = "获取超时：检查 Base URL 或网络。");
            }
            catch (Exception exception)
            {
                await RunOnUiAsync(() => ModelFetchStatus = "获取失败：" + exception.Message);
            }
            finally
            {
                await RunOnUiAsync(() => IsFetchingModels = false);
            }
        }

        /// <summary>
        /// await 之后的 UI 变更切回设置页 Dispatcher。真实应用有 SynchronizationContext，
        /// 离屏探针没有；显式切回来才能安全改 ObservableCollection。
        /// </summary>
        private Task RunOnUiAsync(Action action)
        {
            if (_dispatcher.CheckAccess())
            {
                action();
                return Task.CompletedTask;
            }

            return _dispatcher.InvokeAsync(action).Task;
        }

        private async Task TestProviderAsync()
        {
            ProviderEditorViewModel editor = _draftProvider;
            if (editor == null)
            {
                return;
            }

            if (IsAddingProvider && IsThirdPartyMode)
            {
                ProviderPresetChoice preset = _selectedPreset;
                if (preset == null)
                {
                    ProviderFormStatus = "请选择第三方供应商。";
                    return;
                }

                editor = new ProviderEditorViewModel(preset.Definition, ThirdPartyApiKey, false);
            }

            IsTesting = true;
            ProviderFormStatus = "正在连接…";
            try
            {
                ProviderDefinition definition = editor.ToDefinition(out string error);
                if (definition == null)
                {
                    ProviderFormStatus = error;
                    return;
                }

                var catalog = new ProviderCatalog();
                catalog.Providers.Add(definition);
                string testKey = !string.IsNullOrWhiteSpace(editor.ApiKey)
                    ? editor.ApiKey
                    : _app.Credentials.GetKey(editor.Id);
                if (string.IsNullOrWhiteSpace(testKey) && _editingProvider != null)
                {
                    // 草稿改了 Provider ID 时，测试连接仍可回退到原 ID 的已存密钥。
                    testKey = _app.Credentials.GetKey(_editingProvider.Id);
                }

                if (string.IsNullOrWhiteSpace(testKey))
                {
                    ProviderFormStatus = "未填写 API Key。";
                    return;
                }

                var registry = new ModelRegistry(catalog, new TestCredentialStore(editor.Id, testKey), new IModelApi[]
                {
                    new MiaoWu.Infrastructure.Providers.OpenAiCompletionsApi(_app.Http),
                    new MiaoWu.Infrastructure.Providers.AnthropicMessagesApi(_app.Http)
                });

                ModelDescriptor model = registry.GetDefault();
                if (model == null)
                {
                    ProviderFormStatus = "供应商没有模型。";
                    return;
                }

                ProviderCredential credential = await registry.ResolveCredentialAsync(
                    model.ProviderId,
                    System.Threading.CancellationToken.None);

                if (!credential.HasKey)
                {
                    ProviderFormStatus = "未填写 API Key。";
                    return;
                }

                IModelApi api = registry.GetApi(model.Api);
                var request = new LlmRequest
                {
                    SystemPrompt = "ping",
                    MaxOutputTokens = 1,
                    Messages = new List<LlmMessage>
                    {
                        new LlmMessage
                        {
                            Role = "user",
                            Content = new List<LlmContent>
                            {
                                new LlmContent { Type = "text", Text = "ping" }
                            }
                        }
                    }
                };

                using (var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20)))
                using (ILlmStream stream = api.Open(model, request, credential, timeout.Token))
                {
                    while (true)
                    {
                        LlmStreamEvent evt = await stream.ReadAsync(timeout.Token);
                        if (evt == null)
                        {
                            ProviderFormStatus = "连接成功：模型已响应。";
                            return;
                        }

                        var failed = evt as LlmFailed;
                        if (failed != null)
                        {
                            ProviderFormStatus = "失败：" + (failed.Error == null ? "未知错误" : failed.Error.Message);
                            return;
                        }

                        if (evt is LlmStarted || evt is LlmTextDelta || evt is LlmCompleted)
                        {
                            ProviderFormStatus = "连接成功：TLS / 代理 / Key / 模型均可用。";
                            return;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                ProviderFormStatus = "超时：检查网络、代理或系统 TLS 1.2 补丁。";
            }
            catch (Exception exception)
            {
                ProviderFormStatus = "失败：" + exception.Message;
            }
            finally
            {
                IsTesting = false;
            }
        }

        private void RaisePageFlags()
        {
            OnPropertyChanged(nameof(IsGeneralPage));
            OnPropertyChanged(nameof(IsModelsPage));
            OnPropertyChanged(nameof(IsAboutPage));
        }

        private void FireAndForget(Task task)
        {
            if (task == null)
            {
                return;
            }

            task.ContinueWith(
                t =>
                {
                    if (t.Exception != null)
                    {
                        _dispatcher.BeginInvoke(new Action(() =>
                            ProviderFormStatus = "操作失败：" + t.Exception.GetBaseException().Message));
                    }
                },
                TaskScheduler.Default);
        }

        /// <summary>连接测试要使用表单里刚输入、尚未保存的 Key。</summary>
        private sealed class TestCredentialStore : ICredentialStore
        {
            private readonly string _providerId;
            private readonly string _key;

            public TestCredentialStore(string providerId, string key)
            {
                _providerId = providerId;
                _key = key;
            }

            public string GetKey(string providerId)
            {
                return string.Equals(providerId, _providerId, StringComparison.OrdinalIgnoreCase)
                    ? _key
                    : null;
            }

            public void SetKey(string providerId, string apiKey)
            {
            }

            public void Remove(string providerId)
            {
            }

            public IReadOnlyDictionary<string, string> LoadAll()
            {
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(_key))
                {
                    values[_providerId] = _key;
                }

                return values;
            }
        }
    }

    /// <summary>Provider 编辑草稿。保存前不写盘；取消时整份草稿丢弃。</summary>
    public sealed class ProviderEditorViewModel : ObservableObject
    {
        private readonly ProviderDefinition _definition;
        private string _apiKey;
        private ModelEditorViewModel _selectedModel;
        private string _displayName;
        private string _id;
        private string _api;
        private string _baseUrl;
        private bool _isEditing;
        private readonly bool _hasStoredKey;

        public ProviderEditorViewModel(ProviderDefinition definition, string apiKey, bool hasStoredKey = false)
        {
            _definition = definition ?? throw new ArgumentNullException(nameof(definition));
            _apiKey = apiKey;
            _hasStoredKey = hasStoredKey || !string.IsNullOrWhiteSpace(apiKey);
            _displayName = definition.DisplayName;
            _id = definition.Id;
            _api = definition.Api;
            _baseUrl = definition.BaseUrl;

            Models = new ObservableCollection<ModelEditorViewModel>();
            foreach (ModelDefinition model in definition.Models)
            {
                Models.Add(new ModelEditorViewModel(model));
            }

            AddModelCommand = new RelayCommand(AddModel);
            RemoveModelCommand = new RelayCommand(parameter => RemoveModel(parameter as ModelEditorViewModel));
            _selectedModel = Models.FirstOrDefault();
        }

        public ObservableCollection<ModelEditorViewModel> Models { get; }

        public RelayCommand AddModelCommand { get; }

        public RelayCommand RemoveModelCommand { get; }

        public ModelEditorViewModel SelectedModel
        {
            get { return _selectedModel; }
            set { SetProperty(ref _selectedModel, value); }
        }

        public string Id
        {
            get { return _id; }
            set { SetProperty(ref _id, value); }
        }

        public string DisplayName
        {
            get { return _displayName; }
            set { SetProperty(ref _displayName, value); }
        }

        public string Api
        {
            get { return _api; }
            set { SetProperty(ref _api, value); }
        }

        public string BaseUrl
        {
            get { return _baseUrl; }
            set { SetProperty(ref _baseUrl, value); }
        }

        public string ApiKey
        {
            get { return _apiKey; }
            set
            {
                if (SetProperty(ref _apiKey, value))
                {
                    OnPropertyChanged(nameof(KeyStatus));
                }
            }
        }

        public string KeyStatus
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(_apiKey))
                {
                    return "已填写新密钥（保存后 DPAPI 加密）";
                }

                return _hasStoredKey ? "已配置（留空保持不变）" : "未配置密钥";
            }
        }

        /// <summary>secrets.dat 中是否已有该供应商的密钥；表单不回显明文。</summary>
        public bool HasStoredKey
        {
            get { return _hasStoredKey; }
        }

        public string ModelCountText
        {
            get { return Models.Count + " 个模型"; }
        }

        /// <summary>卡片是否处于编辑态；由 SettingsViewModel 在打开/关闭表单时设置。</summary>
        public bool IsEditing
        {
            get { return _isEditing; }
            set { SetProperty(ref _isEditing, value); }
        }

        public ProviderDefinition ToDefinition(out string error)
        {
            error = null;
            string id = (_id ?? string.Empty).Trim();
            if (id.Length == 0)
            {
                error = "Provider ID 不能为空。";
                return null;
            }

            var definition = new ProviderDefinition
            {
                Id = id,
                DisplayName = string.IsNullOrWhiteSpace(_displayName) ? id : _displayName.Trim(),
                Api = _api,
                BaseUrl = (_baseUrl ?? string.Empty).Trim(),
                Temperature = _definition.Temperature,
                TimeoutSeconds = _definition.TimeoutSeconds,
                MaxRetries = _definition.MaxRetries,
                Compat = _definition.Compat == null ? new OpenAiCompat() : _definition.Compat.Clone()
            };

            foreach (ModelEditorViewModel model in Models)
            {
                ModelDefinition value = model.ToModel(out string modelError);
                if (!string.IsNullOrEmpty(modelError))
                {
                    error = modelError;
                    return null;
                }

                definition.Models.Add(value);
            }

            return definition;
        }

        private void AddModel()
        {
            var model = new ModelEditorViewModel(new ModelDefinition
            {
                Id = string.Empty,
                Name = string.Empty,
                ContextWindow = 128000,
                MaxOutputTokens = 8192
            });
            Models.Add(model);
            SelectedModel = model;
            OnPropertyChanged(nameof(ModelCountText));
        }

        private void RemoveModel(ModelEditorViewModel model)
        {
            if (model != null)
            {
                Models.Remove(model);
                if (ReferenceEquals(_selectedModel, model))
                {
                    SelectedModel = Models.FirstOrDefault();
                }

                OnPropertyChanged(nameof(ModelCountText));
            }
        }

        /// <summary>
        /// 把 /models 返回的条目合并进草稿：已存在的 ID 跳过，新模型用接口的展示名。
        /// 返回新增数量。
        /// </summary>
        public int MergeModels(IEnumerable<ModelCatalogEntry> entries)
        {
            if (entries == null)
            {
                return 0;
            }

            var existing = new HashSet<string>(
                Models.Select(model => (model.Id ?? string.Empty).Trim()),
                StringComparer.OrdinalIgnoreCase);
            int added = 0;

            foreach (ModelCatalogEntry entry in entries)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.Id) || !existing.Add(entry.Id.Trim()))
                {
                    continue;
                }

                Models.Add(new ModelEditorViewModel(ProviderModelCatalogMerge.CreateModel(entry)));
                added++;
            }

            if (added > 0)
            {
                SelectedModel = Models.FirstOrDefault();
                OnPropertyChanged(nameof(ModelCountText));
            }

            return added;
        }
    }

    public sealed class ModelEditorViewModel : ObservableObject
    {
        private string _id;
        private string _name;
        private int _contextWindow;
        private int _maxOutputTokens;
        private bool _supportsImages;
        private bool _supportsReasoning;

        public ModelEditorViewModel(ModelDefinition model)
        {
            _id = model.Id;
            _name = model.Name;
            _contextWindow = model.ContextWindow;
            _maxOutputTokens = model.MaxOutputTokens;
            _supportsImages = model.SupportsImages;
            _supportsReasoning = model.SupportsReasoning;
        }

        public string Id
        {
            get { return _id; }
            set { SetProperty(ref _id, value); }
        }

        public string Name
        {
            get { return _name; }
            set { SetProperty(ref _name, value); }
        }

        public int ContextWindow
        {
            get { return _contextWindow; }
            set { SetProperty(ref _contextWindow, value); }
        }

        public int MaxOutputTokens
        {
            get { return _maxOutputTokens; }
            set { SetProperty(ref _maxOutputTokens, value); }
        }

        public bool SupportsImages
        {
            get { return _supportsImages; }
            set { SetProperty(ref _supportsImages, value); }
        }

        public bool SupportsReasoning
        {
            get { return _supportsReasoning; }
            set { SetProperty(ref _supportsReasoning, value); }
        }

        public ModelDefinition ToModel(out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(_id))
            {
                error = "模型 ID 不能为空。";
                return null;
            }

            if (_contextWindow < 1024)
            {
                error = "模型 " + _id + " 的上下文窗口至少 1024。";
                return null;
            }

            if (_maxOutputTokens > _contextWindow / 2)
            {
                error = "模型 " + _id + " 的最大输出不能超过上下文窗口的一半。";
                return null;
            }

            return new ModelDefinition
            {
                Id = _id.Trim(),
                Name = string.IsNullOrWhiteSpace(_name) ? _id.Trim() : _name.Trim(),
                ContextWindow = _contextWindow,
                MaxOutputTokens = _maxOutputTokens,
                SupportsImages = _supportsImages,
                SupportsReasoning = _supportsReasoning
            };
        }
    }
}
