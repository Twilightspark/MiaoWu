using MiaoWu.App.Mvvm;
using MiaoWu.App.Resources;
using MiaoWu.Core.Documents;

namespace MiaoWu.App.ViewModels
{
    /// <summary>中栏的一个标签：持久化模型 + 当前内容。</summary>
    public sealed class DocumentTabViewModel : ObservableObject
    {
        private readonly CanvasViewModel _owner;
        private DocumentContentViewModel _content;
        private bool _isSelected;

        public DocumentTabViewModel(
            CanvasViewModel owner,
            DocumentTab model,
            DocumentContentViewModel content,
            string toolTip)
        {
            _owner = owner;
            Model = model;
            _content = content;
            ToolTip = toolTip ?? model.Title ?? string.Empty;

            CloseCommand = new RelayCommand(() => _owner.CloseTab(this));
            ActivateCommand = new RelayCommand(() => _owner.SelectedTab = this);
        }

        public DocumentTab Model { get; }

        public string Id
        {
            get { return Model.Id; }
        }

        public DocumentTabKind Kind
        {
            get { return Model.Kind; }
        }

        public string Title
        {
            get { return Model.Title; }
        }

        public string FilePath
        {
            get { return Model.FilePath; }
        }

        public bool IsFileTab
        {
            get { return Model.IsFileTab; }
        }

        public string ToolTip { get; private set; }

        public DocumentContentViewModel Content
        {
            get { return _content; }
            private set
            {
                if (ReferenceEquals(_content, value))
                {
                    return;
                }

                _content = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(MetaText));
            }
        }

        public string MetaText
        {
            get { return _content == null ? string.Empty : _content.MetaText; }
        }

        public bool IsSelected
        {
            get { return _isSelected; }
            set { SetProperty(ref _isSelected, value); }
        }

        public string AutomationName
        {
            get { return Title + " 标签"; }
        }

        public string CloseToolTip
        {
            get { return Strings.CloseTab; }
        }

        public RelayCommand CloseCommand { get; }

        public RelayCommand ActivateCommand { get; }

        public void ReplaceContent(DocumentContentViewModel content)
        {
            Content = content;
        }

        public void NotifyMetaChanged()
        {
            OnPropertyChanged(nameof(MetaText));
        }

        /// <summary>文件在磁盘上重命名后同步标签路径与标题。</summary>
        public void ApplyRename(string filePath, string title)
        {
            Model.FilePath = filePath;
            Model.Title = title ?? string.Empty;
            ToolTip = title ?? string.Empty;
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(FilePath));
            OnPropertyChanged(nameof(ToolTip));
            OnPropertyChanged(nameof(AutomationName));
        }
    }
}
