using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Infrastructure.Providers;
using Xunit;

namespace MiaoWu.Tests.Providers
{
    public sealed class SseReaderTests
    {
        [Fact]
        public async Task ReadsMultiLineDataAndStopsAtDone()
        {
            const string payload =
                ": keep-alive\r\n" +
                "event: message\r\n" +
                "data: first\r\n" +
                "data: second\r\n" +
                "\r\n" +
                "data: {\"x\":1}\n\n" +
                "data: [DONE]\n\n" +
                "data: ignored-after-done\n\n";

            var frames = new List<SseFrame>();
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload)))
            {
                await SseReader.ReadAsync(
                    stream,
                    frame =>
                    {
                        frames.Add(frame);
                        return true;
                    },
                    CancellationToken.None);
            }

            Assert.Equal(2, frames.Count);
            Assert.Equal("message", frames[0].Event);
            Assert.Equal("first\nsecond", frames[0].Data);
            Assert.Equal("{\"x\":1}", frames[1].Data);
        }

        [Fact]
        public async Task StopsWhenHandlerReturnsFalse()
        {
            const string payload = "data: one\n\ndata: two\n\n";
            var frames = new List<SseFrame>();

            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload)))
            {
                await SseReader.ReadAsync(
                    stream,
                    frame =>
                    {
                        frames.Add(frame);
                        return false;
                    },
                    CancellationToken.None);
            }

            Assert.Single(frames);
        }

        [Fact]
        public async Task EmitsTrailingFrameWithoutBlankLine()
        {
            const string payload = "data: tail";
            var frames = new List<SseFrame>();

            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload)))
            {
                await SseReader.ReadAsync(
                    stream,
                    frame =>
                    {
                        frames.Add(frame);
                        return true;
                    },
                    CancellationToken.None);
            }

            Assert.Single(frames);
            Assert.Equal("tail", frames[0].Data);
        }
    }
}
