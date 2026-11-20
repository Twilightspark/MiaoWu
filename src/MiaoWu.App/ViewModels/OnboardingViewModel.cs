using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Input;
using MiaoWu.App.Mvvm;
using MiaoWu.App.Services;
using MiaoWu.Core.Abstractions;
using MiaoWu.Core.Models;
using MiaoWu.Core.Workspaces;
using MiaoWu.Infrastructure.Providers;

namespace MiaoWu.App.ViewModels
{
    /// <summary>
    /// 首次启动引导。占用主窗口客户区，单页：配置第一个工作区，可选地填入一个 Provider Key。
    /// 只写 workspaces.json / providers.json / secrets.dat / config.json；
    /// 关闭表示退出应用，只有“开始使用”会写入 firstLaunch=false。
    /// </summary>
    public sealed class OnboardingViewModel : ObservableObject
    {
        private readonly AppServices _app;
        private readonly IFolderPicker _folderPicker;

        private string _workspacePath;
        private string _workspaceName;
        private string _apiKey;
        private string _providerId = "deepseek";
        private string _errorText;
        private string _hintText;
        private bool _isBusy;
        private bool _closeNotified;

        public OnboardingViewModel(AppServices app, IFolderPicker folderPicker)
        {
            _app = app ?? throw new ArgumentNullException(nameof(app));
            _folderPicker = folderPicker;

            ProviderChoices = new ObservableCollection<ProviderChoice>();
            foreach (ProviderDefinition provider in ProviderPresets.CreateOnboardingProviders())
            {
                string defaultModel = provider.Models.Count == 0 ? null : provider.Models[0].Id;
                ProviderChoices.Add(new ProviderChoice(
                    provider.Id,
                    provider.DisplayName,
                    provider.BaseUrl,
                    defaultModel));
            }

            PickWorkspaceCommand = new RelayCommand(PickWorkspace, () => !_isBusy);
            StartCommand = new RelayCommand(Start, () => CanStart);
            CloseCommand = new RelayCommand(RequestClose, () => !_isBusy);

            ProviderCatalog catalog = _app.ProviderConfigStore.Load();
            string activeProviderId = catalog == null ? null : catalog.ActiveProviderId;
            SelectedChoice = ProviderChoices.FirstOrDefault(
                    p => string.Equals(p.Id, activeProviderId, StringComparison.OrdinalIgnoreCase))
                ?? ProviderChoices[0];

            UpdateHint();
        }

        /// <summary>工作区、Provider 与完成标记都落盘后触发，由主窗口切换为三栏。</summary>
        public event EventHandler Completed;

        /// <summary>用户点关闭或标题栏 X 时触发，由主视图退出应用。</summary>
        public event EventHandler CloseRequested;

        public ObservableCollection<ProviderChoice> ProviderChoices { get; }

        public RelayCommand PickWorkspaceCommand { get; }

        public RelayCommand StartCommand { get; }

        public RelayCommand CloseCommand { get; }

        public string WorkspacePath
        {
            get { return _workspacePath; }
            set
            {
                if (!SetProperty(ref _workspacePath, value))
                {
                    return;
                }

                _workspaceName = WorkspacePolicy.GetDisplayName(value);
                OnPropertyChanged(nameof(WorkspaceName));
                OnPropertyChanged(nameof(HasWorkspace));
                OnPropertyChanged(nameof(CanStart));
                ClearError();
                UpdateHint();
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public string WorkspaceName
        {
            get { return _workspaceName; }
        }

        public bool HasWorkspace
        {
            get { return !string.IsNullOrWhiteSpace(_workspacePath); }
        }

        public string ApiKey
        {
            get { return _apiKey; }
            set
            {
                if (SetProperty(ref _apiKey, value))
                {
                    ClearError();
                    UpdateHint();
                }
            }
        }

        public string ProviderId
        {
            get { return _providerId; }
        }

        public ProviderChoice SelectedChoice
        {
            get
            {
                return ProviderChoices.FirstOrDefault(
                    p => string.Equals(p.Id, _providerId, StringComparison.OrdinalIgnoreCase));
            }
            set
            {
                if (value == null || string.Equals(value.Id, _providerId, StringComparison.Ordinal))
                {
                    return;
                }

                _providerId = value.Id;
                OnPropertyChanged();
                ClearError();
            }
        }

        public string HintText
        {
            get { return _hintText; }
            private set { SetProperty(ref _hintText, value); }
        }

        public string ErrorText
        {
            get { return _errorText; }
            private set
            {
                if (!SetProperty(ref _errorText, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(HasError));
                UpdateHint();
            }
        }

        public bool HasError
        {
            get { return !string.IsNullOrEmpty(_errorText); }
        }

        public bool IsBusy
        {
            get { return _isBusy; }
            private set
            {
                if (!SetProperty(ref _isBusy, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(CanStart));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool CanStart
        {
            get { return HasWorkspace && !_isBusy; }
        }

        public void RequestClose()
        {
            if (_closeNotified || _isBusy)
            {
                return;
            }

            _closeNotified = true;
            EventHandler handler = CloseRequested;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void PickWorkspace()
        {
            string initial = string.IsNullOrWhiteSpace(_workspacePath)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : _workspacePath;

            string path = _folderPicker == null
                ? null
                : _folderPicker.PickFolder("选择第一个工作区", initial);

            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            WorkspacePath = path;
        }

        private void Start()
        {
            if (!CanStart)
            {
                return;
            }

            ErrorText = null;
            IsBusy = true;
            bool completed = false;

            try
            {
                string normalized;
                try
                {
                    normalized = WorkspacePolicy.NormalizePath(_workspacePath);
                }
                catch (Exception)
                {
                    ErrorText = WorkspacePolicy.InvalidPathError;
                    return;
                }

                if (!Directory.Exists(normalized))
                {
                    ErrorText = WorkspacePolicy.MissingFolderError;
                    return;
                }

                WorkspaceCollection collection = _app.WorkspaceStore.Load() ?? new WorkspaceCollection();
                var workspaces = new List<Workspace>(collection.Workspaces ?? new List<Workspace>());
                Workspace workspace = WorkspacePolicy.FindByPath(workspaces, normalized);

                if (workspace == null)
                {
                    string error;
                    if (!WorkspacePolicy.TryAdd(workspaces, normalized, out workspace, out error))
                    {
                        ErrorText = error;
                        return;
                    }
                }

                WorkspacePolicy.ExpandOnly(workspaces, workspace.Id);
                collection.Workspaces = workspaces;
                collection.ActiveWorkspaceId = workspace.Id;
                _app.WorkspaceStore.Save(collection);

                if (!string.IsNullOrWhiteSpace(_apiKey) && !TrySaveProvider())
                {
                    return;
                }

                _app.Settings.FirstLaunch = false;
                _app.Settings.Agent.OnboardingCompleted = true;
                _app.SaveSettings();

                _app.Logger.Info(
                    "onboarding.completed",
                    "首次设置完成：工作区 " + workspace.Id + "，Provider " + _providerId + "。");

                completed = true;
            }
            catch (Exception exception)
            {
                ErrorText = "保存失败：" + exception.Message;
            }
            finally
            {
                IsBusy = false;
            }

            if (!completed)
            {
                return;
            }

            EventHandler handler = Completed;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private bool TrySaveProvider()
        {
            ProviderChoice choice = SelectedChoice;
            if (choice == null)
            {
                ErrorText = "请选择 Provider。";
                return false;
            }

            ProviderDefinition preset = ProviderPresets.CreateOnboardingProviders()
                .FirstOrDefault(p => string.Equals(p.Id, choice.Id, StringComparison.Ordinal));

            if (preset == null)
            {
                ErrorText = "未知的 Provider。";
                return false;
            }

            ProviderCatalog catalog = _app.ProviderConfigStore.Load();
            ProviderDefinition provider = catalog.FindProvider(choice.Id);

            if (provider == null)
            {
                provider = preset;
                catalog.Providers.Add(provider);
            }
            else
            {
                // 旧 providers.json 只有 DeepSeek / OpenAI / Anthropic；首启选择时补齐端点与模型。
                provider.DisplayName = preset.DisplayName;
                provider.Api = preset.Api;
                provider.BaseUrl = preset.BaseUrl;
                provider.Compat = preset.Compat;

                if (provider.Models == null)
                {
                    provider.Models = new List<ModelDefinition>();
                }

                foreach (ModelDefinition model in preset.Models)
                {
                    if (!provider.Models.Any(m => string.Equals(m.Id, model.Id, StringComparison.Ordinal)))
                    {
                        provider.Models.Add(model);
                    }
                }
            }

            catalog.ActiveProviderId = provider.Id;
            IReadOnlyList<string> errors = ProviderConfigValidator.Validate(catalog);
            if (errors.Count > 0)
            {
                ErrorText = string.Join("\n", errors);
                return false;
            }

            IReadOnlyList<string> saveErrors = _app.SaveProviders(catalog);
            if (saveErrors.Count > 0)
            {
                ErrorText = string.Join("\n", saveErrors);
                return false;
            }

            _app.Credentials.SetKey(provider.Id, _apiKey.Trim());
            // 首启选的模型就是所有会话共用的默认模型（切换事件里会写回 config.json）。
            if (!_app.SetDefaultModel(provider.Id, choice.DefaultModel))
            {
                _app.Settings.Agent.DefaultProviderId = provider.Id;
                _app.Settings.Agent.DefaultModelId = choice.DefaultModel;
                _app.SaveSettings();
            }

            return true;
        }

        private void ClearError()
        {
            if (HasError)
            {
                ErrorText = null;
            }
        }

        private void UpdateHint()
        {
            if (HasError)
            {
                return;
            }

            if (!HasWorkspace)
            {
                HintText = "选择一个工作区后即可开始。";
            }
            else if (string.IsNullOrWhiteSpace(_apiKey))
            {
                HintText = "模型可以稍后添加，也可以现在选择供应商并填入 API Key。";
            }
            else
            {
                HintText = "开始时会保存工作区与模型配置。";
            }
        }
    }

    /// <summary>首次引导下拉项。只暴露展示所需字段，具体模型目录由 ProviderPresets 提供。</summary>
    public sealed class ProviderChoice
    {
        public ProviderChoice(string id, string displayName, string baseUrl, string defaultModel)
        {
            Id = id;
            DisplayName = displayName;
            BaseUrl = baseUrl;
            DefaultModel = defaultModel;
        }

        public string Id { get; }

        public string DisplayName { get; }

        public string BaseUrl { get; }

        public string DefaultModel { get; }

        public string Description
        {
            get { return BaseUrl + " · " + DefaultModel; }
        }

        public override string ToString()
        {
            return DisplayName;
        }
    }
}
