using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace TeamRadio56.Core.Update
{
    /// <summary>네트워크 추상화 — 셀프테스트는 가짜 구현으로 갈아 끼운다.</summary>
    public interface IDownloader
    {
        /// <summary>작은 텍스트(매니페스트). 실패는 UpdateException.</summary>
        string DownloadText(string url, CancellationToken ct);

        /// <summary>destPath로 받는다 (완료 전엔 .part). progress(받은 바이트, 전체 또는 -1).</summary>
        void DownloadFile(string url, string destPath, Action<long, long> progress,
                          CancellationToken ct);
    }

    /// <summary>
    /// HttpWebRequest 기반 — net48에서 추가 의존 없이 동기·진행률·리다이렉트가 된다.
    /// GitHub 릴리스 자산은 objects.githubusercontent.com 으로 리다이렉트된다.
    /// </summary>
    public sealed class HttpDownloader : IDownloader
    {
        public const int MaxTextBytes = 256 * 1024;

        public string UserAgent { get; set; } = "teamradio56-updater";
        public int TimeoutMs { get; set; } = 30000;
        /// <summary>false면 프록시를 쓰지 않는다 (셀프테스트의 로컬 서버용).</summary>
        public bool UseSystemProxy { get; set; } = true;

        static HttpDownloader()
        {
#if NETFRAMEWORK
            // 구형 기본값(TLS 1.0)으론 GitHub이 접속을 거부한다
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
#endif
        }

        public string DownloadText(string url, CancellationToken ct)
        {
            var ms = new MemoryStream();
            Fetch(url, ms, null, MaxTextBytes, ct);
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        public void DownloadFile(string url, string destPath, Action<long, long> progress,
                                 CancellationToken ct)
        {
            string tmp = destPath + ".part";
            try
            {
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write,
                                               FileShare.None, 1 << 16))
                    Fetch(url, fs, progress, long.MaxValue, ct);
                if (File.Exists(destPath))
                    File.Delete(destPath);
                File.Move(tmp, destPath);
            }
            catch (Exception ex)
            {
                TryDelete(tmp);
                throw UpdateException.From(ex, "다운로드 저장");
            }
        }

#pragma warning disable SYSLIB0014   // net8 타겟은 테스트 전용 — 실행은 net48
        private void Fetch(string url, Stream sink, Action<long, long> progress,
                           long maxBytes, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.UserAgent = UserAgent;
            req.AllowAutoRedirect = true;
            req.MaximumAutomaticRedirections = 10;
            req.Timeout = TimeoutMs;
            req.ReadWriteTimeout = TimeoutMs;
            if (UseSystemProxy)
            {
                // 요청마다 새 인스턴스 — 공유 DefaultWebProxy에 자격 증명을 달면 SimHub 전체
                // 요청이 프록시 407에 Windows 자격 증명으로 응답하게 된다
                IWebProxy proxy = WebRequest.GetSystemWebProxy();
                if (proxy != null)
                    proxy.Credentials = CredentialCache.DefaultCredentials;   // 사내 인증 프록시
                req.Proxy = proxy;
            }
            else
            {
                req.Proxy = null;
            }

            using (ct.Register(() => { try { req.Abort(); } catch (Exception) { } }))
            {
                try
                {
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (Stream body = resp.GetResponseStream())
                    {
                        long total = resp.ContentLength;
                        if (total > maxBytes)
                            throw new UpdateException(UpdateErrorKind.Network,
                                "응답이 너무 큼: " + url);
                        var buf = new byte[1 << 16];
                        long done = 0;
                        progress?.Invoke(0, total);
                        int n;
                        while ((n = body.Read(buf, 0, buf.Length)) > 0)
                        {
                            ct.ThrowIfCancellationRequested();
                            done += n;
                            if (done > maxBytes)
                                throw new UpdateException(UpdateErrorKind.Network,
                                    "응답이 너무 큼: " + url);
                            try
                            {
                                sink.Write(buf, 0, n);
                            }
                            catch (IOException wex)
                            {
                                throw UpdateException.From(wex, "다운로드 저장");   // 디스크 쪽 — 네트워크 아님
                            }
                            progress?.Invoke(done, total);
                        }
                        if (total >= 0 && done != total)
                            throw new UpdateException(UpdateErrorKind.Network,
                                "다운로드가 중간에 끊김 (" + done + "/" + total + " 바이트)");
                    }
                }
                catch (WebException ex)
                {
                    ct.ThrowIfCancellationRequested();
                    throw Classify(ex, url);
                }
                catch (IOException ex)
                {
                    ct.ThrowIfCancellationRequested();
                    throw new UpdateException(UpdateErrorKind.Network,
                        "연결 오류: " + ex.Message, ex);
                }
            }
        }
#pragma warning restore SYSLIB0014

        private static UpdateException Classify(WebException ex, string url)
        {
            if (ex.Response is HttpWebResponse r)
            {
                int code = (int)r.StatusCode;
                r.Dispose();
                if (code == 404)
                    return new UpdateException(UpdateErrorKind.NotFound,
                        "찾을 수 없음 (404): " + url, ex);
                if (code == 407)
                    return new UpdateException(UpdateErrorKind.Network,
                        "프록시 인증 필요 (407)", ex);
                return new UpdateException(UpdateErrorKind.Network,
                    "서버 오류 (HTTP " + code + "): " + url, ex);
            }
            return new UpdateException(UpdateErrorKind.Network,
                "연결 실패 (" + ex.Status + "): " + ex.Message, ex);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception) { }
        }
    }
}
