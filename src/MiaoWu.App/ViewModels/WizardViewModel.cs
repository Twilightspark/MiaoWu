using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using MiaoWu.App.Mvvm;
using MiaoWu.App.Services;
using MiaoWu.Core.Abstractions;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Core.Models;
using MiaoWu.Core.Workspaces;
using System.Windows.Threading;

namespace MiaoWu.App.ViewModels
{
    /// <summary>向导步骤枚举。5 步：欢迎/隐私 → 数据目录 → Provider（可跳过）→ 工作区 → 完成。</summary>
    public enum WizardStep
    {
        Welcome,
        DataDirectory,
        Provider,
        Workspace,
        Finish
    }

    /// <summary>
    /// 首次启动向导。只写 settings.json / providers.json / secrets.dat；
    /// 数据目录改到其他位置需要重启，这里只展示，不真的搬移。
    /// </summary>
    public sealed class WizardViewModel : ObservableObject
    {
        private readonly AppServices _app;
        private readonly IFolderPicker _folderPicker;
        private readonly Action _refreshWorkspaces;

        private WizardStep _step;
        private bool _isOpen;
        private string _workspacePath;
        private string _workspaceName;
        private string _providerId = "deepseek";
        private string _apiKey;
        private string _providerStatus;
        private string _errorText;
        private bool _enableTools = true;

        public WizardViewModel(AppServices app, IFolderPicker folderPicker, Action refreshWorkspaces)
        {
            _app = app ?? throw new ArgumentNullException(nameof(app));
            _folderPicker = folderPicker;
            _refreshWorkspaces = refreshWorkspaces;

            ProviderChoices = new ObservableCollection<ProviderChoice>
            {
                new ProviderChoice("deepseek", "DeepSeek", "https://api.deepseek.com/v1", "deepseek-chat"),
                new ProviderChoice("openai", "OpenAI", "https://api.openai.com/v1", "gpt-4o-mini"),
                new ProviderChoice("anthropic", "Anthropic", "https://api.anthropic.com", "claude-sonnet-4-5"),
                new ProviderChoice("custom", "自建 / 兼容网关", "http://localhost:11434/v1", "local-model")
            };

            NextCommand = new RelayCommand(Next, () => CanNext);
            BackCommand = new RelayCommand(Back, () => CanBack);
            FinishCommand = new RelayCommand(Finish, () => CanFinish);
            SkipCommand = new RelayCommand(Skip, () => _step == WizardStep.Provider);
            PickWorkspaceCommand = new RelayCommand(PickWorkspace);
            CloseCommand = new RelayCommand(() => IsOpen = false);

            _workspacePath = SuggestWorkspacePath();
            _workspaceName = System.IO.Path.GetFileName(_workspacePath);
            SelectedChoice = ProviderChoices[0];
        }

        public ObservableCollection<ProviderChoice> ProviderChoices { get; }

        public RelayCommand NextCommand { get; }

        public RelayCommand BackCommand { get; }

        public RelayCommand FinishCommand { get; }

        public RelayCommand SkipCommand { get; }

        public RelayCommand PickWorkspaceCommand { get; }

        public RelayCommand CloseCommand { get; }

        public bool IsOpen
        {
            get { return _isOpen; }
            set { SetProperty(ref _isOpen, value); }
        }

        public WizardStep Step
        {
            get { return _step; }
            private set
            {
                if (SetProperty(ref _step, value))
                {
                    RaiseStepFlags();
                }
            }
        }

        public bool IsWelcomeStep
        {
            get { return _step == WizardStep.Welcome; }
        }

        public bool IsDataStep
        {
            get { return _step == WizardStep.DataDirectory; }
        }

        public bool IsProviderStep
        {
            get { return _step == WizardStep.Provider; }
        }

        public bool IsWorkspaceStep
        {
            get { return _step == WizardStep.Workspace; }
        }

        public bool IsFinishStep
        {
            get { return _step == WizardStep.Finish; }
        }

        public string StepText
        {
            get { return "第 " + (((int)_step) + 1) + " / 5 步"; }
        }

        public string DataDirectory
        {
            get { return _app.Paths.RootDirectory; }
        }

        public string WorkspacePath
        {
            get { return _workspacePath; }
            set { SetProperty(ref _workspacePath, value); }
        }

        public string WorkspaceName
        {
            get { return _workspaceName; }
            set { SetProperty(ref _workspaceName, value); }
        }

        public string ProviderId
        {
            get { return _providerId; }
            set { SetProperty(ref _providerId, value); }
        }

        public string ApiKey
        {
            get { return _apiKey; }
            set { SetProperty(ref _apiKey, value); }
        }

        public string ProviderStatus
        {
            get { return _providerStatus; }
            private set { SetProperty(ref _providerStatus, value); }
        }

        public string ErrorText
        {
            get { return _errorText; }
            private set
            {
                if (SetProperty(ref _errorText, value))
                {
                    OnPropertyChanged(nameof(HasError));
                }
            }
        }

        public bool HasError
        {
            get { return !string.IsNullOrEmpty(_errorText); }
        }

        public bool EnableTools
        {
            get { return _enableTools; }
            set { SetProperty(ref _enableTools, value); }
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
                if (value == null)
                {
                    return;
                }

                _providerId = value.Id;
                OnPropertyChanged();
                ProviderStatus = value.Description;
            }
        }

        public bool CanBack
        {
            get { return _step != WizardStep.Welcome; }
        }

        public bool CanNext
        {
            get { return _step != WizardStep.Finish; }
        }

        public bool CanFinish
        {
            get { return _step == WizardStep.Finish; }
        }

        public bool ShowNext
        {
            get { return _step != WizardStep.Finish; }
        }

        public void Open()
        {
            Step = WizardStep.Welcome;
            IsOpen = true;
            ErrorText = null;
            ProviderStatus = SelectedChoice == null ? null : SelectedChoice.Description;
        }

        private void Next()
        {
            ErrorText = null;
            if (_step == WizardStep.Provider && !TrySaveProvider())
            {
                return;
            }

            if (_step == WizardStep.Finish)
            {
                Finish();
                return;
            }

            Step = (WizardStep)(((int)_step) + 1);
        }

        private void Back()
        {
            if (_step == WizardStep.Welcome)
            {
                return;
            }

            Step = (WizardStep)(((int)_step) - 1);
        }

        private void Skip()
        {
            ApiKey = null;
            ProviderStatus = "已跳过。进入主界面后可在设置 → 模型与 Provider 配置。";
            Step = WizardStep.Workspace;
        }

        private bool TrySaveProvider()
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                ProviderStatus = "未填写密钥，可点“稍后配置”跳过。";
                return true;
            }

            try
            {
                ProviderChoice choice = SelectedChoice;
                if (choice == null)
                {
                    ErrorText = "请选择 Provider。";
                    return false;
                }

                ProviderCatalog catalog = _app.ProviderConfigStore.Load();
                ProviderDefinition provider = catalog.FindProvider(choice.Id);

                if (provider == null)
                {
                    provider = new ProviderDefinition
                    {
                        Id = choice.Id,
                        DisplayName = choice.DisplayName,
                        Api = choice.Id == "anthropic" ? "anthropic-messages" : "openai-completions",
                        BaseUrl = choice.BaseUrl
                    };
                    provider.Models.Add(new ModelDefinition
                    {
                        Id = choice.DefaultModel,
                        Name = choice.DefaultModel,
                        ContextWindow = 128000,
                        MaxOutputTokens = 8192
                    });
                    catalog.Providers.Add(provider);
                }

                catalog.ActiveProviderId = provider.Id;
                IReadOnlyList<string> errors = MiaoWu.Infrastructure.Providers.ProviderConfigValidator.Validate(catalog);
                if (errors.Count > 0)
                {
                    ErrorText = string.Join("\n", errors);
                    return false;
                }

                _app.SaveProviders(catalog);
                _app.Credentials.SetKey(provider.Id, _apiKey.Trim());
                _app.Settings.Agent.DefaultProviderId = provider.Id;
                _app.Settings.Agent.DefaultModelId = provider.Models[0].Id;
                _app.SaveSettings();

                ProviderStatus = "已保存 " + provider.DisplayName + "。";
                return true;
            }
            catch (Exception exception)
            {
                ErrorText = "保存 Provider 失败：" + exception.Message;
                return false;
            }
        }

        private void PickWorkspace()
        {
            string path = _folderPicker == null
                ? null
                : _folderPicker.PickFolder("选择工作区目录", _workspacePath);

            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            WorkspacePath = path;
            WorkspaceName = System.IO.Path.GetFileName(path);
        }

        private void Finish()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(WorkspacePath))
                {
                    var models = new List<Workspace>();
                    WorkspaceCollection collection = _app.WorkspaceStore.Load() ?? new WorkspaceCollection();
                    models.AddRange(collection.Workspaces ?? new List<Workspace>());

                    Workspace created;
                    string error;
                    if (WorkspacePolicy.TryAdd(models, WorkspacePath, out created, out error))
                    {
                        collection.Workspaces = models;
                        collection.ActiveWorkspaceId = created.Id;
                        _app.WorkspaceStore.Save(collection);
                    }
                    else
                    {
                        ErrorText = error;
                        return;
                    }
                }

                _app.Settings.Agent.OnboardingCompleted = true;
                _app.Settings.Agent.ToolsEnabled = _enableTools;
                _app.SaveSettings();
                IsOpen = false;

                if (_refreshWorkspaces != null)
                {
                    _refreshWorkspaces();
                }
            }
            catch (Exception exception)
            {
                ErrorText = "完成向导失败：" + exception.Message;
            }
        }

        private void RaiseStepFlags()
        {
            OnPropertyChanged(nameof(IsWelcomeStep));
            OnPropertyChanged(nameof(IsDataStep));
            OnPropertyChanged(nameof(IsProviderStep));
            OnPropertyChanged(nameof(IsWorkspaceStep));
            OnPropertyChanged(nameof(IsFinishStep));
            OnPropertyChanged(nameof(StepText));
            OnPropertyChanged(nameof(CanBack));
            OnPropertyChanged(nameof(CanNext));
            OnPropertyChanged(nameof(CanFinish));
            OnPropertyChanged(nameof(ShowNext));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }

        private static string SuggestWorkspacePath()
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return System.IO.Directory.Exists(documents) ? documents : Environment.CurrentDirectory;
        }
    }

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
