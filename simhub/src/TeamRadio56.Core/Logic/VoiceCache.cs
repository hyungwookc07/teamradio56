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
    /// 키 규약 (tts.py _cache_path와 반드시 일치):
    ///   edge   : md5("edge|{voice}|{rate}|{pitch}|{text}") .mp3
    ///   kokoro : md5("kokoro|{voice}|{tone}|{text}") .wav
    ///   11labs : md5("11labs|{voice_id}|{tone}|{text}") .mp3
    /// 파일명은 md5 hex의 앞 20자만 쓴다 (hexdigest()[:20]).
    /// 무전 효과 적용본은 같은 이름 + "_rfx{VERSION}.wav" (VERSION=3).
    /// </summary>
    public sealed class VoiceCache
    {
        private const int RadioFxVersion = 3;   // radiofx.py VERSION과 일치

        /// <summary>
        /// 문장 세그먼트 접미사 "_seg{버전}.wav" — 세그먼트 렌더 규약(속도/효과)이
        /// 바뀌면 버전을 올린다. 파이썬 radio_assemble.py SEG_SUFFIX와 일치.
        /// </summary>
        public const string SegSuffix = "_seg1.wav";

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
        private readonly bool _edgeOnly;

        public VoiceCache(string cacheDir, string edgeVoice, int edgeBaseRatePercent,
                          string kokoroVoice = "bm_george", bool radioFx = true,
                          string enginePref = "kokoro")
        {
            _cacheDir = cacheDir;
            _edgeVoice = edgeVoice;
            _edgeBaseRate = edgeBaseRatePercent;
            _kokoroVoice = kokoroVoice;
            _radioFx = radioFx;
            // 파이썬 build_engine과 같은 규칙: edge를 고르면 kokoro 캐시를 안 본다
            _edgeOnly = string.Equals(enginePref, "edge",
                                      StringComparison.OrdinalIgnoreCase);
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

        /// <summary>
        /// 런타임 edge 합성에 필요한 정보 — 결과를 저장할 캐시 경로(mp3)와
        /// 톤이 반영된 전달 파라미터. 파이썬 EdgeTTSEngine과 같은 키라
        /// 여기서 합성해 둔 파일은 파이썬 엔진에서도 캐시 히트한다.
        /// </summary>
        public bool EdgeSynthTarget(string text, string tone,
                                    out string path, out string rate, out string pitch)
        {
            path = rate = pitch = null;
            if (!Available || string.IsNullOrEmpty(text))
                return false;
            KeyValuePair<int, string> delivery;
            if (!ToneDelivery.TryGetValue(tone ?? "casual", out delivery))
                delivery = ToneDelivery["casual"];
            rate = (_edgeBaseRate + delivery.Key)
                .ToString("+0;-0", CultureInfo.InvariantCulture) + "%";
            pitch = delivery.Value;
            path = CachePath(
                "edge|" + _edgeVoice + "|" + rate + "|" + pitch + "|" + text, "mp3");
            return true;
        }

        public string EdgeVoice
        {
            get { return _edgeVoice; }
        }

        /// <summary>
        /// (톤, 텍스트)가 찾게 될 캐시 파일명(원본 기준, 폴더 제외) —
        /// 파이썬 _cache_path와의 파일명 규약 대조(replay-check)용.
        /// </summary>
        public IEnumerable<string> CandidateFileNames(string text, string tone)
        {
            foreach (string basePath in CandidateBases(text, tone ?? "casual"))
                yield return Path.GetFileName(basePath);
        }

        private IEnumerable<string> CandidateBases(string text, string tone)
        {
            // 배포 캐시(kokoro) 우선 — ChainEngine [kokoro, edge]와 같은 순서
            if (!_edgeOnly)
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

        /// <summary>문장 세그먼트 파일명 — 통문장과 같은 kokoro 키에 접미사만 다르다.</summary>
        public static string SegmentFileName(string sentence, string tone,
                                             string voice = "bm_george")
        {
            return Md5Prefix("kokoro|" + voice + "|" + tone + "|" + sentence) + SegSuffix;
        }

        private string CachePath(string key, string ext)
        {
            return Path.Combine(_cacheDir, Md5Prefix(key) + "." + ext);
        }

        private static string Md5Prefix(string key)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(key));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                    sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                // 파이썬 _cache_path는 hexdigest()[:20] — 반드시 20자로 자른다
                return sb.ToString(0, 20);
            }
        }
    }
}
