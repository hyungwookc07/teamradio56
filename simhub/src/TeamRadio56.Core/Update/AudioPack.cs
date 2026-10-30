using System;
using System.IO;
using System.Text;
using TeamRadio56.Core.Logic;

namespace TeamRadio56.Core.Update
{
    /// <summary>
    /// 설치된 음성 팩 표식 (&lt;캐시폴더&gt;\teamradio56_audio_pack.txt, "pack=&lt;id&gt;").
    /// </summary>
    public static class AudioPack
    {
        public const string MarkerFileName = "teamradio56_audio_pack.txt";
        /// <summary>업데이터 이전에 손으로 설치된 팩 — 표식 없이 조립 자산만 있다.</summary>
        public const string LegacyPackId = "kokoro-v015";
        /// <summary>설치 중 표식 — 중간에 끊기면 어떤 팩 ID와도 달라 다시 업데이트를 권한다.</summary>
        public const string IncompletePrefix = "incomplete:";

        public static string MarkerPath(string cacheDir) => Path.Combine(cacheDir, MarkerFileName);

        /// <summary>설치된 팩 ID. 표식 → 조립 자산 4개 휴리스틱(LegacyPackId) → 없으면 null.</summary>
        public static string Installed(string cacheDir)
        {
            if (string.IsNullOrEmpty(cacheDir) || !Directory.Exists(cacheDir))
                return null;
            string marker = MarkerPath(cacheDir);
            if (File.Exists(marker))
            {
                try
                {
                    string id = ReadMarker(File.ReadAllText(marker, Encoding.UTF8));
                    if (id != null)
                        return id;
                }
                catch (Exception) { }
                return IncompletePrefix + "?";       // 읽을 수 없는 표식 — 재설치 권유
            }
            foreach (string asset in RadioAssembler.AssetNames)
                if (!File.Exists(Path.Combine(cacheDir, asset)))
                    return null;
            return LegacyPackId;
        }

        public static string ReadMarker(string text)
        {
            foreach (string raw in text.TrimStart('﻿').Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#')
                    continue;
                if (line.StartsWith("pack=", StringComparison.Ordinal))
                    line = line.Substring(5).Trim();
                return line.Length > 0 ? line : null;
            }
            return null;
        }

        public static bool NeedsUpdate(ReleaseManifest m, string cacheDir)
        {
            return m != null && m.HasAudioPack
                && !string.Equals(Installed(cacheDir), m.AudioPack, StringComparison.Ordinal);
        }

        public static void WriteMarker(string cacheDir, string packId)
        {
            string path = MarkerPath(cacheDir);
            string tmp = path + ".part";
            File.WriteAllText(tmp, "# teamradio56 음성 팩 — 업데이터가 관리\npack=" + packId + "\n",
                              new UTF8Encoding(false));
            if (File.Exists(path))
                File.Delete(path);
            File.Move(tmp, path);
        }
    }
}
