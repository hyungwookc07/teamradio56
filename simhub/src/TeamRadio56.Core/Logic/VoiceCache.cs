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
    /// 문장 세그먼트는 kokoro 키(문장 단위) + SegSuffix — RadioAssembler가 조립.
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
        private volatile int _assetsState;   // 0 미확인, 1 있음, 2 없음

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

        public string CacheDir
        {
            get { return _cacheDir; }
        }

        /// <summary>
        /// 캐시된 오디오 파일 경로 (통문장 → 캐시된 edge mp3 순). 무전 효과본(wav)을
        /// 우선하고, 없으면 원본. 아무것도 없으면 null (호출부는 TTS 폴백).
        /// 문장 세그먼트 조립은 여기 들어 있지 않다 — AudioSink가 두 단계 사이에 끼운다.
        /// </summary>
        public string Resolve(string text, string tone)
        {
            return ResolveWhole(text, tone) ?? ResolveEdgeCached(text, tone);
        }

        /// <summary>kokoro 통문장 (_rfx3 우선, 원본). edge 전용이면 항상 null.</summary>
        public string ResolveWhole(string text, string tone)
        {
            if (_edgeOnly || !Available || string.IsNullOrEmpty(text))
                return null;
            return ExistingVariant(KokoroBase(text, tone ?? "casual"));
        }

        /// <summary>
        /// 재생 1단계 통문장 — 무전 효과가 켜져 있으면 _rfx3만 본다. 효과 전 원본은
        /// 세그먼트 조립 뒤에 ResolveWhole로 (파이썬 KokoroPackEngine → RadioFXEngine 순서).
        /// 효과가 꺼져 있으면 ResolveWhole과 같다.
        /// </summary>
        public string ResolveWholeFx(string text, string tone)
        {
            if (!_radioFx)
                return ResolveWhole(text, tone);
            if (_edgeOnly || !Available || string.IsNullOrEmpty(text))
                return null;
            string rfx = RfxPath(KokoroBase(text, tone ?? "casual"));
            return File.Exists(rfx) ? rfx : null;
        }

        /// <summary>
        /// 이전 미스 때 저장된 edge mp3 (_rfx3 우선). 세그먼트 조립보다 뒤에 봐야
        /// 한다 — 옛 edge 파일이 새로 배포된 세그먼트를 가리면 안 된다.
        /// </summary>
        public string ResolveEdgeCached(string text, string tone)
        {
            if (!Available || string.IsNullOrEmpty(text))
                return null;
            return ExistingVariant(EdgeBase(text, tone ?? "casual"));
        }

        /// <summary>
        /// 문장 세그먼트 경로 — 모든 문장이 있을 때만 (부분 히트는 미스, 한 콜에
        /// 목소리를 섞지 않는다). 미스면 처음 없는 문장을 firstMissing에.
        /// edge 전용/무전 효과 꺼짐/조립 자산 없음이면 조용히 null (firstMissing도 null).
        /// </summary>
        public string[] ResolveSegments(string text, string tone, out string firstMissing)
        {
            firstMissing = null;
            if (_edgeOnly || !_radioFx || !Available || string.IsNullOrEmpty(text)
                || !SegmentAssetsPresent)
                return null;
            List<string> paths = RadioAssembler.SegmentPaths(
                _cacheDir, _kokoroVoice, tone ?? "casual", text, out firstMissing);
            return paths != null ? paths.ToArray() : null;
        }

        /// <summary>조립 자산 4종(_seg1_click/gap_casual/gap_urgent/tail)이 모두 있는지 — 처음 한 번만 확인.</summary>
        public bool SegmentAssetsPresent
        {
            get
            {
                int state = _assetsState;
                if (state == 0)
                {
                    bool all = Available;
                    if (all)
                    {
                        foreach (string name in RadioAssembler.AssetNames)
                        {
                            if (!File.Exists(Path.Combine(_cacheDir, name)))
                            {
                                all = false;
                                break;
                            }
                        }
                    }
                    state = all ? 1 : 2;
                    _assetsState = state;
                }
                return state == 1;
            }
        }

        private string ExistingVariant(string basePath)
        {
            if (_radioFx)
            {
                string rfx = RfxPath(basePath);
                if (File.Exists(rfx))
                    return rfx;
            }
            return File.Exists(basePath) ? basePath : null;
        }

        private static string RfxPath(string basePath)
        {
            return Path.ChangeExtension(basePath, null) + "_rfx" + RadioFxVersion + ".wav";
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
                yield return KokoroBase(text, tone);
            yield return EdgeBase(text, tone);
        }

        private string KokoroBase(string text, string tone)
        {
            return CachePath("kokoro|" + _kokoroVoice + "|" + tone + "|" + text, "wav");
        }

        private string EdgeBase(string text, string tone)
        {
            KeyValuePair<int, string> delivery;
            if (!ToneDelivery.TryGetValue(tone, out delivery))
                delivery = ToneDelivery["casual"];
            string rate = (_edgeBaseRate + delivery.Key)
                .ToString("+0;-0", CultureInfo.InvariantCulture) + "%";
            return CachePath(
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
