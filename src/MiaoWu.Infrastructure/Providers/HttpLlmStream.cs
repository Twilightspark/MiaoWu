using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Agents;
using MiaoWu.Core.Errors;
using MiaoWu.Core.Models;

namespace MiaoWu.Infrastructure.Providers
{
    /// <summary>把 SSE 帧映射成归一化事件。实现必须累积最终 LlmResponse。</summary>
    public interface ILlmStreamMapper
    {
        System.Collections.Generic.IEnumerable<LlmStreamEvent> Map(SseFrame frame);

        LlmStreamEvent Finish();
    }

    /// <summary>
    /// 一条 HTTP 流：生产者读 SSE → 有界队列 → 消费者（会话 loop）。
    /// 消费者慢会自然反压到网络读取；Dispose 会取消生产者并关闭响应。
    /// </summary>
    public sealed class HttpLlmStream : ILlmStream
    {
        private readonly Func<HttpRequestMessage> _requestFactory;
        private readonly ILlmStreamMapper _mapper;
        private readonly HttpTransport _transport;
        private readonly IAgentErrorClassifier _classifier;
        private readonly AsyncQueue<LlmStreamEvent> _queue = new AsyncQueue<LlmStreamEvent>();
        private readonly CancellationTokenSource _cts;
        private readonly Task _producer;
        private readonly string _providerId;
        private readonly string _modelId;

        private bool _terminalSeen;
        private bool _disposed;

        public HttpLlmStream(
            Func<HttpRequestMessage> requestFactory,
            ILlmStreamMapper mapper,
            HttpTransport transport,
            IAgentErrorClassifier classifier,
            string providerId,
            string modelId,
            CancellationToken cancellationToken)
        {
            _requestFactory = requestFactory ?? throw new ArgumentNullException(nameof(requestFactory));
            _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _classifier = classifier ?? new AgentErrorClassifier();
            _providerId = providerId;
            _modelId = modelId;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _producer = Task.Run((Func<Task>)ProducerAsync);
        }

        public async Task<LlmStreamEvent> ReadAsync(CancellationToken cancellationToken)
        {
            if (_terminalSeen)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();

            LlmStreamEvent evt;
            try
            {
                evt = await _queue.DequeueAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }

            if (evt == null)
            {
                return null;
            }

            if (evt is LlmCompleted || evt is LlmFailed)
            {
                _terminalSeen = true;
            }

            return evt;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                _producer.Wait(500);
            }
            catch (Exception)
            {
            }

            _queue.Complete();

            try
            {
                _cts.Dispose();
            }
            catch (Exception)
            {
            }
        }

        private async Task ProducerAsync()
        {
            HttpResponseMessage response = null;
            try
            {
                HttpRequestMessage request = _requestFactory();
                response = await _transport.SendAsync(request, _cts.Token).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    string body = await ReadLimitedAsync(response).ConfigureAwait(false);
                    string requestId = GetHeader(response, "x-request-id")
                        ?? GetHeader(response, "request-id");

                    AgentError error = AgentErrorClassifier.FromHttp(
                        (int)response.StatusCode,
                        body,
                        requestId);

                    _queue.Enqueue(new LlmFailed { Error = error });
                    return;
                }

                _queue.Enqueue(new LlmStarted
                {
                    Partial = new LlmResponse
                    {
                        ProviderId = _providerId,
                        ModelId = _modelId,
                        StopReason = StopReason.Pending,
                        Content = new System.Collections.Generic.List<LlmContent>()
                    }
                });

                using (Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                {
                    bool stop = false;
                    await SseReader.ReadAsync(
                        stream,
                        frame =>
                        {
                            foreach (LlmStreamEvent evt in _mapper.Map(frame) ?? EmptyEvents)
                            {
                                if (evt is LlmFailed)
                                {
                                    _queue.Enqueue(evt);
                                    stop = true;
                                    return false;
                                }

                                _queue.Enqueue(evt);
                            }

                            return true;
                        },
                        _cts.Token).ConfigureAwait(false);

                    if (stop)
                    {
                        return;
                    }
                }

                LlmStreamEvent terminal = _mapper.Finish();
                if (terminal != null)
                {
                    _queue.Enqueue(terminal);
                }
            }
            catch (OperationCanceledException)
            {
                // 用户取消：给消费者一个干净的结束，由会话 loop 的取消路径收尾。
            }
            catch (Exception exception)
            {
                AgentError error = _classifier.Classify(exception);
                _queue.Enqueue(new LlmFailed { Error = error });
            }
            finally
            {
                if (response != null)
                {
                    try
                    {
                        response.Dispose();
                    }
                    catch (Exception)
                    {
                    }
                }

                _queue.Complete();
            }
        }

        private static readonly LlmStreamEvent[] EmptyEvents = new LlmStreamEvent[0];

        private static async Task<string> ReadLimitedAsync(HttpResponseMessage response)
        {
            try
            {
                using (Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                {
                    var buffer = new byte[8192];
                    int read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    return Encoding.UTF8.GetString(buffer, 0, Math.Max(0, read));
                }
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static string GetHeader(HttpResponseMessage response, string name)
        {
            try
            {
                System.Collections.Generic.IEnumerable<string> values;
                if (response.Headers.TryGetValues(name, out values))
                {
                    foreach (string value in values)
                    {
                        return value;
                    }
                }
            }
            catch (Exception)
            {
            }

            return null;
        }
    }
}
