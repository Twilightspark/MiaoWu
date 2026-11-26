using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Updates;
using MiaoWu.Infrastructure.Providers;

namespace MiaoWu.Infrastructure.Updates
{
    /// <summary>
    /// 把 GitHub Release 的安装包下载到本地缓存。下载先写 <c>.download</c> 临时文件，
    /// 校验字节数后再改名到最终路径，失败时两个文件都会清掉。
    /// </summary>
    public sealed class GitHubUpdateDownloader : IUpdateDownloader
    {
        private const int TimeoutSeconds = 900;
        private const int BufferSize = 81920;

        private readonly Func<HttpTransport> _transport;
        private readonly string _directory;

        /// <summary><paramref name="directory"/> 通常是 <c>.miaowu\cache\updates</c>。</summary>
        public GitHubUpdateDownloader(HttpTransport transport, string directory)
            : this(() => transport, directory)
        {
        }

        /// <summary>
        /// 每次下载都通过 <paramref name="transport"/> 取当前 transport，代理切换后不会握着
        /// 已释放的旧实例。
        /// </summary>
        public GitHubUpdateDownloader(Func<HttpTransport> transport, string directory)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new ArgumentException("更新缓存目录不能为空。", nameof(directory));
            }

            _directory = directory;
        }

        public async Task<UpdateDownloadResult> DownloadAsync(
            UpdateCheckResult update,
            IProgress<double> progress,
            CancellationToken cancellationToken)
        {
            if (update == null || !update.HasUpdate)
            {
                return Failure("没有可下载的更新。");
            }

            if (string.IsNullOrWhiteSpace(update.AssetUrl))
            {
                return Failure("这次发布没有附带安装包。");
            }

            Uri uri;
            if (!Uri.TryCreate(update.AssetUrl.Trim(), UriKind.Absolute, out uri)
                || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return Failure("安装包地址不是 HTTPS，已拒绝下载。");
            }

            HttpTransport transport = _transport();
            if (transport == null)
            {
                return Failure("网络服务尚未就绪。");
            }

            string fileName = UpdateAssets.SafeInstallerFileName(update.AssetName, update.LatestVersion);
            string finalPath;
            string tempPath;
            try
            {
                Directory.CreateDirectory(_directory);
                finalPath = Path.Combine(_directory, fileName);
                tempPath = finalPath + ".download";
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (Exception exception)
            {
                return Failure("无法准备更新缓存目录：" + exception.Message);
            }

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

                long written = 0;
                try
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Get, uri))
                    using (HttpResponseMessage response = await transport
                        .SendAsync(request, timeout.Token)
                        .ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            return Failure("下载安装包失败（HTTP " + (int)response.StatusCode + "）。");
                        }

                        long total = response.Content.Headers.ContentLength ?? 0;
                        using (Stream source = await response.Content
                            .ReadAsStreamAsync()
                            .ConfigureAwait(false))
                        using (var target = new FileStream(
                            tempPath,
                            FileMode.Create,
                            FileAccess.Write,
                            FileShare.None,
                            BufferSize,
                            true))
                        {
                            var buffer = new byte[BufferSize];
                            while (true)
                            {
                                int read = await source
                                    .ReadAsync(buffer, 0, buffer.Length, timeout.Token)
                                    .ConfigureAwait(false);
                                if (read <= 0)
                                {
                                    break;
                                }

                                await target
                                    .WriteAsync(buffer, 0, read, timeout.Token)
                                    .ConfigureAwait(false);
                                written += read;
                                if (progress != null && total > 0)
                                {
                                    progress.Report(Math.Min(1d, (double)written / total));
                                }
                            }
                        }

                        if (written == 0)
                        {
                            TryDelete(tempPath);
                            return Failure("下载到的安装包是空文件。");
                        }

                        if (update.AssetSize > 0 && written != update.AssetSize)
                        {
                            TryDelete(tempPath);
                            return Failure(
                                "安装包不完整：应有 " + update.AssetSize + " 字节，实际 " + written + " 字节。");
                        }
                    }

                    if (File.Exists(finalPath))
                    {
                        File.Delete(finalPath);
                    }

                    File.Move(tempPath, finalPath);
                    progress?.Report(1d);
                    CleanupOldPackages(finalPath);
                    return new UpdateDownloadResult
                    {
                        Succeeded = true,
                        FilePath = finalPath,
                        Bytes = written,
                        Message = "安装包已下载：" + fileName
                    };
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    TryDelete(tempPath);
                    throw;
                }
                catch (OperationCanceledException)
                {
                    TryDelete(tempPath);
                    return Failure("下载安装包超时，请稍后重试。");
                }
                catch (TimeoutException)
                {
                    TryDelete(tempPath);
                    return Failure("下载安装包超时，请稍后重试。");
                }
                catch (Exception exception)
                {
                    TryDelete(tempPath);
                    return Failure("下载安装包失败：" + exception.Message);
                }
            }
        }

        /// <summary>只保留刚下载的这一个安装包，避免每次升级都在缓存里堆一份 exe。</summary>
        private void CleanupOldPackages(string keepPath)
        {
            try
            {
                foreach (string path in Directory.GetFiles(_directory, "*.exe"))
                {
                    if (!string.Equals(path, keepPath, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(path);
                    }
                }

                foreach (string path in Directory.GetFiles(_directory, "*.download"))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // 缓存清理失败不影响本次更新。
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
            }
        }

        private static UpdateDownloadResult Failure(string message)
        {
            return new UpdateDownloadResult
            {
                Succeeded = false,
                Message = message
            };
        }
    }
}
