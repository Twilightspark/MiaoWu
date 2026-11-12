using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MiaoWu.Infrastructure.Providers
{
    /// <summary>一条 SSE 帧。data 可能由多行 data: 组成，用 \n 连接。</summary>
    public sealed class SseFrame
    {
        public string Event { get; set; }

        public string Data { get; set; }

        public string Id { get; set; }
    }

    /// <summary>
    /// 最小 SSE 解析器：支持 CRLF/LF、注释行、多行 data、event/id 字段，
    /// 收到 data: [DONE] 时停止。不依赖第三方库。
    /// </summary>
    public static class SseReader
    {
        public static async Task ReadAsync(
            Stream stream,
            Func<SseFrame, bool> onFrame,
            CancellationToken cancellationToken)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            if (onFrame == null)
            {
                throw new ArgumentNullException(nameof(onFrame));
            }

            using (var reader = new StreamReader(
                stream,
                new UTF8Encoding(false),
                false,
                8192,
                true))
            {
                string eventName = null;
                string id = null;
                var data = new StringBuilder();
                bool hasData = false;

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null)
                    {
                        break;
                    }

                    if (line.Length == 0)
                    {
                        if (hasData)
                        {
                            string payload = data.ToString();
                            var frame = new SseFrame
                            {
                                Event = eventName,
                                Data = payload,
                                Id = id
                            };

                            eventName = null;
                            id = null;
                            data.Length = 0;
                            hasData = false;

                            if (string.Equals(payload.Trim(), "[DONE]", StringComparison.Ordinal))
                            {
                                return;
                            }

                            if (!onFrame(frame))
                            {
                                return;
                            }
                        }

                        continue;
                    }

                    if (line[0] == ':')
                    {
                        continue;
                    }

                    int colon = line.IndexOf(':');
                    string field = colon < 0 ? line : line.Substring(0, colon);
                    string value = colon < 0 ? string.Empty : line.Substring(colon + 1);
                    if (value.StartsWith(" ", StringComparison.Ordinal))
                    {
                        value = value.Substring(1);
                    }

                    switch (field)
                    {
                        case "event":
                            eventName = value;
                            break;
                        case "data":
                            if (hasData)
                            {
                                data.Append('\n');
                            }

                            data.Append(value);
                            hasData = true;
                            break;
                        case "id":
                            id = value;
                            break;
                    }
                }

                if (hasData)
                {
                    onFrame(new SseFrame
                    {
                        Event = eventName,
                        Data = data.ToString(),
                        Id = id
                    });
                }
            }
        }
    }
}
