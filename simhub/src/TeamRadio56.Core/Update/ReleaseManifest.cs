using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace TeamRadio56.Core.Update
{
    public sealed class AudioAsset
    {
        public string Name { get; }
        public string Sha256 { get; }
        /// <summary>바이트 수 — 매니페스트에 audio_size가 없으면 -1.</summary>
        public long Size { get; }

        public AudioAsset(string name, string sha256, long size)
        {
            Name = name;
            Sha256 = sha256;
            Size = size;
        }
    }

    /// <summary>
    /// 릴리스 매니페스트 (teamradio56-release.txt) — UTF-8 key=value 줄, '#' 주석,
    /// 모르는 키는 무시. GitHub API/JSON 없이 releases/latest/download 로 받는다.
    /// </summary>
    public sealed class ReleaseManifest
    {
        public const int SupportedFormat = 1;
        public const string FileName = "teamradio56-release.txt";

        public int Format { get; private set; }
        public string Version { get; private set; }
        public string Tag { get; private set; }
        public string PluginAsset { get; private set; }
        public string PluginSha256 { get; private set; }
        public string AudioPack { get; private set; } = "";
        public string AudioTag { get; private set; } = "";
        public IReadOnlyList<AudioAsset> AudioAssets { get; private set; } = new AudioAsset[0];
        public string Notes { get; private set; } = "";

        public bool HasAudioPack => AudioPack.Length > 0;

        /// <summary>음성 팩 전체 크기 — 하나라도 모르면 -1.</summary>
        public long AudioTotalSize
        {
            get
            {
                long sum = 0;
                foreach (AudioAsset a in AudioAssets)
                {
                    if (a.Size < 0)
                        return -1;
                    sum += a.Size;
                }
                return AudioAssets.Count > 0 ? sum : -1;
            }
        }

        // 태그·자산 이름·팩 ID는 URL 경로와 로컬 파일명이 되므로 안전 문자만 허용
        private static readonly Regex SafeName = new Regex(
            @"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant);

        public static bool IsSafeName(string s) =>
            s != null && SafeName.IsMatch(s) && !s.Contains("..");

        public static ReleaseManifest Parse(string text)
        {
            if (text == null)
                throw Bad("빈 매니페스트");
            var kv = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string raw in text.TrimStart('﻿').Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#')
                    continue;
                int eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;
                kv[line.Substring(0, eq).Trim().ToLowerInvariant()] = line.Substring(eq + 1).Trim();
            }

            var m = new ReleaseManifest();
            string fmt = Required(kv, "format");
            if (!int.TryParse(fmt, NumberStyles.None, CultureInfo.InvariantCulture, out int format)
                || format < 1)
                throw Bad("format 값이 잘못됨: " + fmt);
            if (format > SupportedFormat)
                throw new UpdateException(UpdateErrorKind.UnsupportedFormat,
                    "매니페스트 format=" + format + " — 이 플러그인은 " + SupportedFormat
                    + "까지만 안다. 수동으로 업데이트해야 한다");
            m.Format = format;

            m.Version = Required(kv, "version");
            if (!PluginVersion.IsStrict(m.Version))
                throw Bad("version은 x.y.z 숫자여야 한다: " + m.Version);
            m.Tag = SafeValue(kv, "tag");
            m.PluginAsset = SafeValue(kv, "plugin_asset");
            if (!m.PluginAsset.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                throw Bad("plugin_asset은 .zip이어야 한다: " + m.PluginAsset);
            m.PluginSha256 = Hash(Required(kv, "plugin_sha256"), "plugin_sha256");

            kv.TryGetValue("notes", out string notes);
            m.Notes = notes ?? "";

            kv.TryGetValue("audio_pack", out string pack);
            if (!string.IsNullOrEmpty(pack))
            {
                if (!IsSafeName(pack))
                    throw Bad("audio_pack 이름이 잘못됨: " + pack);
                m.AudioPack = pack;
                m.AudioTag = SafeValue(kv, "audio_tag");
                string[] names = SplitList(Required(kv, "audio_assets"));
                string[] hashes = SplitList(Required(kv, "audio_sha256"));
                if (names.Length == 0)
                    throw Bad("audio_assets가 비었음");
                if (hashes.Length != names.Length)
                    throw Bad("audio_assets(" + names.Length + ")와 audio_sha256("
                              + hashes.Length + ") 개수가 다름");
                string[] sizes = null;
                if (kv.TryGetValue("audio_size", out string sizeList) && sizeList.Length > 0)
                {
                    sizes = SplitList(sizeList);
                    if (sizes.Length != names.Length)
                        throw Bad("audio_size 개수가 audio_assets와 다름");
                }
                var assets = new List<AudioAsset>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < names.Length; i++)
                {
                    if (!IsSafeName(names[i])
                        || !names[i].EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        throw Bad("audio_assets 이름이 잘못됨: " + names[i]);
                    if (!seen.Add(names[i]))
                        throw Bad("audio_assets 중복: " + names[i]);
                    long size = -1;
                    if (sizes != null
                        && (!long.TryParse(sizes[i], NumberStyles.None,
                                           CultureInfo.InvariantCulture, out size) || size <= 0))
                        throw Bad("audio_size 값이 잘못됨: " + sizes[i]);
                    assets.Add(new AudioAsset(names[i], Hash(hashes[i], "audio_sha256"), size));
                }
                m.AudioAssets = assets;
            }
            return m;
        }

        private static string Required(Dictionary<string, string> kv, string key)
        {
            if (!kv.TryGetValue(key, out string v) || v.Length == 0)
                throw Bad("필수 키 없음: " + key);
            return v;
        }

        private static string SafeValue(Dictionary<string, string> kv, string key)
        {
            string v = Required(kv, key);
            if (!IsSafeName(v))
                throw Bad(key + " 값에 허용되지 않는 문자: " + v);
            return v;
        }

        private static string Hash(string v, string key)
        {
            if (!Sha256Util.IsHex64(v))
                throw Bad(key + "는 64자리 16진수여야 한다: " + v);
            return v.ToLowerInvariant();
        }

        private static string[] SplitList(string v)
        {
            var list = new List<string>();
            foreach (string part in v.Split(','))
            {
                string t = part.Trim();
                if (t.Length > 0)
                    list.Add(t);
            }
            return list.ToArray();
        }

        private static UpdateException Bad(string message) =>
            new UpdateException(UpdateErrorKind.BadManifest, message);
    }
}
