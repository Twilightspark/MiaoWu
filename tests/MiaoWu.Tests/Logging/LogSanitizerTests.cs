using System;
using System.IO;
using System.Text;
using MiaoWu.Infrastructure.Logging;
using Xunit;

namespace MiaoWu.Tests.Logging
{
    public sealed class LogSanitizerTests
    {
        [Fact]
        public void RedactsCommonSecrets()
        {
            const string text =
                "Authorization: Bearer abc.def.ghi\n" +
                "api_key=sk-1234567890abcdef\n" +
                "url=https://user:pass@example.com/path\n" +
                "aws=AKIAABCDEFGHIJKLMNOP\n" +
                "password: hunter2\n";

            string sanitized = LogSanitizer.Sanitize(text);

            Assert.DoesNotContain("abc.def.ghi", sanitized);
            Assert.DoesNotContain("sk-1234567890abcdef", sanitized);
            Assert.DoesNotContain("user:pass@", sanitized);
            Assert.DoesNotContain("AKIAABCDEFGHIJKLMNOP", sanitized);
            Assert.DoesNotContain("hunter2", sanitized);
            Assert.Contains("[redacted]", sanitized);
        }

        [Fact]
        public void RedactsPrivateKeys()
        {
            const string text =
                "-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA\n-----END RSA PRIVATE KEY-----";

            string sanitized = LogSanitizer.Sanitize(text);
            Assert.DoesNotContain("MIIEowIBAAKCAQEA", sanitized);
            Assert.Contains("[private-key]", sanitized);
        }

        [Fact]
        public void ShortenKeepsLimitAndSanitizes()
        {
            string sanitized = LogSanitizer.Shorten("token=sk-1234567890abcdef", 10);
            Assert.DoesNotContain("sk-1234567890abcdef", sanitized);
            Assert.True(sanitized.Length <= 11);
        }

        [Fact]
        public void RollingLoggerWritesSanitizedLines()
        {
            string directory = Path.Combine(Path.GetTempPath(), "miaowu-log-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var logger = new RollingFileLogger(directory))
                {
                    logger.Info("test.event", "hello");
                    logger.Error("test.error", "key sk-abcdefgh12345678", new InvalidOperationException("boom"));
                }

                string[] files = Directory.GetFiles(directory, "app-*.log");
                Assert.Single(files);
                string text = File.ReadAllText(files[0]);
                Assert.Contains("test.event", text);
                Assert.Contains("hello", text);
                Assert.DoesNotContain("sk-abcdefgh12345678", text);
            }
            finally
            {
                try
                {
                    Directory.Delete(directory, true);
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
