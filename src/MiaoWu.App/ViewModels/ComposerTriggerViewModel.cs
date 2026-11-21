using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using MiaoWu.App.Mvvm;
using MiaoWu.Core.Input;
using MiaoWu.Infrastructure.Workspaces;

namespace MiaoWu.App.ViewModels
{
    /// <summary>`/` 与 `@` 菜单里的一行：组标题或一条候选。</summary>
    public sealed class ComposerMenuRowViewModel : ObservableObject
    {
        private bool _isHighlighted;

        public bool IsHeader { get; set; }

        public string HeaderText { get; set; }

        public string Source { get; set; }

        public int Index { get; set; }

        public string Label { get; set; }

        public string Detail { get; set; }

        public string IconKey { get; set; }

        public bool IsDirectory { get; set; }

        public string ToolTip { get; set; }

        /// <summary>候选来源数据：命令是 <see cref="ComposerCommand"/>，引用是 <see cref="WorkspaceFileEntry"/>。</summary>
        public object Payload { get; set; }

        public bool IsHighlighted
        {
            get { return _isHighlighted; }
            set { SetProperty(ref _isHighlighted, value); }
        }
    }

    /// <summary>选中一行之后要做什么。由视图把结果落到编辑器 / 命令执行。</summary>
    public enum ComposerPickKind
    {
        None,
        Mention,
        Text,
        Command
    }

    public sealed class ComposerPickResult
    {
        public ComposerPickKind Kind { get; set; }

        public TriggerHit Hit { get; set; }

        public ComposerMention Mention { get; set; }

        public string Text { get; set; }

        public bool ContinueMenu { get; set; }

        public ComposerCommand Command { get; set; }

        public static ComposerPickResult None()
        {
            return new ComposerPickResult { Kind = ComposerPickKind.None };
        }
    }

    /// <summary>
    /// 输入框触发控制器：把纯内核的检测 / 归约接到会话上。
    /// 每个 <see cref="ConversationViewModel"/> 一个实例，随会话切换重置；
    /// 只做三件事：track、取候选、把选中翻译成 <see cref="ComposerPickResult"/>。
    /// </summary>
    public sealed class ComposerTriggerViewModel : ObservableObject
    {
        private const string CommandSource = "command";
        private const string ReferenceSource = "reference";

        private readonly Func<bool> _isPlanMode;
        private readonly Func<bool> _hasMessages;
        private readonly Func<string> _workspaceRoot;

        private InputTriggerMenuState _state = InputTriggerMenuReducer.Seed(null);
        private WorkspaceFileIndex _fileIndex;
        private string _dismissedSignature;
        private bool _isOpen;

        public ComposerTriggerViewModel(
            Func<bool> isPlanMode,
            Func<bool> hasMessages,
            Func<string> workspaceRoot)
        {
            _isPlanMode = isPlanMode ?? (() => false);
            _hasMessages = hasMessages ?? (() => false);
            _workspaceRoot = workspaceRoot ?? (() => null);
            Rows = new ObservableCollection<ComposerMenuRowViewModel>();
        }

        public ObservableCollection<ComposerMenuRowViewModel> Rows { get; private set; }

        public bool IsOpen
        {
            get { return _isOpen; }
            private set
            {
                if (SetProperty(ref _isOpen, value))
                {
                    OnPropertyChanged(nameof(IsClosed));
                }
            }
        }

        public bool IsClosed
        {
            get { return !_isOpen; }
        }

        public bool HasHighlight
        {
            get { return _state.HasHighlight; }
        }

        /// <summary>会话 / 工作区切换时调用：关掉菜单、清掉拒绝记忆与文件索引。</summary>
        public void Reset()
        {
            _dismissedSignature = null;
            _state = InputTriggerMenuReducer.Seed(null);
            Rows.Clear();
            IsOpen = false;
            _fileIndex = null;
        }

        /// <summary>工作区变了：丢弃旧文件索引，下一次 `@` 重新扫描。</summary>
        public void ResetWorkspace()
        {
            _fileIndex = null;
        }

        /// <summary>
        /// 草稿或光标变化时调用。返回菜单是否吃掉了这次变化（当前始终返回 false，
        /// 键盘裁决由视图显式调用 <see cref="Move"/> / <see cref="PickHighlighted"/>）。
        /// </summary>
        public void Track(string text, int caret)
        {
            TriggerHit hit = TriggerDetector.Detect(text, caret, TriggerGuardTier.Plain);
            if (hit == null)
            {
                Close(false);
                return;
            }

            if (_dismissedSignature != null
                && string.Equals(_dismissedSignature, hit.Signature, StringComparison.Ordinal))
            {
                Close(false);
                return;
            }

            if (!_state.IsOpen
                || _state.Hit == null
                || _state.Hit.Trigger != hit.Trigger)
            {
                _state = InputTriggerMenuReducer.Seed(BuildGroups(hit.Trigger));
            }

            Apply(new MenuHitEvent(hit));

            long generation = _state.Generation;
            if (hit.Trigger == TriggerChar.Slash)
            {
                Apply(new MenuGroupSettledEvent(generation, CommandSource, BuildCommandCandidates(hit)));
            }
            else
            {
                Apply(new MenuGroupSettledEvent(generation, ReferenceSource, BuildReferenceCandidates(hit)));
            }
        }

        public bool Move(int delta)
        {
            if (!IsOpen)
            {
                return false;
            }

            Apply(new MenuMoveEvent(delta));
            return HasHighlight;
        }

        public void Hover(ComposerMenuRowViewModel row)
        {
            if (row == null || row.IsHeader)
            {
                return;
            }

            Apply(new MenuHoverEvent(row.Source, row.Index));
        }

        /// <summary>Esc / 外部点击：关闭并记住“这个 token 被拒绝”。</summary>
        public void Dismiss()
        {
            if (_state.Hit != null)
            {
                _dismissedSignature = _state.Hit.Signature;
            }

            Close(false);
        }

        public void Close(bool remember)
        {
            if (remember && _state.Hit != null)
            {
                _dismissedSignature = _state.Hit.Signature;
            }

            if (!_state.IsOpen && Rows.Count == 0)
            {
                return;
            }

            Apply(new MenuCloseEvent());
            Rows.Clear();
            OnPropertyChanged(nameof(HasHighlight));
        }

        /// <summary>选中当前高亮行；没有高亮时返回 <see cref="ComposerPickKind.None"/>。</summary>
        /// <param name="drill">true 表示 Tab 下钻：目录行只把路径写回草稿并保持菜单打开。</param>
        public ComposerPickResult PickHighlighted(bool drill = false)
        {
            InputTriggerCandidate candidate = _state.Highlighted;
            return PickCandidate(candidate, drill);
        }

        /// <summary>指针点选某一行：点目录行把目录本身作为引用插入，不自动下钻。</summary>
        public ComposerPickResult PickRow(ComposerMenuRowViewModel row)
        {
            if (row == null || row.IsHeader)
            {
                return ComposerPickResult.None();
            }

            InputTriggerGroup group = _state.FindGroup(row.Source);
            if (group == null || row.Index < 0 || row.Index >= group.Items.Count)
            {
                return ComposerPickResult.None();
            }

            return PickCandidate(group.Items[row.Index], false);
        }

        private ComposerPickResult PickCandidate(InputTriggerCandidate candidate, bool drill)
        {
            if (candidate == null)
            {
                return ComposerPickResult.None();
            }

            TriggerHit hit = _state.Hit == null ? null : _state.Hit.Clone();
            _dismissedSignature = hit == null ? null : hit.Signature;
            Apply(new MenuCloseEvent());
            Rows.Clear();

            var command = candidate.Payload as ComposerCommand;
            if (command != null)
            {
                return new ComposerPickResult
                {
                    Kind = ComposerPickKind.Command,
                    Command = command,
                    Hit = hit
                };
            }

            var entry = candidate.Payload as WorkspaceFileEntry;
            if (entry == null)
            {
                return ComposerPickResult.None();
            }

            if (entry.IsDirectory && drill)
            {
                // 目录行 + Tab / 行尾箭头：把文字写回草稿并保留下钻状态；引号保持打开，
                // 下一次击键仍在同一 token 上。
                string text = MentionFormatter.Format(entry.RelativePath, true, true);
                if (text == null)
                {
                    return ComposerPickResult.None();
                }

                return new ComposerPickResult
                {
                    Kind = ComposerPickKind.Text,
                    Text = text,
                    ContinueMenu = true,
                    Hit = hit
                };
            }

            ComposerMention mention = ToMention(entry);
            if (mention == null)
            {
                return ComposerPickResult.None();
            }

            return new ComposerPickResult
            {
                Kind = ComposerPickKind.Mention,
                Mention = mention,
                Hit = hit
            };
        }

        /// <summary>把文件索引的一行变成引用；无法用 mention 语法表达时返回 null。</summary>
        public static ComposerMention ToMention(WorkspaceFileEntry entry)
        {
            if (entry == null)
            {
                return null;
            }

            string text = MentionFormatter.Format(entry.RelativePath, entry.IsDirectory, false);
            if (text == null)
            {
                return null;
            }

            ComposerMentionKind kind = entry.IsDirectory
                ? ComposerMentionKind.Folder
                : (entry.IsImage ? ComposerMentionKind.Image : ComposerMentionKind.File);

            return new ComposerMention
            {
                RelativePath = entry.RelativePath,
                DisplayName = entry.Name,
                ParentPath = entry.ParentPath,
                Kind = kind
            };
        }

        private IEnumerable<InputTriggerGroup> BuildGroups(TriggerChar trigger)
        {
            if (trigger == TriggerChar.Slash)
            {
                return new[]
                {
                    new InputTriggerGroup(CommandSource, "指令", 0, true, null)
                };
            }

            return new[]
            {
                new InputTriggerGroup(ReferenceSource, "文件", 0, false, null)
            };
        }

        private IReadOnlyList<InputTriggerCandidate> BuildCommandCandidates(TriggerHit hit)
        {
            IReadOnlyList<ComposerCommand> commands = ComposerCommandCatalog.Match(
                hit.Query,
                _isPlanMode(),
                _hasMessages());

            var candidates = new List<InputTriggerCandidate>(commands.Count);
            foreach (ComposerCommand command in commands)
            {
                candidates.Add(new InputTriggerCandidate
                {
                    Id = command.Name,
                    Label = command.Title,
                    Detail = command.Alias,
                    Section = string.Empty,
                    Payload = command
                });
            }

            return candidates;
        }

        private IReadOnlyList<InputTriggerCandidate> BuildReferenceCandidates(TriggerHit hit)
        {
            WorkspaceFileIndex index = GetFileIndex();
            if (index == null)
            {
                return new List<InputTriggerCandidate>();
            }

            IReadOnlyList<WorkspaceFileEntry> entries = index.Search(hit.Query, MentionSearch.DefaultLimit);
            var candidates = new List<InputTriggerCandidate>(entries.Count);
            foreach (WorkspaceFileEntry entry in entries)
            {
                // mention 语法无法表达的路径（含控制字符 / 内嵌引号）不出行。
                if (MentionFormatter.Format(entry.RelativePath, entry.IsDirectory, false) == null)
                {
                    continue;
                }

                candidates.Add(new InputTriggerCandidate
                {
                    Id = entry.RelativePath,
                    Label = entry.Name,
                    Detail = entry.ParentPath,
                    IsDirectory = entry.IsDirectory,
                    Payload = entry
                });
            }

            return candidates;
        }

        private WorkspaceFileIndex GetFileIndex()
        {
            string root = _workspaceRoot();
            if (string.IsNullOrWhiteSpace(root))
            {
                return null;
            }

            if (_fileIndex != null
                && string.Equals(_fileIndex.RootPath, root, StringComparison.OrdinalIgnoreCase))
            {
                return _fileIndex;
            }

            _fileIndex = new WorkspaceFileIndex(root);
            return _fileIndex;
        }

        private void Apply(InputTriggerMenuEvent evt)
        {
            _state = InputTriggerMenuReducer.Reduce(_state, evt);
            IsOpen = _state.IsOpen;
            RebuildRows();
            OnPropertyChanged(nameof(HasHighlight));
        }

        private void RebuildRows()
        {
            Rows.Clear();
            if (!_state.IsOpen)
            {
                return;
            }

            foreach (InputTriggerGroup group in _state.Groups)
            {
                if (group.ShowTitle && group.Items.Count > 0)
                {
                    Rows.Add(new ComposerMenuRowViewModel
                    {
                        IsHeader = true,
                        HeaderText = group.Title,
                        Source = group.Source,
                        Index = -1
                    });
                }

                for (int i = 0; i < group.Items.Count; i++)
                {
                    InputTriggerCandidate item = group.Items[i];
                    var command = item.Payload as ComposerCommand;
                    var entry = item.Payload as WorkspaceFileEntry;

                    string icon = "Ico.File";
                    if (command != null)
                    {
                        icon = IconForCommand(command.Kind);
                    }
                    else if (entry != null)
                    {
                        icon = entry.IsDirectory ? "Ico.Folder" : (entry.IsImage ? "Ico.Image" : "Ico.File");
                    }

                    Rows.Add(new ComposerMenuRowViewModel
                    {
                        Source = group.Source,
                        Index = i,
                        Label = item.Label,
                        Detail = item.Detail,
                        IconKey = icon,
                        IsDirectory = item.IsDirectory,
                        ToolTip = command != null ? command.Description : item.Id,
                        Payload = item.Payload,
                        IsHighlighted = string.Equals(_state.HighlightSource, group.Source, StringComparison.Ordinal)
                            && _state.HighlightIndex == i
                    });
                }
            }
        }

        private static string IconForCommand(ComposerCommandKind kind)
        {
            switch (kind)
            {
                case ComposerCommandKind.Compact:
                    return "Ico.Sparkle";
                case ComposerCommandKind.Plan:
                    return "Ico.Edit";
                case ComposerCommandKind.Work:
                    return "Ico.Check";
                default:
                    return "Ico.Sparkle";
            }
        }
    }
}
