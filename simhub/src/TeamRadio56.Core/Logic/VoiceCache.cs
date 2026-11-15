using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TeamRadio56.Core.Logic
{
    /// <summary>
    /// kokoro 음성 팩 파일 찾기 — 파이썬 tts.py의 오디오 캐시 키 규약 포팅.
    /// 사전 생성된 오디오 파일을 텍스트+톤으로 찾는다. 합성은 하지 않는다
    /// (합성/사전 생성은 파이썬 도구). 찾는 순서는 VoiceRoute.
    ///
    /// 키 규약 (tts.py _cache_path와 반드시 일치):
    ///   kokoro : md5("kokoro|{voice}|{tone}|{text}") .wav
    /// 파일명은 md5 hex의 앞 20자만 쓴다 (hexdigest()[:20]).
    /// 무전 효과 적용본은 같은 이름 + "_rfx{VERSION}.wav" (VERSION=3) — 음성 팩은
    /// 효과본과 세그먼트만 담으므로 효과는 항상 켠다.
    /// 문장 세그먼트는 kokoro 키(문장 단위) + SegSuffix — RadioAssembler가 조립.
    /// </summary>
    public sealed class VoiceCache
    {
        public const int RadioFxVersion = 3;    // radiofx.py VERSION과 일치

        /// <summary>
        /// 문장 세그먼트 접미사 "_seg{버전}.wav" — 세그먼트 렌더 규약(속도/효과)이
        /// 바뀌면 버전을 올린다. 파이썬 radio_assemble.py SEG_SUFFIX와 일치.
        /// </summary>
        public const string SegSuffix = "_seg1.wav";

        public const string DefaultVoice = "bm_george";

        private readonly string _cacheDir;
        private readonly string _kokoroVoice;
        private volatile int _assetsState;   // 0 미확인, 1 있음, 2 없음
        private readonly Func<string, bool> _exists;
        private readonly bool _virtual;

        public VoiceCache(string cacheDir, string kokoroVoice = DefaultVoice,
                          Func<string, bool> fileExists = null)
        {
            // fileExists: 가상 팩 대조 (커버리지 퍼즈) — 주면 폴더 없이도 Available
            _exists = fileExists ?? File.Exists;
            _virtual = fileExists != null;
            _cacheDir = cacheDir;
            _kokoroVoice = kokoroVoice ?? DefaultVoice;
        }

        public bool Available
        {
            get { return _virtual || (!string.IsNullOrEmpty(_cacheDir) && Directory.Exists(_cacheDir)); }
        }

        public string CacheDir
        {
            get { return _cacheDir; }
        }

        /// <summary>kokoro 통문장 (_rfx3 우선, 원본).</summary>
        public string ResolveWhole(string text, string tone)
        {
            if (!Available || string.IsNullOrEmpty(text))
                return null;
            string basePath = KokoroBase(text, tone ?? "casual");
            string rfx = RfxPath(basePath);
            if (_exists(rfx))
                return rfx;
            return _exists(basePath) ? basePath : null;
        }

        /// <summary>
        /// 재생 1단계 통문장 — _rfx3만 본다. 효과 전 원본은 세그먼트 조립 뒤에
        /// ResolveWhole로 (파이썬 KokoroPackEngine → RadioFXEngine 순서).
        /// </summary>
        public string ResolveWholeFx(string text, string tone)
        {
            if (!Available || string.IsNullOrEmpty(text))
                return null;
            string rfx = RfxPath(KokoroBase(text, tone ?? "casual"));
            return _exists(rfx) ? rfx : null;
        }

        /// <summary>
        /// 문장 세그먼트 경로 — 모든 문장이 있을 때만 (부분 히트는 미스, 한 콜에
        /// 목소리를 섞지 않는다). 미스면 처음 없는 문장을 firstMissing에.
        /// 조립 자산이 없으면 조용히 null (firstMissing도 null).
        /// </summary>
        public string[] ResolveSegments(string text, string tone, out string firstMissing)
        {
            firstMissing = null;
            if (!Available || string.IsNullOrEmpty(text) || !SegmentAssetsPresent)
                return null;
            List<string> paths = RadioAssembler.SegmentPaths(
                _cacheDir, _kokoroVoice, tone ?? "casual", text, out firstMissing, _exists);
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
                            if (!_exists(Path.Combine(_cacheDir, name)))
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

        private static string RfxPath(string basePath)
        {
            return Path.ChangeExtension(basePath, null) + "_rfx" + RadioFxVersion + ".wav";
        }

        private string KokoroBase(string text, string tone)
        {
            return Path.Combine(_cacheDir ?? "", WholeFileName(text, tone, _kokoroVoice));
        }

        /// <summary>
        /// 통문장 원본 파일명 (폴더 제외, 효과본은 확장자 앞에 "_rfx3") — 파이썬
        /// _cache_path와의 파일명 규약 대조(replay-check --dump-cache-names)용.
        /// </summary>
        public static string WholeFileName(string text, string tone, string voice = DefaultVoice)
        {
            return Md5Prefix("kokoro|" + voice + "|" + (tone ?? "casual") + "|" + text) + ".wav";
        }

        /// <summary>통문장 효과본 파일명 — 음성 팩이 담는 그 이름.</summary>
        public static string WholeFxFileName(string text, string tone, string voice = DefaultVoice)
        {
            return RfxPath(WholeFileName(text, tone, voice));
        }

        /// <summary>문장 세그먼트 파일명 — 통문장과 같은 kokoro 키에 접미사만 다르다.</summary>
        public static string SegmentFileName(string sentence, string tone,
                                             string voice = DefaultVoice)
        {
            return Md5Prefix("kokoro|" + voice + "|" + tone + "|" + sentence) + SegSuffix;
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
