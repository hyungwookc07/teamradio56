using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using TeamRadio56.Core.Update;

namespace TeamRadio56.Replay
{
    /// <summary>
    /// --release-check: 릴리스 도구(release.ps1)가 만드는 것을 Core 업데이터 기준으로 검증.
    ///
    ///   --release-check voice_lines/audio_pack.txt [--plugin-source TeamRadio56Plugin.cs]
    ///                   [--manifest dist/release/x.y.z/teamradio56-release.txt] [--audio-dir 팩zip폴더]
    ///
    /// 기술자만 주면 release.ps1과 같은 모양의 매니페스트를 만들어 ReleaseManifest.Parse로 읽는다.
    /// --manifest를 주면 스크립트가 실제로 만든 폴더를 릴리스 서버처럼 내놓고, 사용자 PC의
    /// 업데이트 흐름(확인 → 플러그인 교체 → 음성 팩 설치, zip이 폴더에 있을 때)을 그대로 돌린다.
    /// --audio-dir은 audio_tag 릴리스(audio-&lt;pack&gt;)의 자산 폴더 — 팩 설치를 그 폴더에서 받는다.
    /// </summary>
    internal static class ReleaseCheck
    {
        private const string Base = "https://release.invalid/releases";
        private static int _pass;
        private static readonly List<string> Failures = new List<string>();

        private sealed class PackDescriptor
        {
            public string Pack = "";
            public string Tag = "";
            public readonly List<AudioAsset> Assets = new List<AudioAsset>();
        }

        public static int Run(string packPath, string pluginSource, string manifestPath, string audioDir = null)
        {
            string fullVersion = null;
            PackDescriptor pack = null;
            try
            {
                pack = ParseDescriptor(File.ReadAllText(packPath, Encoding.UTF8));
                _pass++;
                fullVersion = ReadPluginVersion(pluginSource);
                _pass++;
            }
            catch (Exception ex)
            {
                Failures.Add(ex.Message);
            }

            if (pack != null && fullVersion != null)
            {
                Console.WriteLine($"  플러그인 {fullVersion} · 음성 팩 {pack.Pack} (zip {pack.Assets.Count}개, "
                                  + $"{pack.Assets.Sum(a => a.Size) / 1048576.0:F1} MB, 자산 위치 {pack.Tag})");
                Section("기술자 → 매니페스트 형식", () => Synthesized(pack, fullVersion));
                Section("기술자 규칙 위반 거부", RejectsBadDescriptors);
                if (manifestPath != null)
                    Section("release.ps1 출력 → 사용자 업데이트 흐름",
                            () => Produced(pack, fullVersion, manifestPath, audioDir));
            }

            if (Failures.Count > 0)
            {
                Console.WriteLine($"❌ 릴리스 도구 검증 실패 {Failures.Count}건 (통과 {_pass}건)");
                foreach (string f in Failures)
                    Console.WriteLine("   - " + f);
                return 1;
            }
            Console.WriteLine($"✅ 릴리스 도구 검증 {_pass}건 통과");
            return 0;
        }

        // release.ps1의 기술자 해석과 같은 규칙 — 어긋나면 스크립트가 먼저 거부하므로
        // 여기서는 커밋된 기술자가 그 규칙과 Core 파서를 둘 다 통과하는지 본다.
        private static PackDescriptor ParseDescriptor(string text)
        {
            var d = new PackDescriptor();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string raw in text.TrimStart('\uFEFF').Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#')
                    continue;
                int eq = line.IndexOf('=');
                if (eq <= 0)
                    throw new FormatException("audio_pack.txt 줄 형식이 잘못됨: " + line);
                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();
                switch (key)
                {
                    case "pack":
                        d.Pack = val;
                        break;
                    case "tag":
                        d.Tag = val;
                        break;
                    case "asset":
                        string[] parts = val.Split(',');
                        if (parts.Length != 3)
                            throw new FormatException("asset 줄은 이름,sha256,크기: " + line);
                        string name = parts[0].Trim();
                        string sha = parts[1].Trim().ToLowerInvariant();
                        if (!ReleaseManifest.IsSafeName(name)
                            || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                            throw new FormatException("asset 이름이 잘못됨: " + name);
                        if (!Regex.IsMatch(sha, "^[0-9a-f]{64}$"))
                            throw new FormatException("asset sha256이 잘못됨: " + name);
                        if (!long.TryParse(parts[2].Trim(), NumberStyles.None, CultureInfo.InvariantCulture,
                                           out long size) || size <= 0)
                            throw new FormatException("asset 크기가 잘못됨: " + name);
                        if (!seen.Add(name))
                            throw new FormatException("asset 이름 중복: " + name);
                        d.Assets.Add(new AudioAsset(name, sha, size));
                        break;
                    default:
                        throw new FormatException("audio_pack.txt에 모르는 키: " + key);
                }
            }
            if (!ReleaseManifest.IsSafeName(d.Pack))
                throw new FormatException("pack이 비었거나 잘못됨: '" + d.Pack + "'");
            if (!ReleaseManifest.IsSafeName(d.Tag))
                throw new FormatException("tag가 비었거나 잘못됨: '" + d.Tag + "'");
            if (d.Assets.Count == 0)
                throw new FormatException("asset 줄이 없음");
            return d;
        }

        private static string ReadPluginVersion(string path)
        {
            Match m = Regex.Match(File.ReadAllText(path, Encoding.UTF8),
                                  "public\\s+const\\s+string\\s+Version\\s*=\\s*\"([^\"]+)\"");
            if (!m.Success)
                throw new FormatException("Version 상수를 찾지 못함: " + path);
            if (PluginVersion.ParseNumeric(m.Groups[1].Value) == null)
                throw new FormatException("Version이 x.y.z로 시작하지 않음: " + m.Groups[1].Value);
            return m.Groups[1].Value;
        }

        private static string NumericVersion(string full) =>
            Regex.Match(full, @"^\d+\.\d+\.\d+").Value;

        /// <summary>release.ps1 8단계와 같은 줄 구성.</summary>
        private static string ManifestText(PackDescriptor p, string fullVersion, string pluginSha, string notes)
        {
            string v = NumericVersion(fullVersion);
            var sb = new StringBuilder();
            sb.Append("# teamradio56 릴리스 매니페스트 — release.ps1이 생성 (" + fullVersion + ")\n");
            sb.Append("format=1\n");
            sb.Append("version=" + v + "\n");
            sb.Append("tag=v" + v + "\n");
            sb.Append("plugin_asset=teamradio56-plugin-" + v + ".zip\n");
            sb.Append("plugin_sha256=" + pluginSha + "\n");
            sb.Append("audio_pack=" + p.Pack + "\n");
            sb.Append("audio_tag=" + p.Tag + "\n");
            sb.Append("audio_assets=" + string.Join(",", p.Assets.Select(a => a.Name)) + "\n");
            sb.Append("audio_sha256=" + string.Join(",", p.Assets.Select(a => a.Sha256)) + "\n");
            sb.Append("audio_size=" + string.Join(",", p.Assets.Select(a => a.Size.ToString(CultureInfo.InvariantCulture))) + "\n");
            if (notes != null)
                sb.Append("notes=" + notes + "\n");
            return sb.ToString();
        }

        private static void Synthesized(PackDescriptor p, string fullVersion)
        {
            string v = NumericVersion(fullVersion);
            ReleaseManifest m = ReleaseManifest.Parse(
                ManifestText(p, fullVersion, new string('a', 64), "요약 한 줄"));
            Check(m.Version == v && m.Tag == "v" + v, "version/tag = " + m.Version + "/" + m.Tag);
            Check(PluginVersion.Compare(m.Version, fullVersion) == 0,
                  "매니페스트 버전이 플러그인 버전 숫자부와 같음");
            Check(m.PluginAsset == "teamradio56-plugin-" + v + ".zip", "plugin_asset 이름");
            CheckPack(m, p);
            Check(m.Notes == "요약 한 줄", "notes");
            Check(ReleaseManifest.Parse(ManifestText(p, fullVersion, new string('a', 64), null)).Notes == "",
                  "notes 생략 가능");

            // 팩 자산은 전용 릴리스(audio-<pack>)에 둔다. 예전 방식(플러그인 릴리스에 함께)은
            // 아직 없는 미래 태그를 가리키면 사용자 음성 팩 업데이트가 404가 된다
            bool own = p.Tag == "audio-" + p.Pack;
            bool legacy = p.Tag.StartsWith("v", StringComparison.Ordinal)
                          && PluginVersion.ParseNumeric(p.Tag) != null && PluginVersion.Compare(p.Tag, v) <= 0;
            Check(own || legacy,
                  "audio_pack.txt의 tag(" + p.Tag + ")는 audio-" + p.Pack + " 또는 이번 버전(" + v + ") 이하 v태그");
            Check(p.Assets.Sum(a => a.Size) < 512L * 1024 * 1024, "음성 팩 전체 크기가 상식적 (< 512 MB)");
        }

        private static void CheckPack(ReleaseManifest m, PackDescriptor p)
        {
            Check(m.AudioPack == p.Pack && m.AudioTag == p.Tag, "audio_pack/audio_tag가 기술자와 같음");
            Check(m.AudioAssets.Count == p.Assets.Count, "audio_assets 개수");
            for (int i = 0; i < Math.Min(m.AudioAssets.Count, p.Assets.Count); i++)
            {
                AudioAsset a = m.AudioAssets[i], b = p.Assets[i];
                Check(a.Name == b.Name && a.Sha256 == b.Sha256 && a.Size == b.Size,
                      "audio 자산 #" + (i + 1) + " (" + b.Name + ") 이름·sha·크기 일치");
            }
            Check(m.AudioTotalSize == p.Assets.Sum(a => a.Size), "AudioTotalSize = 기술자 합계");
        }

        private static void RejectsBadDescriptors()
        {
            const string sha = "bf9ab0afd1c2875444ba73bb369fc4b02a5a6fec79a7f1c921f525b340f771ad";
            string ok = "pack=p1\ntag=v1.0.0\nasset=a.zip," + sha + ",10\n";
            Check(ParseDescriptor("\uFEFF# c\r\n" + ok.Replace("\n", "\r\n")).Assets.Count == 1, "BOM·CRLF·주석 허용");
            Rejects(ok + "asset=a.zip," + sha + ",10\n", "중복 asset");
            Rejects("pack=p1\ntag=v1.0.0\n", "asset 없음");
            Rejects("tag=v1.0.0\nasset=a.zip," + sha + ",10\n", "pack 없음");
            Rejects("pack=p1\nasset=a.zip," + sha + ",10\n", "tag 없음");
            Rejects("pack=p1\ntag=v1.0.0\nasset=../a.zip," + sha + ",10\n", "경로 문자 asset");
            Rejects("pack=p1\ntag=v1.0.0\nasset=a.wav," + sha + ",10\n", "zip 아닌 asset");
            Rejects("pack=p1\ntag=v1.0.0\nasset=a.zip,abc,10\n", "짧은 sha");
            Rejects("pack=p1\ntag=v1.0.0\nasset=a.zip," + sha + ",0\n", "크기 0");
            Rejects("pack=p1\ntag=v1.0.0\nasset=a.zip," + sha + "\n", "크기 빠짐");
            Rejects(ok + "audio_tag=v1\n", "모르는 키 (오타)");
            Rejects("pack=p 1\ntag=v1.0.0\nasset=a.zip," + sha + ",10\n", "공백 든 pack");
        }

        private static void Rejects(string text, string what)
        {
            try
            {
                ParseDescriptor(text);
                Failures.Add("기술자 거부 기대: " + what);
            }
            catch (FormatException)
            {
                _pass++;
            }
        }

        private static void Produced(PackDescriptor p, string fullVersion, string manifestPath, string audioDir)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(manifestPath));
            audioDir = audioDir != null ? Path.GetFullPath(audioDir) : null;
            string v = NumericVersion(fullVersion);
            ReleaseManifest m = ReleaseManifest.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
            Check(Path.GetFileName(manifestPath) == ReleaseManifest.FileName, "매니페스트 파일 이름");
            Check(m.Version == v && m.Tag == "v" + v, "version/tag = 플러그인 버전");
            Check(m.PluginAsset == "teamradio56-plugin-" + v + ".zip", "plugin_asset 이름");
            CheckPack(m, p);
            byte[] head = File.ReadAllBytes(manifestPath).Take(3).ToArray();
            Check(!(head.Length == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF),
                  "매니페스트는 BOM 없는 UTF-8");

            string zip = Path.Combine(dir, m.PluginAsset);
            Check(File.Exists(zip), "플러그인 zip이 매니페스트 옆에 있음");
            if (!File.Exists(zip))
                return;
            Check(Sha256Util.FileMatches(zip, m.PluginSha256), "plugin_sha256 = zip 실제 해시");
            using (ZipArchive za = ZipFile.OpenRead(zip))
            {
                var names = za.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
                Check(names.SequenceEqual(PluginSwap.DllNames.OrderBy(n => n, StringComparer.Ordinal)),
                      "zip 루트에 두 DLL만: " + string.Join(", ", names));
            }

            // 릴리스 폴더를 서버처럼 내놓고 사용자 PC 흐름을 돌린다
            string root = Path.Combine(Path.GetTempPath(), "teamradio56-releasecheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                string pluginDir = Path.Combine(root, "SimHub");
                string cacheDir = Path.Combine(root, "audio_cache");
                Directory.CreateDirectory(pluginDir);
                Directory.CreateDirectory(cacheDir);
                foreach (string n in PluginSwap.DllNames)
                    File.WriteAllBytes(Path.Combine(pluginDir, n), Encoding.ASCII.GetBytes("MZold-" + n));

                var dl = new FolderDownloader(dir, m.Tag, audioDir, m.AudioTag);
                var svc = new UpdateService(dl, Base);
                UpdateCheck oldUser = svc.Check("0.15.0-kokoro", cacheDir, CancellationToken.None);
                Check(oldUser.PluginUpdateAvailable, "0.15 사용자에게 플러그인 업데이트가 뜸");
                Check(oldUser.AudioUpdateAvailable, "팩 없는 사용자에게 음성 팩 업데이트가 뜸");
                Check(!svc.Check(fullVersion, cacheDir, CancellationToken.None).PluginUpdateAvailable,
                      "같은 버전 사용자에게는 플러그인 업데이트가 안 뜸");

                svc.InstallPlugin(oldUser.Latest, pluginDir, Path.Combine(root, "work"), null, CancellationToken.None);
                using (ZipArchive za = ZipFile.OpenRead(zip))
                {
                    foreach (string n in PluginSwap.DllNames)
                    {
                        byte[] want;
                        using (var ms = new MemoryStream())
                        {
                            using (Stream s = za.GetEntry(n).Open())
                                s.CopyTo(ms);
                            want = ms.ToArray();
                        }
                        Check(File.ReadAllBytes(Path.Combine(pluginDir, n)).SequenceEqual(want),
                              n + " 이(가) 릴리스 zip 내용으로 교체됨");
                        Check(File.Exists(Path.Combine(pluginDir, n + ".old")), n + ".old 백업 남음");
                    }
                }
                Check(UpdateService.CleanupOnStartup(pluginDir) == PluginSwap.DllNames.Length,
                      "다음 시작 때 .old 정리");

                string packDir = audioDir
                                 ?? (m.AudioTag == m.Tag ? dir : null);
                if (audioDir != null)
                    Check(m.AudioAssets.All(a => File.Exists(Path.Combine(audioDir, a.Name))),
                          "--audio-dir에 매니페스트의 음성 팩 zip이 전부 있음");
                if (packDir != null && m.AudioAssets.All(a => File.Exists(Path.Combine(packDir, a.Name))))
                {
                    long entries = 0;
                    foreach (AudioAsset a in m.AudioAssets)
                        using (ZipArchive za = ZipFile.OpenRead(Path.Combine(packDir, a.Name)))
                            entries += za.Entries.Count;
                    svc.InstallAudioPack(oldUser.Latest, cacheDir, Path.Combine(root, "work"), null,
                                         CancellationToken.None);
                    Check(AudioPack.Installed(cacheDir) == m.AudioPack, "음성 팩 설치 후 표식 = " + m.AudioPack);
                    int wav = Directory.GetFiles(cacheDir, "*.wav").Length;
                    Check(wav > 0 && wav == entries, $"음성 팩 wav {wav}개 = zip 항목 {entries}개");
                    Check(!AudioPack.NeedsUpdate(oldUser.Latest, cacheDir), "설치 후 음성 팩 업데이트가 더 안 뜸");
                    Console.WriteLine($"  음성 팩 {m.AudioPack} 설치 흐름 확인 ({m.AudioTag}) — wav {wav}개");
                }
                else
                {
                    Console.WriteLine("  (음성 팩 zip이 릴리스 폴더에 없어 팩 설치 흐름은 건너뜀)");
                }
            }
            catch (UpdateException ex)
            {
                Failures.Add("업데이트 흐름 실패 [" + ex.Kind + "] " + ex.Message);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (Exception) { }
            }
        }

        /// <summary>릴리스 폴더를 GitHub releases/{latest/download, download/tag} 처럼 내놓는다.
        /// 음성 팩 폴더를 주면 download/{audioTag}/ 를 그 폴더로 잇는다.</summary>
        private sealed class FolderDownloader : IDownloader
        {
            private readonly string _dir;
            private readonly string _tag;
            private readonly string _audioDir;
            private readonly string _audioTag;

            public FolderDownloader(string dir, string tag, string audioDir, string audioTag)
            {
                _dir = dir;
                _tag = tag;
                _audioDir = audioDir;
                _audioTag = audioTag;
            }

            private string Map(string url)
            {
                string latest = Base + "/latest/download/";
                string tagged = Base + "/download/" + Uri.EscapeDataString(_tag) + "/";
                string audio = _audioDir != null && _audioTag != _tag
                    ? Base + "/download/" + Uri.EscapeDataString(_audioTag) + "/" : null;
                string name = null;
                string folder = _dir;
                if (url.StartsWith(latest, StringComparison.Ordinal))
                    name = url.Substring(latest.Length);
                else if (url.StartsWith(tagged, StringComparison.Ordinal))
                    name = url.Substring(tagged.Length);
                else if (audio != null && url.StartsWith(audio, StringComparison.Ordinal))
                {
                    name = url.Substring(audio.Length);
                    folder = _audioDir;
                }
                string path = name != null ? Path.Combine(folder, Uri.UnescapeDataString(name)) : null;
                if (path == null || !File.Exists(path))
                    throw new UpdateException(UpdateErrorKind.NotFound, "404 (릴리스 폴더): " + url);
                return path;
            }

            public string DownloadText(string url, CancellationToken ct) =>
                File.ReadAllText(Map(url), Encoding.UTF8);

            public void DownloadFile(string url, string destPath, Action<long, long> progress,
                                     CancellationToken ct)
            {
                File.Copy(Map(url), destPath, true);
            }
        }

        private static void Section(string name, Action body)
        {
            int before = Failures.Count;
            try
            {
                body();
            }
            catch (Exception ex)
            {
                Failures.Add(name + ": 예외 " + ex.GetType().Name + " — " + ex.Message);
            }
            Console.WriteLine((Failures.Count == before ? "  ✓ " : "  ✗ ") + name);
        }

        private static void Check(bool cond, string what)
        {
            if (cond)
                _pass++;
            else
                Failures.Add(what);
        }
    }
}
