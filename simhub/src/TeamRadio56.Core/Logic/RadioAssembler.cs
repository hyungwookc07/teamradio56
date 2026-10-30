using System;
using System.Collections.Generic;
using System.IO;

namespace TeamRadio56.Core.Logic
{
    /// <summary>
    /// 문장 세그먼트 조립 — 파이썬 radio_assemble.py 포팅. 두 쪽 출력은
    /// 바이트 단위로 같아야 한다 (replay-check의 assemble_check cmp 게이트).
    ///
    /// 출력: 8 kHz 모노 PCM16 WAV (44바이트 헤더)
    ///   click + seg1 + gap(tone) + seg2 + ... + gap(tone) + segN + tail
    /// 런타임에는 크로스페이드·연산이 없다 — 페이드는 사전 생성 때 이미 들어 있다.
    /// 출력은 항상 PCM16 — SoundPlayer는 μ-law(태그 7) 헤더를 거부한다.
    ///
    /// 입력 클립 규약: RIFF/WAVE, 모노 8000 Hz, PCM16(태그 1) 또는 μ-law(태그 7, 8비트).
    /// fmt 크기는 16 또는 18, 알 수 없는 청크(fact, LIST 등)는 건너뛴다.
    /// 그 밖의 형식이나 파싱 실패는 null — 호출부는 캐시 미스로 취급한다.
    /// </summary>
    public static class RadioAssembler
    {
        public const int SampleRate = 8000;

        private static readonly short[] UlawTable = BuildUlawTable();
        private static readonly object AssetLock = new object();
        private static readonly Dictionary<string, short[]> AssetCache =
            new Dictionary<string, short[]>(StringComparer.Ordinal);

        /// <summary>조립 자산 파일명 — click / tail / gap_casual / gap_urgent.</summary>
        public static string AssetName(string kind)
        {
            // SegSuffix "_seg1.wav" → "_seg1_" + kind + ".wav"
            return VoiceCache.SegSuffix.Substring(0, VoiceCache.SegSuffix.Length - 4)
                   + "_" + kind + ".wav";
        }

        public static readonly string[] AssetNames =
        {
            AssetName("click"), AssetName("gap_casual"), AssetName("gap_urgent"), AssetName("tail"),
        };

        /// <summary>
        /// 문장마다 세그먼트 경로 — 하나라도 없으면 null (부분 조립은 하지 않는다).
        /// 파이썬 radio_assemble.segment_paths와 같은 규칙.
        /// </summary>
        public static List<string> SegmentPaths(string cacheDir, string voice,
                                                string tone, string text)
        {
            string firstMissing;
            return SegmentPaths(cacheDir, voice, tone, text, out firstMissing);
        }

        /// <summary>같은 규칙 — 미스면 처음 없는 문장을 firstMissing으로 알려준다 (미스 로그용).</summary>
        public static List<string> SegmentPaths(string cacheDir, string voice,
                                                string tone, string text,
                                                out string firstMissing,
                                                Func<string, bool> exists = null)
        {
            if (exists == null)
                exists = File.Exists;
            firstMissing = null;
            if (string.IsNullOrEmpty(cacheDir) || text == null)
                return null;
            List<string> sents = Messages.SplitSentences(text);
            if (sents.Count == 0)
                return null;
            var paths = new List<string>(sents.Count);
            foreach (string s in sents)
            {
                string p = Path.Combine(cacheDir, VoiceCache.SegmentFileName(s, tone, voice));
                if (!exists(p))
                {
                    firstMissing = s;
                    return null;
                }
                paths.Add(p);
            }
            return paths;
        }

        /// <summary>세그먼트들을 무전 한 번(PCM16 WAV 바이트)으로 조립. 실패하면 null.</summary>
        public static byte[] Build(string cacheDir, string tone, IList<string> segPaths)
        {
            if (segPaths == null || segPaths.Count == 0)
                return null;
            short[] click = Asset(Path.Combine(cacheDir, AssetName("click")));
            short[] gap = Asset(Path.Combine(cacheDir, AssetName("gap_" + tone)));
            short[] tail = Asset(Path.Combine(cacheDir, AssetName("tail")));
            if (click == null || gap == null || tail == null)
                return null;

            var segs = new short[segPaths.Count][];
            long n = click.Length + tail.Length + (long)gap.Length * (segPaths.Count - 1);
            for (int i = 0; i < segPaths.Count; i++)
            {
                segs[i] = ReadSamples(segPaths[i]);
                if (segs[i] == null)
                    return null;
                n += segs[i].Length;
            }

            byte[] wav = new byte[44 + 2 * n];
            WriteHeader(wav, (int)n);
            int pos = 44;
            pos = Put(wav, pos, click);
            for (int i = 0; i < segs.Length; i++)
            {
                if (i > 0)
                    pos = Put(wav, pos, gap);
                pos = Put(wav, pos, segs[i]);
            }
            Put(wav, pos, tail);
            return wav;
        }

        /// <summary>WAV → int16 샘플. 규약 밖이거나 깨진 파일이면 null.</summary>
        internal static short[] ReadSamples(string path)
        {
            byte[] b;
            try
            {
                b = File.ReadAllBytes(path);
            }
            catch (Exception)
            {
                return null;
            }
            if (b.Length < 12 || !Tag(b, 0, "RIFF") || !Tag(b, 8, "WAVE"))
                return null;
            int fmtAt = -1, fmtLen = 0, dataAt = -1, dataLen = 0;
            long pos = 12;
            while (pos + 8 <= b.Length)
            {
                long size = U32(b, (int)pos + 4);
                long start = pos + 8;
                long end = start + size;
                if (end > b.Length)
                    return null;
                if (fmtAt < 0 && Tag(b, (int)pos, "fmt "))
                {
                    fmtAt = (int)start;
                    fmtLen = (int)size;
                }
                else if (dataAt < 0 && Tag(b, (int)pos, "data"))
                {
                    dataAt = (int)start;
                    dataLen = (int)size;
                }
                pos = end + (size & 1);     // 홀수 크기 청크는 패딩 1바이트
            }
            if (fmtAt < 0 || dataAt < 0 || (fmtLen != 16 && fmtLen != 18))
                return null;
            int formatTag = U16(b, fmtAt);
            int channels = U16(b, fmtAt + 2);
            long rate = U32(b, fmtAt + 4);
            int bits = U16(b, fmtAt + 14);
            if (channels != 1 || rate != SampleRate)
                return null;
            if (formatTag == 1 && bits == 16)
            {
                var s = new short[dataLen / 2];
                for (int i = 0; i < s.Length; i++)
                    s[i] = (short)(b[dataAt + 2 * i] | (b[dataAt + 2 * i + 1] << 8));
                return s;
            }
            if (formatTag == 7 && bits == 8)
            {
                var s = new short[dataLen];
                for (int i = 0; i < s.Length; i++)
                    s[i] = UlawTable[b[dataAt + i]];
                return s;
            }
            return null;
        }

        private static short[] Asset(string path)
        {
            lock (AssetLock)
            {
                short[] s;
                if (AssetCache.TryGetValue(path, out s))
                    return s;
                s = ReadSamples(path);
                if (s != null)
                    AssetCache[path] = s;
                return s;
            }
        }

        private static short[] BuildUlawTable()
        {
            // G.711 표준 복호 — libsndfile과 같은 값 (tools/assemble_check.py가 대조)
            var table = new short[256];
            for (int i = 0; i < 256; i++)
            {
                int u = ~i & 0xFF;
                int t = ((u & 0x0F) << 3) + 0x84;
                t <<= (u & 0x70) >> 4;
                table[i] = (short)((u & 0x80) != 0 ? 0x84 - t : t - 0x84);
            }
            return table;
        }

        private static void WriteHeader(byte[] w, int n)
        {
            PutTag(w, 0, "RIFF");
            PutU32(w, 4, 36 + 2 * (uint)n);
            PutTag(w, 8, "WAVE");
            PutTag(w, 12, "fmt ");
            PutU32(w, 16, 16);
            PutU16(w, 20, 1);                 // PCM
            PutU16(w, 22, 1);                 // 모노
            PutU32(w, 24, SampleRate);
            PutU32(w, 28, SampleRate * 2);
            PutU16(w, 32, 2);
            PutU16(w, 34, 16);
            PutTag(w, 36, "data");
            PutU32(w, 40, 2 * (uint)n);
        }

        private static int Put(byte[] w, int pos, short[] s)
        {
            foreach (short v in s)
            {
                w[pos++] = (byte)(v & 0xFF);
                w[pos++] = (byte)((v >> 8) & 0xFF);
            }
            return pos;
        }

        private static bool Tag(byte[] b, int at, string tag)
        {
            for (int i = 0; i < 4; i++)
            {
                if (b[at + i] != (byte)tag[i])
                    return false;
            }
            return true;
        }

        private static void PutTag(byte[] w, int at, string tag)
        {
            for (int i = 0; i < 4; i++)
                w[at + i] = (byte)tag[i];
        }

        private static int U16(byte[] b, int at)
        {
            return b[at] | (b[at + 1] << 8);
        }

        private static long U32(byte[] b, int at)
        {
            return (uint)(b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24));
        }

        private static void PutU16(byte[] w, int at, int v)
        {
            w[at] = (byte)(v & 0xFF);
            w[at + 1] = (byte)((v >> 8) & 0xFF);
        }

        private static void PutU32(byte[] w, int at, uint v)
        {
            w[at] = (byte)(v & 0xFF);
            w[at + 1] = (byte)((v >> 8) & 0xFF);
            w[at + 2] = (byte)((v >> 16) & 0xFF);
            w[at + 3] = (byte)((v >> 24) & 0xFF);
        }
    }
}
