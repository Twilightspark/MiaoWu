using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Infrastructure.Json;

namespace MiaoWu.Infrastructure.Tools
{
    /// <summary>
    /// ask_user_question：向用户提一道或多道选择题并等待答复。与审批共用覆盖输入卡的弹窗，
    /// 但语义独立：审批问的是“能不能做”，提问问的是“选哪一个”。
    /// 选项之外用户始终可以自己写一句，所以没有单独的“允许自由文本”开关。
    /// 一次调用最多 4 道题，逐题询问；用户跳过时本工具以取消结束。
    /// </summary>
    public sealed class AskUserQuestionTool : IAgentTool, IToolAuditRedactor
    {
        private const int MaxQuestions = 4;
        private const int MaxQuestionLength = 500;
        private const int MaxHeaderLength = 60;
        private const int MaxOptionLabelLength = 80;
        private const int MaxOptionDescriptionLength = 200;

        private readonly IUserQuestionService _questions;

        public AskUserQuestionTool(IUserQuestionService questions)
        {
            _questions = questions;
        }

        public string Name
        {
            get { return ToolNames.AskUserQuestion; }
        }

        public string Description
        {
            get
            {
                return "向用户提一道或多道选择题并等待答复；每题 1–6 个选项，"
                    + "multi_select 决定单选还是多选，用户也可以自己输入答案。";
            }
        }

        public ToolRisk Risk
        {
            get { return ToolRisk.Low; }
        }

        public bool RequiresApproval
        {
            get { return false; }
        }

        public ToolApprovalPreview Describe(ToolCall call, ToolContext context)
        {
            return null;
        }

        public Task<ToolResult> ExecuteAsync(
            ToolCall call,
            ToolContext context,
            CancellationToken cancellationToken)
        {
            if (_questions == null || context == null || string.IsNullOrWhiteSpace(context.SessionId))
            {
                return Task.FromResult(ToolResult.Failure(
                    call,
                    ToolResultStatus.Failed,
                    "question_unavailable",
                    "当前会话没有提问通道。"));
            }

            List<QuestionSpec> specs;
            try
            {
                specs = ParseQuestions(call);
            }
            catch (ToolArgumentException exception)
            {
                return Task.FromResult(ToolResultFactory.InvalidArguments(call, exception));
            }

            return AskAllAsync(call, context, specs, cancellationToken);
        }

        public string GetAuditArguments(ToolCall call, ToolContext context)
        {
            try
            {
                List<QuestionSpec> specs = ParseQuestions(call);
                int optionCount = 0;
                bool multiSelect = false;
                for (int i = 0; i < specs.Count; i++)
                {
                    optionCount += specs[i].Options.Count;
                    multiSelect |= specs[i].MultiSelect;
                }

                return "questions=" + specs.Count.ToString(CultureInfo.InvariantCulture)
                    + "；options=" + optionCount.ToString(CultureInfo.InvariantCulture)
                    + "；multi_select=" + (multiSelect ? "true" : "false");
            }
            catch (Exception)
            {
                return "ask_user_question";
            }
        }

        private async Task<ToolResult> AskAllAsync(
            ToolCall call,
            ToolContext context,
            List<QuestionSpec> specs,
            CancellationToken cancellationToken)
        {
            var answers = new List<string>(specs.Count);
            for (int i = 0; i < specs.Count; i++)
            {
                QuestionSpec spec = specs[i];
                var request = new UserQuestionRequest
                {
                    RequestId = (string.IsNullOrWhiteSpace(call.Id) ? Guid.NewGuid().ToString("N") : call.Id)
                        + "-q" + (i + 1).ToString(CultureInfo.InvariantCulture),
                    SessionId = context.SessionId,
                    ToolName = Name,
                    Header = spec.Header,
                    Question = spec.Question,
                    Options = spec.Options,
                    MultiSelect = spec.MultiSelect,
                    CreatedAt = context.Now == default(DateTimeOffset) ? DateTimeOffset.Now : context.Now
                };

                UserQuestionAnswer answer;
                try
                {
                    answer = await _questions
                        .AskAsync(request, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return Cancelled(call, answers);
                }

                if (answer == null || answer.IsCancelled)
                {
                    return Cancelled(call, answers);
                }

                answers.Add("问题 " + (i + 1).ToString(CultureInfo.InvariantCulture) + "："
                    + spec.Question + "\n回答：" + AnswerText(answer));
            }

            string content = string.Join("\n\n", answers);
            return ToolResult.Success(
                call,
                content,
                "提问 " + specs.Count.ToString(CultureInfo.InvariantCulture) + " 个，已答复",
                false,
                false);
        }

        private static ToolResult Cancelled(ToolCall call, List<string> answered)
        {
            ToolResult result = ToolResult.Failure(
                call,
                ToolResultStatus.Cancelled,
                "question_cancelled",
                "用户取消了提问。");

            if (answered.Count > 0)
            {
                result.Content = "用户取消了后续提问。已回答的部分：\n\n" + string.Join("\n\n", answered);
            }

            return result;
        }

        private static string AnswerText(UserQuestionAnswer answer)
        {
            if (answer == null)
            {
                return "（未回答）";
            }

            return string.IsNullOrWhiteSpace(answer.Text)
                ? JoinLabels(answer.SelectedLabels)
                : answer.Text.Trim();
        }

        private static string JoinLabels(IReadOnlyList<string> labels)
        {
            if (labels == null || labels.Count == 0)
            {
                return "（未选择）";
            }

            var builder = new StringBuilder();
            for (int i = 0; i < labels.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append("；");
                }

                builder.Append(labels[i]);
            }

            return builder.ToString();
        }

        private static List<QuestionSpec> ParseQuestions(ToolCall call)
        {
            ToolArguments arguments = ToolArguments.Parse(call == null ? null : call.ArgumentsJson);
            IReadOnlyList<JsonValue> rawQuestions = arguments.GetOptionalArray("questions");
            if (rawQuestions == null || rawQuestions.Count == 0)
            {
                throw new ToolArgumentException("invalid_arguments", "questions 不能为空。");
            }

            if (rawQuestions.Count > MaxQuestions)
            {
                throw new ToolArgumentException(
                    "invalid_arguments",
                    "一次最多提 " + MaxQuestions + " 个问题。");
            }

            var specs = new List<QuestionSpec>(rawQuestions.Count);
            for (int i = 0; i < rawQuestions.Count; i++)
            {
                JsonValue item = rawQuestions[i];
                if (item == null || item.Kind != JsonKind.Object)
                {
                    throw new ToolArgumentException("invalid_arguments", "questions 的每一项都必须是对象。");
                }

                string question = ReadString(item, "question", MaxQuestionLength);
                if (string.IsNullOrWhiteSpace(question))
                {
                    throw new ToolArgumentException("invalid_arguments", "第 " + (i + 1) + " 个问题缺少 question。");
                }

                string header = ReadString(item, "header", MaxHeaderLength);
                bool multiSelect = ReadBool(item, "multi_select");
                IReadOnlyList<UserQuestionOption> options = ParseOptions(item.Get("options"), i);
                if (options.Count == 0)
                {
                    throw new ToolArgumentException(
                        "invalid_arguments",
                        "第 " + (i + 1) + " 个问题至少要有一个选项。");
                }

                specs.Add(new QuestionSpec
                {
                    Question = question.Trim(),
                    Header = string.IsNullOrWhiteSpace(header) ? null : header.Trim(),
                    MultiSelect = multiSelect,
                    Options = options
                });
            }

            return specs;
        }

        private static IReadOnlyList<UserQuestionOption> ParseOptions(JsonValue raw, int questionIndex)
        {
            if (raw == null || raw.IsNull)
            {
                return new UserQuestionOption[0];
            }

            if (raw.Kind != JsonKind.Array)
            {
                throw new ToolArgumentException("invalid_arguments", "options 必须是数组。");
            }

            if (raw.Count > UserQuestionRequest.MaxOptions)
            {
                throw new ToolArgumentException(
                    "invalid_arguments",
                    "每个问题最多 " + UserQuestionRequest.MaxOptions + " 个选项。");
            }

            var options = new List<UserQuestionOption>(raw.Count);
            var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < raw.Count; i++)
            {
                JsonValue item = raw.Get(i);
                if (item == null || item.Kind != JsonKind.Object)
                {
                    throw new ToolArgumentException("invalid_arguments", "每个选项都必须是对象。");
                }

                string label = ReadString(item, "label", MaxOptionLabelLength);
                if (string.IsNullOrWhiteSpace(label))
                {
                    throw new ToolArgumentException("invalid_arguments", "选项缺少 label。");
                }

                label = label.Trim();
                if (!labels.Add(label))
                {
                    throw new ToolArgumentException("invalid_arguments", "选项 label 不能重复：" + label + "。");
                }

                string description = ReadString(item, "description", MaxOptionDescriptionLength);
                options.Add(new UserQuestionOption
                {
                    Id = "q" + (questionIndex + 1).ToString(CultureInfo.InvariantCulture)
                        + "o" + (i + 1).ToString(CultureInfo.InvariantCulture),
                    Label = label,
                    Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim()
                });
            }

            return options;
        }

        private static string ReadString(JsonValue item, string name, int maxLength)
        {
            JsonValue value = item.Get(name);
            if (value == null || value.IsNull)
            {
                return null;
            }

            string text = value.AsString(null);
            if (text == null)
            {
                throw new ToolArgumentException("invalid_arguments", "参数 " + name + " 必须是字符串。");
            }

            if (maxLength > 0 && text.Length > maxLength)
            {
                throw new ToolArgumentException(
                    "invalid_arguments",
                    "参数 " + name + " 超过 " + maxLength + " 个字符。");
            }

            return text;
        }

        private static bool ReadBool(JsonValue item, string name)
        {
            JsonValue value = item.Get(name);
            return value != null && !value.IsNull && value.AsBoolean(false);
        }

        private sealed class QuestionSpec
        {
            public string Question;
            public string Header;
            public bool MultiSelect;
            public IReadOnlyList<UserQuestionOption> Options;
        }
    }
}
