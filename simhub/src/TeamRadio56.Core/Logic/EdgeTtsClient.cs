using System;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TeamRadio56.Core.Logic
{
    /// <summary>
    /// edge-tts(파이썬)의 최소 포팅 — Microsoft Edge "소리내어 읽기" 서비스로
    /// 텍스트를 mp3로 합성한다. builtin 모드에서 사전 생성 캐시에 없는 문구
    /// (동적 숫자 조합 등)의 폴백. 파이썬 엔진의 EdgeTTSEngine과 같은 서비스라
    /// 목소리도 같고, 결과를 같은 캐시 키에 저장하므로 서로 재사용된다.
    ///
    /// 프로토콜 (edge-tts 7.x 기준):
    ///   wss://speech.platform.bing.com/.../edge/v1
    ///     ?TrustedClientToken=...&ConnectionId=...&Sec-MS-GEC=...&Sec-MS-GEC-Version=...
    ///   → speech.config(JSON) 전송 → ssml 전송 → Path:audio 바이너리 조각 수신
    ///   → Path:turn.end 텍스트로 종료.
    /// Sec-MS-GEC = SHA256(윈도우 파일타임 100ns 틱을 5분 단위로 내림 + 토큰).
    /// </summary>
    public static class EdgeTtsClient
    {
        private const string TrustedClientToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
        private const string ChromiumFullVersion = "143.0.3650.75";
        private const string ChromiumMajor = "143";

        private const string WssBase =
            "wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1"
            + "?TrustedClientToken=" + TrustedClientToken;

        /// <summary>마지막 실패 사유 (진단 로그용). 성공 시 null.</summary>
        public static string LastError;

        /// <summary>
        /// 동기 합성 — 보이스 워커 스레드에서 부른다. 성공하면 outPath에 mp3를
        /// 쓰고 true. 실패(오프라인/차단/타임아웃)는 false — 호출부가 폴백.
        /// </summary>
        public static bool Synthesize(string text, string voice, string rate,
                                      string pitch, string outPath,
                                      int timeoutMs = 10000)
        {
            try
            {
                return SynthesizeAsync(text, voice, rate, pitch, outPath, timeoutMs)
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                LastError = ex.GetBaseException().Message;
                return false;
            }
        }

        private static async Task<bool> SynthesizeAsync(
            string text, string voice, string rate, string pitch,
            string outPath, int timeoutMs)
        {
#if NETFRAMEWORK
            // 구형 기본값(TLS 1.0)로는 접속이 거부된다
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
#endif
            using (var cts = new CancellationTokenSource(timeoutMs))
            using (var ws = new ClientWebSocket())
            {
                SetHeaders(ws.Options);
                string url = WssBase
                    + "&ConnectionId=" + Guid.NewGuid().ToString("N")
                    + "&Sec-MS-GEC=" + GenerateSecMsGec()
                    + "&Sec-MS-GEC-Version=1-" + ChromiumFullVersion;
                await ws.ConnectAsync(new Uri(url), cts.Token).ConfigureAwait(false);

                await SendText(ws,
                    "X-Timestamp:" + JsDate() + "\r\n"
                    + "Content-Type:application/json; charset=utf-8\r\n"
                    + "Path:speech.config\r\n\r\n"
                    + "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{"
                    + "\"sentenceBoundaryEnabled\":\"true\",\"wordBoundaryEnabled\":\"false\""
                    + "},\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}\r\n",
                    cts.Token).ConfigureAwait(false);

                string ssml =
                    "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis'"
                    + " xml:lang='en-US'>"
                    + "<voice name='" + voice + "'>"
                    + "<prosody pitch='" + pitch + "' rate='" + rate + "' volume='+0%'>"
                    + EscapeXml(text)
                    + "</prosody></voice></speak>";
                await SendText(ws,
                    "X-RequestId:" + Guid.NewGuid().ToString("N") + "\r\n"
                    + "Content-Type:application/ssml+xml\r\n"
                    + "X-Timestamp:" + JsDate() + "Z\r\n"   // Z 중복은 서비스 관례
                    + "Path:ssml\r\n\r\n" + ssml,
                    cts.Token).ConfigureAwait(false);

                var audio = new MemoryStream();
                var buffer = new byte[16 * 1024];
                var message = new MemoryStream();
                while (true)
                {
                    message.SetLength(0);
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(
                            new ArraySegment<byte>(buffer), cts.Token).ConfigureAwait(false);
                        message.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    byte[] data = message.ToArray();
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        string headers = Encoding.UTF8.GetString(data);
                        if (headers.Contains("Path:turn.end"))
                            break;
                        continue;
                    }
                    // 바이너리: [2바이트 BE 헤더 길이][헤더][오디오]
                    if (data.Length < 2)
                        continue;
                    int headerLen = (data[0] << 8) | data[1];
                    if (data.Length < 2 + headerLen)
                        continue;
                    string binHeaders = Encoding.UTF8.GetString(data, 2, headerLen);
                    if (binHeaders.Contains("Path:audio"))
                        audio.Write(data, 2 + headerLen, data.Length - 2 - headerLen);
                }

                if (audio.Length == 0)
                {
                    LastError = "서비스가 오디오를 보내지 않음";
                    return false;
                }
                string dir = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                // 절반만 쓰인 파일이 캐시로 남지 않게 임시 파일 → 교체
                string tmp = outPath + ".part";
                File.WriteAllBytes(tmp, audio.ToArray());
                if (File.Exists(outPath))
                    File.Delete(outPath);
                File.Move(tmp, outPath);
                LastError = null;
                return true;
            }
        }

        private static void SetHeaders(ClientWebSocketOptions options)
        {
            // 일부 런타임은 특정 헤더 설정을 거부한다 — 되는 것만 싣는다
            TrySet(options, "Pragma", "no-cache");
            TrySet(options, "Cache-Control", "no-cache");
            TrySet(options, "Origin",
                "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold");
            TrySet(options, "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36"
                + " (KHTML, like Gecko) Chrome/" + ChromiumMajor + ".0.0.0"
                + " Safari/537.36 Edg/" + ChromiumMajor + ".0.0.0");
            TrySet(options, "Accept-Language", "en-US,en;q=0.9");
            TrySet(options, "Cookie", "muid=" + RandomHex(32).ToUpperInvariant() + ";");
            try
            {
                options.Proxy = WebRequest.DefaultWebProxy;
            }
            catch (Exception) { }
        }

        private static void TrySet(ClientWebSocketOptions options, string name, string value)
        {
            try
            {
                options.SetRequestHeader(name, value);
            }
            catch (Exception) { }
        }

        private static Task SendText(ClientWebSocket ws, string text, CancellationToken ct)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            return ws.SendAsync(new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text, true, ct);
        }

        /// <summary>edge_tts.drm.generate_sec_ms_gec 미러 (대조 테스트용 public).</summary>
        public static string GenerateSecMsGec()
        {
            // 유닉스 초 → 윈도우 파일타임 epoch(1601) 초 → 5분 내림 → 100ns 틱
            double seconds = (DateTime.UtcNow
                - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds
                + 11644473600.0;
            seconds -= seconds % 300.0;
            double ticks = seconds * 1e7;
            string input = ticks.ToString("F0",
                System.Globalization.CultureInfo.InvariantCulture) + TrustedClientToken;
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.ASCII.GetBytes(input));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                    sb.Append(b.ToString("X2",
                        System.Globalization.CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        private static string JsDate()
        {
            return DateTime.UtcNow.ToString(
                "ddd MMM dd yyyy HH:mm:ss 'GMT+0000 (Coordinated Universal Time)'",
                System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string EscapeXml(string text)
        {
            return text.Replace("&", "&amp;").Replace("<", "&lt;")
                       .Replace(">", "&gt;").Replace("'", "&apos;")
                       .Replace("\"", "&quot;");
        }

        private static string RandomHex(int chars)
        {
            var bytes = new byte[chars / 2];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            var sb = new StringBuilder(chars);
            foreach (byte b in bytes)
                sb.Append(b.ToString("x2",
                    System.Globalization.CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }
}
