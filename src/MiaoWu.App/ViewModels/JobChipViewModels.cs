using System;
using System.Text;
using MiaoWu.App.Mvvm;
using MiaoWu.Infrastructure.Tools;

namespace MiaoWu.App.ViewModels
{
    /// <summary>
    /// 会话标题栏里的一个后台任务行：只投影 <see cref="BackgroundJobInfo"/>，
    /// 不持有注册表；由 <see cref="ConversationViewModel"/> 每秒刷新。
    /// </summary>
    public sealed class JobChipItemViewModel : ObservableObject
    {
        private bool _isRunning;
        private bool _isFailed;
        private string _statusText;
        private string _timeText;
        private bool _showGroupHeader;
        private string _groupHeader;

        public JobChipItemViewModel(BackgroundJobInfo info)
        {
            if (info == null)
            {
                throw new ArgumentNullException(nameof(info));
            }

            Id = info.Id;
            Command = info.Command ?? string.Empty;
            Title = BuildTitle(info);
            Update(info, DateTimeOffset.UtcNow);
        }

        public string Id { get; }

        /// <summary>标题行：优先用工具传入的描述，否则把命令压成一行。</summary>
        public string Title { get; }

        public string Command { get; }

        public string ToolTip
        {
            get { return Command; }
        }

        public bool IsRunning
        {
            get { return _isRunning; }
            private set
            {
                if (SetProperty(ref _isRunning, value))
                {
                    OnPropertyChanged(nameof(IsSettled));
                    OnPropertyChanged(nameof(ShowStop));
                }
            }
        }

        public bool IsSettled
        {
            get { return !_isRunning; }
        }

        public bool IsFailed
        {
            get { return _isFailed; }
            private set { SetProperty(ref _isFailed, value); }
        }

        /// <summary>只有运行中的任务才画「停止」。</summary>
        public bool ShowStop
        {
            get { return _isRunning; }
        }

        public string StatusText
        {
            get { return _statusText; }
            private set { SetProperty(ref _statusText, value); }
        }

        public string TimeText
        {
            get { return _timeText; }
            private set { SetProperty(ref _timeText, value); }
        }

        /// <summary>行上方是否画分组标题（运行中 / 已结束）。</summary>
        public bool ShowGroupHeader
        {
            get { return _showGroupHeader; }
            private set { SetProperty(ref _showGroupHeader, value); }
        }

        public string GroupHeader
        {
            get { return _groupHeader; }
            private set { SetProperty(ref _groupHeader, value); }
        }

        public string DetailText
        {
            get { return StatusText + " · " + TimeText; }
        }

        public string AutomationName
        {
            get { return Title + "，" + DetailText; }
        }

        public void Update(BackgroundJobInfo info, DateTimeOffset now)
        {
            if (info == null)
            {
                return;
            }

            IsRunning = info.Running;
            IsFailed = !info.Running
                && (info.Killed || !info.ExitCode.HasValue || info.ExitCode.Value != 0);

            string status;
            if (info.Running)
            {
                status = "运行中";
            }
            else if (info.Killed)
            {
                status = "已停止";
            }
            else if (!info.ExitCode.HasValue)
            {
                status = "已结束";
            }
            else if (info.ExitCode.Value == 0)
            {
                status = "已完成";
            }
            else
            {
                status = "退出码 " + info.ExitCode.Value;
            }

            StatusText = status;
            TimeText = FormatElapsed(info, now);
            OnPropertyChanged(nameof(DetailText));
            OnPropertyChanged(nameof(AutomationName));
        }

        /// <summary>注册表按开始时间倒序返回，这里只负责给每组第一行标一个标题。</summary>
        public void SetGroup(string header)
        {
            GroupHeader = header;
            ShowGroupHeader = !string.IsNullOrEmpty(header);
        }

        private static string BuildTitle(BackgroundJobInfo info)
        {
            if (!string.IsNullOrWhiteSpace(info.Description))
            {
                return Summarize(info.Description);
            }

            return Summarize(info.Command);
        }

        private static string Summarize(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "(空命令)";
            }

            string oneLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return oneLine.Length <= 60 ? oneLine : oneLine.Substring(0, 60) + "…";
        }

        private static string FormatElapsed(BackgroundJobInfo info, DateTimeOffset now)
        {
            DateTimeOffset end = info.CompletedAt ?? now;
            TimeSpan elapsed = end - info.StartedAt;
            if (elapsed < TimeSpan.Zero)
            {
                elapsed = TimeSpan.Zero;
            }

            long total = (long)Math.Floor(elapsed.TotalSeconds);
            long hours = total / 3600;
            long minutes = (total % 3600) / 60;
            long seconds = total % 60;

            var text = new StringBuilder();
            if (hours > 0)
            {
                text.Append(hours).Append("小时");
            }

            if (total >= 60)
            {
                text.Append(minutes).Append("分");
            }

            text.Append(seconds).Append("秒");
            return text.ToString();
        }
    }
}
