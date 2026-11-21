using System;
using System.Collections.Generic;
using System.Globalization;
using MiaoWu.App.Mvvm;
using MiaoWu.Core.Agents;
using MiaoWu.Core.Text;

namespace MiaoWu.App.ViewModels
{
    /// <summary>输入框下方三个读数里当前打开的面板。</summary>
    public enum ComposerDockPanel
    {
        None = 0,
        Stats = 1,
        Usage = 2,
        Context = 3
    }

    /// <summary>明细面板里的一行：左侧标签、右侧数值。</summary>
    public sealed class ComposerDockRow
    {
        public ComposerDockRow(string label, string value)
        {
            Label = label ?? string.Empty;
            Value = value ?? string.Empty;
        }

        public string Label { get; }

        public string Value { get; }
    }

    /// <summary>
    /// 输入卡下方那一行读数：会话统计 / Token 用量 / 上下文已用。
    /// 三条都是只读投影，没有数据就整条不渲染；点开才出现明细面板。
    /// </summary>
    public sealed class ComposerDockViewModel : ObservableObject
    {
        private SessionMeter _meter = new SessionMeter();
        private bool _isOpen;
        private bool _compact;
        private ComposerDockPanel _openPanel = ComposerDockPanel.None;
        private readonly List<ComposerDockRow> _statsRows = new List<ComposerDockRow>();
        private readonly List<ComposerDockRow> _usageRows = new List<ComposerDockRow>();
        private readonly List<ComposerDockRow> _contextRows = new List<ComposerDockRow>();

        public IReadOnlyList<ComposerDockRow> StatsRows
        {
            get { return _statsRows; }
        }

        public IReadOnlyList<ComposerDockRow> UsageRows
        {
            get { return _usageRows; }
        }

        public IReadOnlyList<ComposerDockRow> ContextRows
        {
            get { return _contextRows; }
        }

        public bool HasStats
        {
            get { return _meter.Stats != null && _meter.Stats.HasData; }
        }

        public bool HasUsage
        {
            get { return _meter.Usage != null && _meter.Usage.Total > 0; }
        }

        public bool HasContext
        {
            get { return _meter.Context != null && _meter.Context.HasData; }
        }

        public bool HasAny
        {
            get { return HasStats || HasUsage || HasContext; }
        }

        public string StatsSummary
        {
            get
            {
                SessionStats stats = _meter.Stats;
                if (stats == null || !stats.HasData)
                {
                    return string.Empty;
                }

                string text = stats.Turns + " 轮 " + stats.Steps + " 步";
                if (!_compact && stats.TokensPerSecond > 0)
                {
                    text += " · " + FormatSpeed(stats.TokensPerSecond);
                }

                return text;
            }
        }

        public string StatsTitleRight
        {
            get
            {
                SessionStats stats = _meter.Stats;
                return stats == null ? string.Empty : stats.Turns + " 轮 " + stats.Steps + " 步";
            }
        }

        public string UsageSummary
        {
            get
            {
                SessionTokenUsage usage = _meter.Usage;
                if (usage == null || !usage.HasData)
                {
                    return string.Empty;
                }

                string text = TokenEstimator.Format(usage.Total) + " tok";
                if (!_compact && usage.CacheRead > 0 && usage.BilledInput > 0)
                {
                    text += " · 缓存 " + FormatCacheHit(usage.CacheHitRate);
                }

                return text;
            }
        }

        public string UsageTitleRight
        {
            get
            {
                SessionTokenUsage usage = _meter.Usage;
                return usage == null || !usage.HasData
                    ? string.Empty
                    : TokenEstimator.Format(usage.Total) + " tok";
            }
        }

        public double ContextPercent
        {
            get { return _meter.Context == null ? 0 : _meter.Context.Percent; }
        }

        public string ContextPercentText
        {
            get { return _meter.Context == null ? string.Empty : _meter.Context.Percent + "%"; }
        }

        public string ContextTitleRight
        {
            get
            {
                SessionContextUsage context = _meter.Context;
                if (context == null || !context.HasData)
                {
                    return string.Empty;
                }

                return context.Percent + "%";
            }
        }

        public string ContextRatioText
        {
            get
            {
                SessionContextUsage context = _meter.Context;
                if (context == null || !context.HasData)
                {
                    return string.Empty;
                }

                return TokenEstimator.Format(context.UsedTokens)
                    + " / "
                    + TokenEstimator.Format(context.WindowTokens);
            }
        }

        /// <summary>构成条的内宽（面板 268 减两侧 12 内边距）。</summary>
        private const double BarWidth = 244;

        public double SystemBarWidth
        {
            get { return SegmentWidth(_meter.Context == null ? 0 : _meter.Context.SystemTokens); }
        }

        public double ToolsBarWidth
        {
            get { return SegmentWidth(_meter.Context == null ? 0 : _meter.Context.ToolsTokens); }
        }

        public double MessageBarWidth
        {
            get { return SegmentWidth(_meter.Context == null ? 0 : _meter.Context.MessageTokens); }
        }

        private double SegmentWidth(int tokens)
        {
            SessionContextUsage context = _meter.Context;
            if (context == null || tokens <= 0)
            {
                return 0;
            }

            int total = context.SystemTokens + context.ToolsTokens + context.MessageTokens;
            if (total <= 0)
            {
                return 0;
            }

            return BarWidth * tokens / total;
        }

        public bool IsOpen
        {
            get { return _isOpen; }
            private set { SetProperty(ref _isOpen, value); }
        }

        public ComposerDockPanel OpenPanel
        {
            get { return _openPanel; }
            private set { SetProperty(ref _openPanel, value); }
        }

        public bool ShowStats
        {
            get { return _isOpen && _openPanel == ComposerDockPanel.Stats; }
        }

        public bool ShowUsage
        {
            get { return _isOpen && _openPanel == ComposerDockPanel.Usage; }
        }

        public bool ShowContext
        {
            get { return _isOpen && _openPanel == ComposerDockPanel.Context; }
        }

        public void Update(SessionMeter meter)
        {
            _meter = meter ?? new SessionMeter();
            RebuildRows();

            // 模型切换可能暂时让窗口未知：面板主动关闭，不保留过期内容。
            if (_openPanel == ComposerDockPanel.Context && !HasContext)
            {
                Close();
            }

            if (_openPanel == ComposerDockPanel.Stats && !HasStats)
            {
                Close();
            }

            if (_openPanel == ComposerDockPanel.Usage && !HasUsage)
            {
                Close();
            }

            NotifyAll();
        }

        /// <summary>
        /// 窄宽度（右栏最小 384）时摘要只留轮 / 步与总 token，速度与缓存命中移进明细，
        /// 避免三个 pill 超出读数行。
        /// </summary>
        public void SetCompact(bool compact)
        {
            if (_compact == compact)
            {
                return;
            }

            _compact = compact;
            OnPropertyChanged(nameof(StatsSummary));
            OnPropertyChanged(nameof(UsageSummary));
        }

        public void Reset()
        {
            _meter = new SessionMeter();
            RebuildRows();
            Close();
            NotifyAll();
        }

        public void Toggle(ComposerDockPanel panel)
        {
            if (_isOpen && _openPanel == panel)
            {
                Close();
                return;
            }

            if ((panel == ComposerDockPanel.Stats && !HasStats)
                || (panel == ComposerDockPanel.Usage && !HasUsage)
                || (panel == ComposerDockPanel.Context && !HasContext))
            {
                Close();
                return;
            }

            OpenPanel = panel;
            IsOpen = true;
            NotifyPanelVisibility();
        }

        public void Close()
        {
            if (!_isOpen && _openPanel == ComposerDockPanel.None)
            {
                return;
            }

            IsOpen = false;
            OpenPanel = ComposerDockPanel.None;
            NotifyPanelVisibility();
        }

        private void RebuildRows()
        {
            _statsRows.Clear();
            SessionStats stats = _meter.Stats;
            if (stats != null && stats.HasData)
            {
                _statsRows.Add(new ComposerDockRow("模型用时", FormatDuration(stats.LlmMs)));
                _statsRows.Add(new ComposerDockRow("工具调用用时", FormatDuration(stats.ToolMs)));
                _statsRows.Add(new ComposerDockRow(
                    "首 token 平均",
                    stats.TtftSteps > 0 ? FormatDuration(stats.AverageTtftMs) : "—"));
                _statsRows.Add(new ComposerDockRow(
                    "输出速度",
                    stats.TokensPerSecond > 0 ? FormatSpeed(stats.TokensPerSecond) : "—"));
            }

            _usageRows.Clear();
            SessionTokenUsage usage = _meter.Usage;
            if (usage != null && usage.HasData)
            {
                _usageRows.Add(new ComposerDockRow(
                    "缓存命中",
                    usage.BilledInput > 0 ? FormatCacheHit(usage.CacheHitRate) : "—"));
                _usageRows.Add(new ComposerDockRow("未缓存输入", FormatTokens(usage.UncachedInput)));
                _usageRows.Add(new ComposerDockRow("缓存读取", FormatTokens(usage.CacheRead)));
                if (usage.CacheWrite > 0)
                {
                    _usageRows.Add(new ComposerDockRow("缓存写入", FormatTokens(usage.CacheWrite)));
                }

                _usageRows.Add(new ComposerDockRow("输出", FormatTokens(usage.Output)));
            }

            _contextRows.Clear();
            SessionContextUsage context = _meter.Context;
            if (context != null && context.HasData)
            {
                _contextRows.Add(new ComposerDockRow("系统提示词", "≈ " + FormatTokens(context.SystemTokens)));
                _contextRows.Add(new ComposerDockRow("工具定义", "≈ " + FormatTokens(context.ToolsTokens)));
                _contextRows.Add(new ComposerDockRow("对话消息", "≈ " + FormatTokens(context.MessageTokens)));
            }
        }

        private void NotifyAll()
        {
            OnPropertyChanged(nameof(HasStats));
            OnPropertyChanged(nameof(HasUsage));
            OnPropertyChanged(nameof(HasContext));
            OnPropertyChanged(nameof(HasAny));
            OnPropertyChanged(nameof(StatsSummary));
            OnPropertyChanged(nameof(StatsTitleRight));
            OnPropertyChanged(nameof(UsageSummary));
            OnPropertyChanged(nameof(UsageTitleRight));
            OnPropertyChanged(nameof(ContextPercent));
            OnPropertyChanged(nameof(ContextPercentText));
            OnPropertyChanged(nameof(ContextTitleRight));
            OnPropertyChanged(nameof(ContextRatioText));
            OnPropertyChanged(nameof(SystemBarWidth));
            OnPropertyChanged(nameof(ToolsBarWidth));
            OnPropertyChanged(nameof(MessageBarWidth));
            OnPropertyChanged(nameof(StatsRows));
            OnPropertyChanged(nameof(UsageRows));
            OnPropertyChanged(nameof(ContextRows));
            NotifyPanelVisibility();
        }

        private void NotifyPanelVisibility()
        {
            OnPropertyChanged(nameof(ShowStats));
            OnPropertyChanged(nameof(ShowUsage));
            OnPropertyChanged(nameof(ShowContext));
        }

        private static string FormatTokens(int tokens)
        {
            return TokenEstimator.Format(Math.Max(0, tokens));
        }

        /// <summary>速度保留一位小数；0 不显示。</summary>
        private static string FormatSpeed(double tokensPerSecond)
        {
            return tokensPerSecond.ToString("0.0", CultureInfo.InvariantCulture) + " tok/s";
        }

        /// <summary>缓存命中接近 100% 时增加小数位，避免四舍五入成假的 100%。</summary>
        private static string FormatCacheHit(double rate)
        {
            if (rate <= 0)
            {
                return "0%";
            }

            double percent = rate * 100;
            if (rate < 1 && percent >= 99.95)
            {
                return percent.ToString("0.000", CultureInfo.InvariantCulture) + "%";
            }

            if (Math.Abs(percent - 100) < 0.0001)
            {
                return "100%";
            }

            return percent.ToString("0.0", CultureInfo.InvariantCulture) + "%";
        }

        /// <summary>把毫秒换成中文时长：秒 / 分秒 / 小时分。</summary>
        public static string FormatDuration(long milliseconds)
        {
            if (milliseconds <= 0)
            {
                return "0 秒";
            }

            if (milliseconds < 60000)
            {
                double seconds = milliseconds / 1000.0;
                if (seconds < 10)
                {
                    return seconds.ToString("0.0", CultureInfo.InvariantCulture) + " 秒";
                }

                return ((int)Math.Round(seconds)).ToString(CultureInfo.InvariantCulture) + " 秒";
            }

            if (milliseconds < 3600000)
            {
                int minutes = (int)(milliseconds / 60000);
                int seconds = (int)Math.Round((milliseconds % 60000) / 1000.0);
                if (seconds == 60)
                {
                    minutes++;
                    seconds = 0;
                }

                return seconds == 0
                    ? minutes + " 分"
                    : minutes + " 分 " + seconds + " 秒";
            }

            int hours = (int)(milliseconds / 3600000);
            int restMinutes = (int)((milliseconds % 3600000) / 60000);
            return restMinutes == 0
                ? hours + " 小时"
                : hours + " 小时 " + restMinutes + " 分";
        }
    }
}
