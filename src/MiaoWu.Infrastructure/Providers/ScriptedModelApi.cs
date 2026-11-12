using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Errors;
using MiaoWu.Core.Models;

namespace MiaoWu.Infrastructure.Providers
{
    /// <summary>
    /// 脚本化模型：按调用顺序返回预置事件，用于 xUnit 跑完整 loop
    /// （成功、失败、取消、工具回合）而不需要网络。
    /// </summary>
    public sealed class ScriptedModelApi : IModelApi
    {
        private readonly Queue<ScriptedTurn> _turns = new Queue<ScriptedTurn>();
        private readonly object _gate = new object();
        private readonly string _id;

        public ScriptedModelApi()
            : this("scripted")
        {
        }

        /// <summary>id 可覆盖成真实协议名，便于 UI 集成测试复用正常配置。</summary>
        public ScriptedModelApi(string id)
        {
            _id = string.IsNullOrWhiteSpace(id) ? "scripted" : id;
        }

        public string Id
        {
            get { return _id; }
        }

        public int Remaining
        {
            get
            {
                lock (_gate)
                {
                    return _turns.Count;
                }
            }
        }

        public void EnqueueText(string text)
        {
            var events = new List<LlmStreamEvent>();
            if (!string.IsNullOrEmpty(text))
            {
                events.Add(new LlmTextDelta { ContentIndex = 0, Delta = text });
            }

            events.Add(new LlmCompleted
            {
                Response = new LlmResponse
                {
                    Content = new List<LlmContent>
                    {
                        new LlmContent { Type = "text", Text = text ?? string.Empty }
                    },
                    Usage = new TokenUsage { Input = 12, Output = 8, Total = 20 },
                    StopReason = StopReason.Stop,
                    ModelId = "scripted"
                }
            });

            Enqueue(events.ToArray());
        }

        public void EnqueueToolCalls(params ToolCallScript[] calls)
        {
            var content = new List<LlmContent>();
            var events = new List<LlmStreamEvent>();
            if (calls != null)
            {
                foreach (ToolCallScript call in calls)
                {
                    if (call == null)
                    {
                        continue;
                    }

                    content.Add(new LlmContent
                    {
                        Type = "toolCall",
                        ToolCallId = call.Id,
                        ToolName = call.Name,
                        ArgumentsJson = call.ArgumentsJson
                    });

                    events.Add(new LlmToolCallDelta
                    {
                        ContentIndex = 1,
                        ToolCallId = call.Id,
                        ToolName = call.Name,
                        ArgumentsDelta = call.ArgumentsJson
                    });
                }
            }

            events.Add(new LlmCompleted
            {
                Response = new LlmResponse
                {
                    Content = content,
                    Usage = new TokenUsage { Input = 10, Output = 5, Total = 15 },
                    StopReason = StopReason.ToolUse,
                    ModelId = "scripted"
                }
            });

            Enqueue(events.ToArray());
        }

        public void EnqueueFailure(AgentError error)
        {
            Enqueue(new LlmStreamEvent[]
            {
                new LlmFailed { Error = error ?? AgentError.Create(AgentErrorKind.Unknown, "脚本失败。") }
            });
        }

        public void Enqueue(params LlmStreamEvent[] events)
        {
            lock (_gate)
            {
                _turns.Enqueue(new ScriptedTurn { Events = events ?? new LlmStreamEvent[0] });
            }
        }

        public ILlmStream Open(
            ModelDescriptor model,
            LlmRequest request,
            ProviderCredential credential,
            CancellationToken cancellationToken)
        {
            ScriptedTurn turn;
            lock (_gate)
            {
                turn = _turns.Count > 0
                    ? _turns.Dequeue()
                    : new ScriptedTurn
                    {
                        Events = new LlmStreamEvent[]
                        {
                            new LlmCompleted
                            {
                                Response = new LlmResponse
                                {
                                    Content = new List<LlmContent>
                                    {
                                        new LlmContent { Type = "text", Text = "(scripted: no turn)" }
                                    },
                                    StopReason = StopReason.Stop
                                }
                            }
                        }
                    };
            }

            return new ScriptedStream(turn.Events);
        }

        public sealed class ToolCallScript
        {
            public string Id { get; set; }

            public string Name { get; set; }

            public string ArgumentsJson { get; set; }
        }

        private sealed class ScriptedTurn
        {
            public LlmStreamEvent[] Events;
        }

        private sealed class ScriptedStream : ILlmStream
        {
            private readonly LlmStreamEvent[] _events;
            private int _index;

            public ScriptedStream(LlmStreamEvent[] events)
            {
                _events = events ?? new LlmStreamEvent[0];
            }

            public Task<LlmStreamEvent> ReadAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_index >= _events.Length)
                {
                    return Task.FromResult<LlmStreamEvent>(null);
                }

                return Task.FromResult(_events[_index++]);
            }

            public void Dispose()
            {
            }
        }
    }
}
