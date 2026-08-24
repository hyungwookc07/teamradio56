using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TeamRadio56.Core.Logic
{
    /// <summary>
    /// 파이썬 tts.py의 오디오 캐시 키 규약 포팅 — 사전 생성된 오디오 파일을
    /// 텍스트+톤으로 찾는다. 합성은 하지 않는다 (합성/사전 생성은 파이썬 도구).
    ///
    /// 키 규약 (tts.py와 반드시 일치):
    ///   edge   : md5("edge|{voice}|{rate}|{pitch}|{text}") .mp3
    ///   kokoro : md5("kokoro|{voice}|{tone}|{text}") .wav
    ///   11labs : md5("11labs|{voice_id}|{tone}|{text}") .mp3
    /// 무전 효과 적용본은 같은 이름 + "_rfx{VERSION}.wav" (VERSION=2).
    /// </summary>
    public sealed class VoiceCache
    {
        private const int RadioFxVersion = 2;   // radiofx.py VERSION과 일치

        // EdgeTTSEngine.TONE_DELIVERY와 일치 (기본 속도에 더할 %, 피치)
        private static readonly Dictionary<string, KeyValuePair<int, string>> ToneDelivery =
            new Dictionary<string, KeyValuePair<int, string>>
            {
                { "casual", new KeyValuePair<int, string>(0, "-6Hz") },
                { "urgent", new KeyValuePair<int, string>(14, "+4Hz") },
            };

        private readonly string _cacheDir;
        private readonly string _edgeVoice;
        private readonly int _edgeBaseRate;
        private readonly string _kokoroVoice;
        private readonly bool _radioFx;

        public VoiceCache(string cacheDir, string edgeVoice, int edgeBaseRatePercent,
                          string kokoroVoice = "bm_george", bool radioFx = true)
        {
            _cacheDir = cacheDir;
            _edgeVoice = edgeVoice;
            _edgeBaseRate = edgeBaseRatePercent;
            _kokoroVoice = kokoroVoice;
            _radioFx = radioFx;
        }

        public bool Available
        {
            get { return !string.IsNullOrEmpty(_cacheDir) && Directory.Exists(_cacheDir); }
        }

        /// <summary>
        /// 캐시된 오디오 파일 경로. 무전 효과본(wav)을 우선하고, 없으면 원본.
        /// 아무것도 없으면 null (호출부는 TTS 폴백).
        /// </summary>
        public string Resolve(string text, string tone)
        {
            if (!Available || string.IsNullOrEmpty(text))
                return null;
            foreach (string basePath in CandidateBases(text, tone ?? "casual"))
            {
                if (_radioFx)
                {
                    string rfx = Path.ChangeExtension(basePath, null)
                                 + "_rfx" + RadioFxVersion + ".wav";
                    if (File.Exists(rfx))
                        return rfx;
                }
                if (File.Exists(basePath))
                    return basePath;
            }
            return null;
        }

        private IEnumerable<string> CandidateBases(string text, string tone)
        {
            // 배포 캐시(kokoro) 우선 — ChainEngine [kokoro, edge]와 같은 순서
            yield return CachePath("kokoro|" + _kokoroVoice + "|" + tone + "|" + text, "wav");

            KeyValuePair<int, string> delivery;
            if (!ToneDelivery.TryGetValue(tone, out delivery))
                delivery = ToneDelivery["casual"];
            string rate = (_edgeBaseRate + delivery.Key)
                .ToString("+0;-0", CultureInfo.InvariantCulture) + "%";
            yield return CachePath(
                "edge|" + _edgeVoice + "|" + rate + "|" + delivery.Value + "|" + text,
                "mp3");
        }

        private string CachePath(string key, string ext)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(key));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                    sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return Path.Combine(_cacheDir, sb + "." + ext);
            }
        }
    }
}
