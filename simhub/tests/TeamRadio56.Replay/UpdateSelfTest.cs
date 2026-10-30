using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using TeamRadio56.Core.Logic;
using TeamRadio56.Core.Update;

namespace TeamRadio56.Replay
{
    /// <summary>
    /// --update-selftest: 업데이터 Core 로직을 임시 폴더에서 검증.
    /// 로드된 DLL 이름 바꾸기(Windows 전용 동작)는 IFileOps 가짜로 잠금/실패를 흉내 낸다.
    /// </summary>
    internal static class UpdateSelfTest
    {
        private static int _pass;
        private static readonly List<string> Failures = new List<string>();
        private static string _root;

        private const string CoreDll = "TeamRadio56.Core.dll";
        private const string SimHubDll = "TeamRadio56.SimHub.dll";
        private const string Base = "https://example.invalid/releases";

        public static int Run()
        {
            _root = Path.Combine(Path.GetTempPath(), "teamradio56-selftest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            try
            {
                Section("버전 비교", Versions);
                Section("매니페스트 파싱", Manifests);
                Section("sha256", Hashes);
                Section("zip 안전 풀기", Zips);
                Section("플러그인 설치 (가짜 다운로더)", PluginInstall);
                Section("DLL 교체·롤백", Swaps);
                Section(".old 정리", Cleanup);
                Section("음성 팩 표식·휴리스틱", Markers);
                Section("음성 팩 설치", AudioInstall);
                Section("확인 주기", Schedule);
                Section("업데이트 컨트롤러 (백그라운드 흐름)", Controller);
                Section("HTTP 다운로더 (로컬 서버)", Http);
            }
            finally
            {
                try { Directory.Delete(_root, true); } catch (Exception) { }
            }

            Console.WriteLine();
            if (Failures.Count > 0)
            {
                Console.WriteLine($"❌ 업데이터 셀프테스트 실패 {Failures.Count}건 (통과 {_pass}건)");
                foreach (string f in Failures)
                    Console.WriteLine("   - " + f);
                return 1;
            }
            Console.WriteLine($"✅ 업데이터 셀프테스트 {_pass}건 통과");
            return 0;
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
                Failures.Add(name + ": 예외 " + ex.GetType().Name + " — " + ex.Message + (Environment.GetEnvironmentVariable("SELFTEST_TRACE") == "1" ? "\n" + ex.StackTrace : ""));
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

        private static void Throws(UpdateErrorKind kind, Action action, string what)
        {
            try
            {
                action();
                Failures.Add(what + ": 예외가 나야 하는데 성공함");
            }
            catch (UpdateException ex) when (ex.Kind == kind)
            {
                _pass++;
            }
            catch (Exception ex)
            {
                Failures.Add(what + ": " + kind + " 기대, 실제 "
                             + (ex is UpdateException ue ? ue.Kind.ToString() : ex.GetType().Name)
                             + " — " + ex.Message);
            }
        }

        private static string NewDir(string name)
        {
            string d = Path.Combine(_root, name + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(d);
            return d;
        }

        private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

        private static string Text(string path) => File.ReadAllText(path, Encoding.UTF8);

        private static void MakeZip(string path, params (string name, byte[] data)[] entries)
        {
            using (var fs = new FileStream(path, FileMode.Create))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                foreach (var (name, data) in entries)
                    using (Stream s = zip.CreateEntry(name).Open())
                        s.Write(data, 0, data.Length);
        }

        private static byte[] ZipBytes(params (string name, byte[] data)[] entries)
        {
            string tmp = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
            MakeZip(tmp, entries);
            byte[] bytes = File.ReadAllBytes(tmp);
            File.Delete(tmp);
            return bytes;
        }

        // -- 버전 ----------------------------------------------------------

        private static void Versions()
        {
            Check(PluginVersion.IsNewer("0.16.0", "0.15.0-kokoro"), "0.16.0 > 0.15.0-kokoro");
            Check(!PluginVersion.IsNewer("0.16.0", "0.16.0-update"), "0.16.0 == 0.16.0-update");
            Check(PluginVersion.IsNewer("0.16.1", "0.16.0-update"), "0.16.1 > 0.16.0-update");
            Check(PluginVersion.IsNewer("0.10.0", "0.9.9"), "0.10.0 > 0.9.9 (사전순 아님)");
            Check(!PluginVersion.IsNewer("0.15.0", "0.16.0"), "구버전은 새것 아님");
            Check(PluginVersion.Compare("1.0", "1.0.0") == 0, "1.0 == 1.0.0");
            Check(PluginVersion.Compare("v0.16.0", "0.16.0") == 0, "v 접두 허용");
            Check(PluginVersion.IsNewer("1.0.0", "0.99.99"), "major 우선");
            Check(!PluginVersion.IsNewer("garbage", "0.1.0"), "해석 불가 latest → false");
            Check(!PluginVersion.IsNewer("1.0.0", "garbage"), "해석 불가 current → false");
            Check(!PluginVersion.IsNewer("1.0.0", null), "null current → false");
            Check(PluginVersion.ParseNumeric("0.16.0.5").SequenceEqual(new[] { 0, 16, 0 }), "넷째 자리 무시");
            Check(PluginVersion.ParseNumeric("2.x").SequenceEqual(new[] { 2, 0, 0 }), "2.x → 2.0.0");
            Check(PluginVersion.ParseNumeric("1.2.3-rc1").SequenceEqual(new[] { 1, 2, 3 }), "접미사 무시");
            Check(PluginVersion.ParseNumeric("99999999999") == null, "int 범위 초과 → null");
            Check(PluginVersion.ParseNumeric("-1.0.0") == null, "음수 → null");
            Check(PluginVersion.IsStrict("0.16.0"), "엄격 x.y.z");
            Check(!PluginVersion.IsStrict("0.16"), "엄격: 두 자리 거부");
            Check(!PluginVersion.IsStrict("0.16.0-update"), "엄격: 접미사 거부");
            Check(!PluginVersion.IsStrict("v0.16.0"), "엄격: v 거부");
        }

        // -- 매니페스트 ----------------------------------------------------

        private static readonly string ShaA = new string('a', 64);
        private static readonly string ShaB = new string('b', 64);

        private static string ManifestText(string plugin = null, string audio = null, string extra = "")
        {
            return "\uFEFF# teamradio56 release\r\n"
                + "format=1\r\n"
                + "version=0.16.0\r\n"
                + "tag=v0.16.0\r\n"
                + "future_key=whatever\r\n"
                + (plugin ?? "plugin_asset=teamradio56-plugin-0.16.0.zip\r\nplugin_sha256=" + ShaA.ToUpperInvariant() + "\r\n")
                + (audio ?? "audio_pack=kokoro-v015\r\naudio_tag=v0.16.0\r\n"
                   + "audio_assets=p1.zip, p2.zip\r\naudio_sha256=" + ShaA + "," + ShaB + "\r\n"
                   + "audio_size=100,200\r\n")
                + "notes=첫 업데이터 릴리스 = 테스트\r\n"
                + extra;
        }

        private static void Manifests()
        {
            ReleaseManifest m = ReleaseManifest.Parse(ManifestText());
            Check(m.Format == 1 && m.Version == "0.16.0" && m.Tag == "v0.16.0", "기본 필드");
            Check(m.PluginAsset == "teamradio56-plugin-0.16.0.zip", "plugin_asset");
            Check(m.PluginSha256 == ShaA, "sha 소문자화");
            Check(m.AudioPack == "kokoro-v015" && m.AudioTag == "v0.16.0", "audio_pack/tag");
            Check(m.AudioAssets.Count == 2 && m.AudioAssets[1].Name == "p2.zip"
                  && m.AudioAssets[1].Sha256 == ShaB && m.AudioAssets[1].Size == 200, "audio_assets 순서·sha·크기");
            Check(m.AudioTotalSize == 300, "audio 전체 크기");
            Check(m.Notes == "첫 업데이터 릴리스 = 테스트", "notes ('=' 포함 값)");

            ReleaseManifest noAudio = ReleaseManifest.Parse(ManifestText(audio: ""));
            Check(!noAudio.HasAudioPack && noAudio.AudioAssets.Count == 0, "음성 팩 없는 릴리스");
            ReleaseManifest noSize = ReleaseManifest.Parse(ManifestText(audio:
                "audio_pack=k\naudio_tag=v1\naudio_assets=a.zip\naudio_sha256=" + ShaA + "\n"));
            Check(noSize.AudioAssets[0].Size == -1 && noSize.AudioTotalSize == -1, "audio_size 생략 → -1");

            Throws(UpdateErrorKind.BadManifest, () => ReleaseManifest.Parse(
                ManifestText(plugin: "plugin_asset=x.zip\n")), "plugin_sha256 누락");
            Throws(UpdateErrorKind.UnsupportedFormat, () => ReleaseManifest.Parse(
                ManifestText().Replace("format=1", "format=2")), "format=2");
            Throws(UpdateErrorKind.BadManifest, () => ReleaseManifest.Parse(
                ManifestText().Replace("format=1", "format=abc")), "format=abc");
            Throws(UpdateErrorKind.BadManifest, () => ReleaseManifest.Parse(
                ManifestText().Replace("format=1\r\n", "")), "format 누락");
            Throws(UpdateErrorKind.BadManifest, () => ReleaseManifest.Parse(
                ManifestText().Replace("version=0.16.0", "version=0.16")), "version 두 자리");
            Throws(UpdateErrorKind.BadManifest, () => ReleaseManifest.Parse(
                ManifestText().Replace("tag=v0.16.0", "tag=../v0.16.0")), "tag 경로 탈출");
            Throws(UpdateErrorKind.BadManifest, () => ReleaseManifest.Parse(
                ManifestText(plugin: "plugin_asset=a/b.zip\nplugin_sha256=" + ShaA + "\n")), "plugin_asset 슬래시");
            Throws(UpdateErrorKind.BadManifest, () => ReleaseManifest.Parse(
                ManifestText(plugin: "plugin_asset=b.exe\nplugin_sha256=" + ShaA + "\n")), "plugin_asset zip 아님");
            Throws(UpdateErrorKind.BadManifest, () => ReleaseManifest.Parse(
                ManifestText(plugin: "plugin_asset=b.zip\nplugin_sha256=" + ShaA.Substring(1) + "\n")), "sha 63자");
            Throws(UpdateErrorKind.BadManifest, () => ReleaseManifest.Parse(
                ManifestText(plugin: "plugin_asset=b.zip\nplugin_sha256=" + new string('g', 64) + "\n")), "sha 16진수 아님");
            Throws(UpdateErrorKind.BadManifest, () => ReleaseManifest.Parse(ManifestText(audio:
                "audio_pack=k\naudio_tag=v1\naudio_assets=a.zip,b.zip\naudio_sha256=" + ShaA + "\n")),
                "audio sha 개수 불일치");
            Throws(UpdateErrorKind.BadManifest, () => ReleaseManifest.Parse(ManifestText(audio:
                "audio_pack=k\naudio_tag=v1\naudio_assets=a.zip,A.zip\naudio_sha256=" + ShaA + "," + ShaB + "\n")),
                "audio 자산 중복");
            Throws(UpdateErrorKind.BadManifest, () => ReleaseManifest.Parse(ManifestText(audio:
                "audio_pack=k\naudio_assets=a.zip\naudio_sha256=" + ShaA + "\n")), "audio_tag 누락");
            Throws(UpdateErrorKind.BadManifest, () => ReleaseManifest.Parse(ManifestText(audio:
                "audio_pack=k\naudio_tag=v1\naudio_assets=..\\x.zip\naudio_sha256=" + ShaA + "\n")),
                "audio 자산 경로");
            Throws(UpdateErrorKind.BadManifest, () => ReleaseManifest.Parse(ManifestText(audio:
                "audio_pack=k\naudio_tag=v1\naudio_assets=a.zip\naudio_sha256=" + ShaA + "\naudio_size=0\n")),
                "audio_size 0");
            Throws(UpdateErrorKind.BadManifest, () => ReleaseManifest.Parse(
                "<!DOCTYPE html><html><body>Sign in to Wi-Fi</body></html>"), "HTML 응답(포털)");
            Throws(UpdateErrorKind.BadManifest, () => ReleaseManifest.Parse(""), "빈 응답");

            var svc = new UpdateService(new FakeDownloader(), "https://github.com/hyungwookc07/teamradio56/releases/");
            Check(svc.LatestManifestUrl
                  == "https://github.com/hyungwookc07/teamradio56/releases/latest/download/teamradio56-release.txt",
                  "latest 매니페스트 URL");
            Check(svc.AssetUrl("v0.16.0", "a.zip")
                  == "https://github.com/hyungwookc07/teamradio56/releases/download/v0.16.0/a.zip", "자산 URL");
            Check(new UpdateService(new FakeDownloader()).LatestManifestUrl
                  == "https://github.com/hyungwookc07/teamradio56/releases/latest/download/teamradio56-release.txt",
                  "기본 저장소 URL");
        }

        // -- sha256 --------------------------------------------------------

        private static void Hashes()
        {
            Check(Sha256Util.OfBytes(B("abc"))
                  == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", "sha256(abc)");
            string dir = NewDir("sha");
            string f = Path.Combine(dir, "x.bin");
            File.WriteAllBytes(f, B("abc"));
            Sha256Util.Verify(f, "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD");
            _pass++;
            Throws(UpdateErrorKind.Integrity, () => Sha256Util.Verify(f, ShaA), "sha 불일치");
            Check(!Sha256Util.FileMatches(Path.Combine(dir, "none"), ShaA), "없는 파일 불일치");
        }

        // -- zip ----------------------------------------------------------

        private static void Zips()
        {
            Check(SafeZip.RootFileName("a.wav") == "a.wav", "루트 파일");
            Check(SafeZip.RootFileName("sub/a.wav") == null, "하위 폴더 → 건너뜀");
            Check(SafeZip.RootFileName("sub\\a.wav") == null, "역슬래시 하위 폴더 → 건너뜀");
            Check(SafeZip.RootFileName("dir/") == null, "폴더 항목 → 건너뜀");
            foreach (string evil in new[] { "../a.wav", "..\\a.wav", "/etc/a.wav", "\\\\srv\\a.wav",
                                            "C:\\a.wav", "C:a.wav", "a/../../b.wav", "a.wav:ads", "..", "", "a.wav." })
                Throws(UpdateErrorKind.BadPackage, () => SafeZip.RootFileName(evil), "zip 항목 거부: '" + evil + "'");

            // zip-slip: 하나라도 탈출하면 정상 항목도 쓰지 않는다
            string parent = NewDir("slip");
            string target = Path.Combine(parent, "cache");
            Directory.CreateDirectory(target);
            string slip = Path.Combine(parent, "slip.zip");
            MakeZip(slip, ("good.wav", B("g")), ("../evil.wav", B("e")));
            Throws(UpdateErrorKind.BadPackage, () => SafeZip.ExtractRootFiles(slip, target, ".wav"), "zip-slip 거부");
            Check(!File.Exists(Path.Combine(parent, "evil.wav")), "zip-slip: 밖에 안 씀");
            Check(!File.Exists(Path.Combine(target, "good.wav")), "zip-slip: 정상 항목도 안 씀");
            string slip2 = Path.Combine(parent, "slip2.zip");
            MakeZip(slip2, ("good.wav", B("g")), ("sub\\..\\..\\evil2.wav", B("e")));
            Throws(UpdateErrorKind.BadPackage, () => SafeZip.ExtractRootFiles(slip2, target, ".wav"), "역슬래시 zip-slip 거부");
            Check(!File.Exists(Path.Combine(parent, "evil2.wav")), "역슬래시 zip-slip: 밖에 안 씀");

            // 정상: 루트 wav만, 덮어쓰기
            string ok = Path.Combine(parent, "ok.zip");
            MakeZip(ok, ("x.wav", B("new-x")), ("y.WAV", B("y")), ("readme.txt", B("r")),
                    ("sub/z.wav", B("z")), ("dir/", new byte[0]));
            File.WriteAllText(Path.Combine(target, "x.wav"), "old-x");
            var seen = new List<int>();
            int n = SafeZip.ExtractRootFiles(ok, target, ".wav", (d, t) => seen.Add(d));
            Check(n == 2, "루트 wav 2개만 풂 (실제 " + n + ")");
            Check(Text(Path.Combine(target, "x.wav")) == "new-x", "기존 파일 덮어씀");
            Check(File.Exists(Path.Combine(target, "y.WAV")), "확장자 대소문자 무시");
            Check(!File.Exists(Path.Combine(target, "readme.txt")), "wav 아닌 파일 건너뜀");
            Check(!File.Exists(Path.Combine(target, "z.wav")) && !Directory.Exists(Path.Combine(target, "sub")),
                  "하위 폴더 항목 건너뜀");
            Check(!Directory.GetFiles(target, "*.part").Any(), ".part 안 남김");
            Check(seen.SequenceEqual(new[] { 1, 2 }), "풀기 진행률");

            string junk = Path.Combine(parent, "junk.zip");
            File.WriteAllBytes(junk, B("this is not a zip file at all"));
            Throws(UpdateErrorKind.BadPackage, () => SafeZip.ExtractRootFiles(junk, target, ".wav"), "깨진 zip");
        }

        // -- 플러그인 설치 ---------------------------------------------------

        private static string PluginDir()
        {
            string d = NewDir("plugin");
            File.WriteAllText(Path.Combine(d, CoreDll), "MZ old core");
            File.WriteAllText(Path.Combine(d, SimHubDll), "MZ old simhub");
            File.WriteAllText(Path.Combine(d, "teamradio56.settings.txt"), "keep");
            return d;
        }

        private static bool Untouched(string pluginDir)
        {
            return Text(Path.Combine(pluginDir, CoreDll)) == "MZ old core"
                && Text(Path.Combine(pluginDir, SimHubDll)) == "MZ old simhub"
                && !Directory.GetFiles(pluginDir, "*.old*").Any();
        }

        private static ReleaseManifest PluginManifest(byte[] zip, string shaOverride = null)
        {
            return ReleaseManifest.Parse("format=1\nversion=0.16.0\ntag=v0.16.0\n"
                + "plugin_asset=teamradio56-plugin-0.16.0.zip\n"
                + "plugin_sha256=" + (shaOverride ?? Sha256Util.OfBytes(zip)) + "\n");
        }

        private static readonly string PluginUrl = Base + "/download/v0.16.0/teamradio56-plugin-0.16.0.zip";

        private static void PluginInstall()
        {
            byte[] good = ZipBytes((CoreDll, B("MZ new core")), (SimHubDll, B("MZ new simhub")),
                                   ("README.txt", B("extra files are ignored")));

            // 성공
            string dir = PluginDir();
            string work = UpdateService.DefaultWorkDir(dir);
            var dl = new FakeDownloader();
            dl.Files[PluginUrl] = good;
            dl.Files[Base + "/latest/download/teamradio56-release.txt"] =
                B("format=1\nversion=0.16.0\ntag=v0.16.0\nplugin_asset=teamradio56-plugin-0.16.0.zip\n"
                  + "plugin_sha256=" + Sha256Util.OfBytes(good) + "\n");
            var svc = new UpdateService(dl, Base);
            UpdateCheck chk = svc.Check("0.15.0-kokoro", Path.Combine(dir, "no_cache"), CancellationToken.None);
            Check(chk.PluginUpdateAvailable && !chk.AudioUpdateAvailable && chk.InstalledAudioPack == null,
                  "Check: 플러그인 업데이트 있음, 음성 팩 없음");
            Check(!new UpdateCheck(chk.Latest, "0.16.0-update", null).PluginUpdateAvailable, "Check: 같은 버전이면 없음");
            var stages = new List<UpdateStage>();
            svc.InstallPlugin(chk.Latest, dir, work, p => stages.Add(p.Stage), CancellationToken.None);
            Check(Text(Path.Combine(dir, CoreDll)) == "MZ new core"
                  && Text(Path.Combine(dir, SimHubDll)) == "MZ new simhub", "새 DLL 설치");
            Check(Text(Path.Combine(dir, CoreDll + ".old")) == "MZ old core"
                  && Text(Path.Combine(dir, SimHubDll + ".old")) == "MZ old simhub", "구 DLL은 .old로");
            string record = Path.Combine(dir, PluginSwap.BackupRecordName);
            Check(File.Exists(record)
                  && File.ReadAllLines(record).Contains(CoreDll + ".old -> " + CoreDll)
                  && File.ReadAllLines(record).Contains(SimHubDll + ".old -> " + SimHubDll),
                  "백업 기록: DLL별 정확한 백업 이름");
            Check(!File.Exists(Path.Combine(dir, "README.txt")), "zip의 다른 파일은 설치 안 함");
            Check(Text(Path.Combine(dir, "teamradio56.settings.txt")) == "keep", "설정 파일 보존");
            Check(!Directory.Exists(Path.Combine(work, "staging"))
                  && !File.Exists(Path.Combine(work, "teamradio56-plugin-0.16.0.zip")), "작업 파일 정리");
            Check(!File.Exists(Path.Combine(dir, "teamradio56_write_probe.tmp")), "쓰기 확인 파일 정리");
            Check(stages.Contains(UpdateStage.Downloading) && stages.Contains(UpdateStage.Verifying)
                  && stages.Contains(UpdateStage.Installing) && stages.Last() == UpdateStage.Done, "진행 단계 보고");

            // sha 불일치 → 아무것도 안 건드림
            dir = PluginDir();
            work = UpdateService.DefaultWorkDir(dir);
            dl = new FakeDownloader();
            dl.Files[PluginUrl] = good;
            svc = new UpdateService(dl, Base);
            Throws(UpdateErrorKind.Integrity, () => svc.InstallPlugin(PluginManifest(good, ShaB), dir, work, null,
                CancellationToken.None), "플러그인 sha 불일치");
            Check(Untouched(dir), "sha 불일치: 플러그인 그대로");
            Check(!File.Exists(Path.Combine(work, "teamradio56-plugin-0.16.0.zip")), "sha 불일치: 받은 zip 삭제");

            // DLL 하나 누락
            byte[] missing = ZipBytes((CoreDll, B("MZ new core")));
            dl.Files[PluginUrl] = missing;
            Throws(UpdateErrorKind.BadPackage, () => svc.InstallPlugin(PluginManifest(missing), dir, work, null,
                CancellationToken.None), "zip에 SimHub DLL 없음");
            Check(Untouched(dir), "DLL 누락: 플러그인 그대로");

            // DLL이 하위 폴더에
            byte[] nested = ZipBytes(("x/" + CoreDll, B("MZ a")), ("x/" + SimHubDll, B("MZ b")));
            dl.Files[PluginUrl] = nested;
            Throws(UpdateErrorKind.BadPackage, () => svc.InstallPlugin(PluginManifest(nested), dir, work, null,
                CancellationToken.None), "DLL이 zip 루트에 없음");
            Check(Untouched(dir), "하위 폴더 DLL: 플러그인 그대로");

            // 빈 DLL
            byte[] empty = ZipBytes((CoreDll, new byte[0]), (SimHubDll, B("MZ b")));
            dl.Files[PluginUrl] = empty;
            Throws(UpdateErrorKind.BadPackage, () => svc.InstallPlugin(PluginManifest(empty), dir, work, null,
                CancellationToken.None), "빈 DLL");

            // DLL이 아닌 내용
            byte[] notPe = ZipBytes((CoreDll, B("hello")), (SimHubDll, B("MZ b")));
            dl.Files[PluginUrl] = notPe;
            Throws(UpdateErrorKind.BadPackage, () => svc.InstallPlugin(PluginManifest(notPe), dir, work, null,
                CancellationToken.None), "PE 아닌 DLL");
            Check(Untouched(dir), "PE 아님: 플러그인 그대로");

            // 플러그인 zip의 zip-slip
            byte[] slip = ZipBytes((CoreDll, B("MZ a")), (SimHubDll, B("MZ b")), ("../" + CoreDll, B("MZ evil")));
            dl.Files[PluginUrl] = slip;
            Throws(UpdateErrorKind.BadPackage, () => svc.InstallPlugin(PluginManifest(slip), dir, work, null,
                CancellationToken.None), "플러그인 zip-slip");
            Check(Untouched(dir) && !File.Exists(Path.Combine(work, CoreDll)), "zip-slip: 플러그인 그대로, 스테이징 밖에 안 씀");

            // 404
            dl.Files.Clear();
            Throws(UpdateErrorKind.NotFound, () => svc.InstallPlugin(PluginManifest(good), dir, work, null,
                CancellationToken.None), "자산 404");
            Check(Untouched(dir), "404: 플러그인 그대로");

            // 오프라인 매니페스트
            dl.FailAll = true;
            Throws(UpdateErrorKind.Network, () => svc.FetchLatest(CancellationToken.None), "오프라인 확인");
            dl.FailAll = false;

            // 취소
            dl.Files[PluginUrl] = good;
            var cts = new CancellationTokenSource();
            cts.Cancel();
            Throws(UpdateErrorKind.Cancelled, () => svc.InstallPlugin(PluginManifest(good), dir, work, null, cts.Token),
                "취소");
            Check(Untouched(dir), "취소: 플러그인 그대로");
        }

        // -- 교체·롤백 -------------------------------------------------------

        private static (string plugin, string staging) SwapDirs()
        {
            string plugin = PluginDir();
            string staging = Path.Combine(plugin, "teamradio56_update", "staging");
            Directory.CreateDirectory(staging);
            File.WriteAllText(Path.Combine(staging, CoreDll), "MZ new core");
            File.WriteAllText(Path.Combine(staging, SimHubDll), "MZ new simhub");
            return (plugin, staging);
        }

        private static void Swaps()
        {
            // 두 번째 DLL 넣기 실패 → 전부 되돌림
            var (plugin, staging) = SwapDirs();
            string liveSimHub = Path.Combine(plugin, SimHubDll);
            var ops = new FaultyFileOps();
            FaultyFileOps first = ops;
            ops.FailMoveTo = dst => first.Moves == 4 && dst == liveSimHub;   // 새 SimHub DLL 넣기만 실패
            Throws(UpdateErrorKind.Io, () => PluginSwap.Install(plugin, staging, PluginSwap.DllNames, ops),
                "두 번째 이동 실패");
            Check(Untouched(plugin), "롤백: 두 DLL 원래대로, .old 없음");
            Check(Text(Path.Combine(staging, CoreDll)) == "MZ new core"
                  && Text(Path.Combine(staging, SimHubDll)) == "MZ new simhub", "롤백: 스테이징 복원");
            Check(ops.Moves == 4 + 3, "롤백: 이동 4번(마지막 실패) + 되돌리기 3번 (실제 " + ops.Moves + ")");

            // 첫 백업부터 권한 없음
            (plugin, staging) = SwapDirs();
            ops = new FaultyFileOps { FailMoveTo = dst => dst.EndsWith(".old"), Denied = true };
            Throws(UpdateErrorKind.AccessDenied, () => PluginSwap.Install(plugin, staging, PluginSwap.DllNames, ops),
                "권한 없음 분류");
            Check(Untouched(plugin), "권한 없음: 그대로");

            // 잠긴 .old → .old1
            (plugin, staging) = SwapDirs();
            string coreOld = Path.Combine(plugin, CoreDll + ".old");
            File.WriteAllText(coreOld, "MZ loaded previous");
            ops = new FaultyFileOps();
            ops.Locked.Add(coreOld);
            IList<SwapStep> steps = PluginSwap.Install(plugin, staging, PluginSwap.DllNames, ops);
            Check(steps[0].BackupPath == coreOld + "1", "잠긴 .old → .old1");
            Check(Text(coreOld) == "MZ loaded previous" && Text(coreOld + "1") == "MZ old core", "잠긴 .old 보존, .old1에 백업");
            Check(Text(Path.Combine(plugin, CoreDll)) == "MZ new core", "잠긴 .old: 설치 성공");

            // .old, .old1 둘 다 잠김 → .old2
            (plugin, staging) = SwapDirs();
            coreOld = Path.Combine(plugin, CoreDll + ".old");
            File.WriteAllText(coreOld, "a");
            File.WriteAllText(coreOld + "1", "b");
            ops = new FaultyFileOps();
            ops.Locked.Add(coreOld);
            ops.Locked.Add(coreOld + "1");
            Check(PluginSwap.ChooseBackupPath(Path.Combine(plugin, CoreDll), ops) == coreOld + "2", ".old/.old1 잠김 → .old2");

            // 안 잠긴 묵은 .old는 지우고 재사용
            (plugin, staging) = SwapDirs();
            coreOld = Path.Combine(plugin, CoreDll + ".old");
            File.WriteAllText(coreOld, "stale");
            steps = PluginSwap.Install(plugin, staging, PluginSwap.DllNames, RealFileOps.Instance);
            Check(steps[0].BackupPath == coreOld && Text(coreOld) == "MZ old core", "묵은 .old 재사용");

            // 스테이징에 DLL 없음 → 아무것도 안 건드림
            (plugin, staging) = SwapDirs();
            File.Delete(Path.Combine(staging, SimHubDll));
            Throws(UpdateErrorKind.BadPackage, () => PluginSwap.Install(plugin, staging, PluginSwap.DllNames,
                RealFileOps.Instance), "스테이징 누락");
            Check(Untouched(plugin), "스테이징 누락: 그대로");

            // 되돌리기마저 실패 → 수동 복구 안내
            (plugin, staging) = SwapDirs();
            liveSimHub = Path.Combine(plugin, SimHubDll);
            string stagedCore = Path.Combine(staging, CoreDll);
            ops = new FaultyFileOps();
            FaultyFileOps undoFails = ops;
            ops.FailMoveTo = dst => (undoFails.Moves == 4 && dst == liveSimHub) || (undoFails.Moves > 4 && dst == stagedCore);
            try
            {
                PluginSwap.Install(plugin, staging, PluginSwap.DllNames, ops);
                Failures.Add("되돌리기 실패: 예외가 나야 함");
            }
            catch (UpdateException ex)
            {
                Check(ex.Kind == UpdateErrorKind.RollbackFailed && ex.Message.Contains("수동"),
                      "되돌리기 실패 → RollbackFailed + 수동 복구 안내");
                // 나머지 되돌리기는 계속 진행 — SimHub DLL은 원래대로
                Check(Text(liveSimHub) == "MZ old simhub", "되돌리기 실패해도 나머지는 복구");
                string liveCore = Path.Combine(plugin, CoreDll);
                Check(ex.ManualRestore.Count == 1 && ex.ManualRestore[0].Key == liveCore + ".old"
                      && ex.ManualRestore[0].Value == liveCore,
                      "되돌리기 실패: 수동 복구 목록은 남은 백업 → 원래 이름만 (실제 "
                      + string.Join("; ", ex.ManualRestore.Select(r => r.Key + "→" + r.Value)) + ")");
            }

            // 서비스 경유 되돌리기 실패 → 백업 기록에 수동 복구 쌍
            string rbDir = PluginDir();
            byte[] rbZip = ZipBytes((CoreDll, B("MZ new core")), (SimHubDll, B("MZ new simhub")));
            var rbDl = new FakeDownloader();
            rbDl.Files[PluginUrl] = rbZip;
            string rbSimHub = Path.Combine(rbDir, SimHubDll);
            string rbStagedCore = Path.Combine(UpdateService.DefaultWorkDir(rbDir), "staging", CoreDll);
            var rbOps = new FaultyFileOps();
            FaultyFileOps rbRef = rbOps;
            rbOps.FailMoveTo = dst => (rbRef.Moves == 4 && dst == rbSimHub) || (rbRef.Moves > 4 && dst == rbStagedCore);
            Throws(UpdateErrorKind.RollbackFailed, () => new UpdateService(rbDl, Base, rbOps).InstallPlugin(
                PluginManifest(rbZip), rbDir, UpdateService.DefaultWorkDir(rbDir), null, CancellationToken.None),
                "서비스 경유 되돌리기 실패");
            string rbRecord = Path.Combine(rbDir, PluginSwap.BackupRecordName);
            Check(File.Exists(rbRecord) && File.ReadAllLines(rbRecord).Contains(CoreDll + ".old -> " + CoreDll)
                  && !File.ReadAllLines(rbRecord).Any(l => l.StartsWith(SimHubDll)),
                  "되돌리기 실패: 백업 기록엔 직접 바꿔야 할 것만");
        }

        // -- .old 정리 --------------------------------------------------------

        private static void Cleanup()
        {
            string dir = NewDir("cleanup");
            foreach (string f in new[] { CoreDll, CoreDll + ".old", SimHubDll + ".old3", CoreDll + ".old1",
                                         "Other.dll.old", "TeamRadio56.settings.txt", "TeamRadio56.Core.dll.older" })
                File.WriteAllText(Path.Combine(dir, f), f);
            Directory.CreateDirectory(Path.Combine(dir, UpdateService.WorkDirName, "staging"));
            File.WriteAllText(Path.Combine(dir, PluginSwap.BackupRecordName), "x");
            var ops = new FaultyFileOps();
            ops.Locked.Add(Path.Combine(dir, CoreDll + ".old1"));
            int removed = UpdateService.CleanupOnStartup(dir, ops);
            Check(removed == 2, ".old 2개 삭제 (실제 " + removed + ")");
            Check(!File.Exists(Path.Combine(dir, CoreDll + ".old")) && !File.Exists(Path.Combine(dir, SimHubDll + ".old3")),
                  ".old/.old3 삭제");
            Check(File.Exists(Path.Combine(dir, CoreDll + ".old1")), "잠긴 .old1은 남김 (예외 없음)");
            Check(File.Exists(Path.Combine(dir, CoreDll)) && File.Exists(Path.Combine(dir, "Other.dll.old"))
                  && File.Exists(Path.Combine(dir, "TeamRadio56.settings.txt"))
                  && File.Exists(Path.Combine(dir, "TeamRadio56.Core.dll.older")), "다른 파일은 안 건드림");
            Check(!Directory.Exists(Path.Combine(dir, UpdateService.WorkDirName)), "작업 폴더 삭제");
            Check(!File.Exists(Path.Combine(dir, PluginSwap.BackupRecordName)), "백업 기록 삭제 (플러그인이 떴음)");

            // 읽기 전용 .old도 지운다 (Windows에선 속성을 풀어야 지워짐)
            dir = NewDir("cleanup-ro");
            string ro = Path.Combine(dir, SimHubDll + ".old");
            File.WriteAllText(ro, "ro");
            File.SetAttributes(ro, FileAttributes.ReadOnly);
            Check(UpdateService.CleanupOnStartup(dir) == 1 && !File.Exists(ro), "읽기 전용 .old 삭제");
            Check(UpdateService.CleanupOnStartup(Path.Combine(dir, "nope")) == 0, "없는 폴더도 예외 없음");
        }

        // -- 음성 팩 표식 ------------------------------------------------------

        private static void MakeSegments(string dir, int count = 4)
        {
            for (int i = 0; i < count; i++)
                File.WriteAllText(Path.Combine(dir, RadioAssembler.AssetNames[i]), "seg");
        }

        private static ReleaseManifest PackManifest(string pack) => ReleaseManifest.Parse(
            "format=1\nversion=0.16.0\ntag=v0.16.0\nplugin_asset=p.zip\nplugin_sha256=" + ShaA + "\n"
            + "audio_pack=" + pack + "\naudio_tag=v0.16.0\naudio_assets=a.zip\naudio_sha256=" + ShaA + "\n");

        private static void Markers()
        {
            string d = NewDir("marker");
            Check(AudioPack.Installed(d) == null, "빈 캐시 → 없음");
            Check(AudioPack.Installed(Path.Combine(d, "nope")) == null, "없는 폴더 → 없음");
            Check(AudioPack.Installed(null) == null, "null → 없음");
            MakeSegments(d, 3);
            Check(AudioPack.Installed(d) == null, "세그먼트 3개 → 없음");
            MakeSegments(d, 4);
            Check(AudioPack.Installed(d) == AudioPack.LegacyPackId, "세그먼트 4개 → kokoro-v015 (수동 설치)");
            Check(!AudioPack.NeedsUpdate(PackManifest("kokoro-v015"), d), "같은 팩 → 업데이트 불필요");
            Check(AudioPack.NeedsUpdate(PackManifest("kokoro-v016"), d), "다른 팩 → 업데이트 필요");
            Check(!AudioPack.NeedsUpdate(ReleaseManifest.Parse(
                "format=1\nversion=0.16.0\ntag=v1\nplugin_asset=p.zip\nplugin_sha256=" + ShaA), d), "팩 없는 릴리스 → 불필요");

            AudioPack.WriteMarker(d, "kokoro-v016");
            Check(AudioPack.Installed(d) == "kokoro-v016", "표식 우선 (휴리스틱보다)");
            Check(!AudioPack.NeedsUpdate(PackManifest("kokoro-v016"), d), "표식과 같으면 불필요");
            File.WriteAllText(AudioPack.MarkerPath(d), "\uFEFF# c\r\n\r\npack=kokoro-v017\r\n");
            Check(AudioPack.Installed(d) == "kokoro-v017", "표식 BOM/주석/CRLF");
            File.WriteAllText(AudioPack.MarkerPath(d), "kokoro-v018\n");
            Check(AudioPack.Installed(d) == "kokoro-v018", "pack= 없는 표식");
            File.WriteAllText(AudioPack.MarkerPath(d), "");
            Check(AudioPack.Installed(d).StartsWith(AudioPack.IncompletePrefix), "빈 표식 → 미완료");
            Check(AudioPack.NeedsUpdate(PackManifest("kokoro-v015"), d), "빈 표식 → 재설치 권유");
        }

        // -- 음성 팩 설치 ------------------------------------------------------

        private static void AudioInstall()
        {
            byte[] z1 = ZipBytes(("a1.wav", B("A1")), ("shared.wav", B("new-shared")), (RadioAssembler.AssetNames[0], B("new-click")));
            byte[] z2 = ZipBytes(("b1.wav", B("B1")), ("notes.txt", B("skip")));
            string manifest(string sha2) =>
                "format=1\nversion=0.16.0\ntag=v0.17.0\nplugin_asset=p.zip\nplugin_sha256=" + ShaA + "\n"
                + "audio_pack=kokoro-v016\naudio_tag=v0.16.0\naudio_assets=pack_1of2.zip,pack_2of2.zip\n"
                + "audio_sha256=" + Sha256Util.OfBytes(z1) + "," + sha2 + "\n";
            string u1 = Base + "/download/v0.16.0/pack_1of2.zip";
            string u2 = Base + "/download/v0.16.0/pack_2of2.zip";

            string cache = NewDir("cache");
            MakeSegments(cache);
            File.WriteAllText(Path.Combine(cache, "shared.wav"), "old-shared");
            string work = NewDir("work");
            var dl = new FakeDownloader();
            dl.Files[u1] = z1;
            dl.Files[u2] = z2;
            var svc = new UpdateService(dl, Base);

            // 두 번째 zip sha 불일치 → 아무것도 풀지 않고 표식도 그대로
            ReleaseManifest bad = ReleaseManifest.Parse(manifest(ShaB));
            Check(new UpdateCheck(bad, "0.16.0", AudioPack.Installed(cache)).AudioUpdateAvailable, "Check: 음성 팩 업데이트 있음");
            Throws(UpdateErrorKind.Integrity, () => svc.InstallAudioPack(bad, cache, work, null, CancellationToken.None),
                "음성 팩 sha 불일치");
            Check(!File.Exists(Path.Combine(cache, "a1.wav")) && Text(Path.Combine(cache, "shared.wav")) == "old-shared",
                  "sha 불일치: 검증 전엔 풀지 않음");
            Check(AudioPack.Installed(cache) == AudioPack.LegacyPackId && !File.Exists(AudioPack.MarkerPath(cache)),
                  "sha 불일치: 표식 그대로");
            Check(File.Exists(Path.Combine(work, "pack_1of2.zip")), "검증된 첫 zip은 재시도용으로 남김");

            // 재시도: 첫 zip은 다시 받지 않는다
            ReleaseManifest good = ReleaseManifest.Parse(manifest(Sha256Util.OfBytes(z2)));
            dl.Requests.Clear();
            var progress = new List<UpdateProgress>();
            svc.InstallAudioPack(good, cache, work, progress.Add, CancellationToken.None);
            Check(!dl.Requests.Contains(u1) && dl.Requests.Contains(u2), "검증된 zip은 다시 안 받음");
            Check(Text(Path.Combine(cache, "a1.wav")) == "A1" && Text(Path.Combine(cache, "b1.wav")) == "B1", "wav 풀림");
            Check(Text(Path.Combine(cache, "shared.wav")) == "new-shared", "기존 wav 덮어씀");
            Check(Text(Path.Combine(cache, RadioAssembler.AssetNames[0])) == "new-click", "세그먼트 자산 덮어씀");
            Check(!File.Exists(Path.Combine(cache, "notes.txt")), "wav 아닌 항목 건너뜀");
            Check(AudioPack.Installed(cache) == "kokoro-v016", "표식 기록");
            Check(!Directory.GetFiles(work, "*.zip").Any(), "받은 zip 정리");
            Check(progress.Any(p => p.Stage == UpdateStage.Downloading && p.Item == 2 && p.ItemCount == 2
                                    && p.Total == z2.Length && p.Done == z2.Length), "다운로드 진행률 (바이트/전체)");
            Check(progress.Any(p => p.Stage == UpdateStage.Extracting && p.Item == 1 && p.Done == 3 && p.Total == 3),
                  "풀기 진행률");
            Check(progress.Last().Stage == UpdateStage.Done, "완료 보고");
            Check(!new UpdateCheck(good, "0.16.0", AudioPack.Installed(cache)).AudioUpdateAvailable, "설치 후 업데이트 없음");

            // 팩 zip의 zip-slip → 거부, 표식은 미완료로 남아 다시 권유
            string cache2 = NewDir("cache2");
            byte[] slip = ZipBytes(("ok.wav", B("ok")), ("../escape.wav", B("x")));
            var dl2 = new FakeDownloader();
            dl2.Files[Base + "/download/v1/s.zip"] = slip;
            ReleaseManifest sm = ReleaseManifest.Parse(
                "format=1\nversion=0.16.0\ntag=v1\nplugin_asset=p.zip\nplugin_sha256=" + ShaA + "\n"
                + "audio_pack=kokoro-v016\naudio_tag=v1\naudio_assets=s.zip\naudio_sha256=" + Sha256Util.OfBytes(slip) + "\n");
            Throws(UpdateErrorKind.BadPackage, () => new UpdateService(dl2, Base).InstallAudioPack(sm, cache2,
                NewDir("work2"), null, CancellationToken.None), "음성 팩 zip-slip");
            Check(!File.Exists(Path.Combine(Path.GetDirectoryName(cache2), "escape.wav")), "음성 팩 zip-slip: 밖에 안 씀");
            Check(AudioPack.NeedsUpdate(sm, cache2), "중단된 설치 → 다시 권유");

            // 풀기 시작 후 취소는 무시 — 반쯤 섞인 팩을 남기지 않는다
            string cache4 = NewDir("cache4");
            var cts4 = new CancellationTokenSource();
            new UpdateService(dl, Base).InstallAudioPack(good, cache4, NewDir("work4"), p =>
            {
                if (p.Stage == UpdateStage.Extracting)
                    cts4.Cancel();
            }, cts4.Token);
            Check(cts4.IsCancellationRequested && AudioPack.Installed(cache4) == "kokoro-v016"
                  && File.Exists(Path.Combine(cache4, "b1.wav")), "풀기 중 취소 → 끝까지 설치");

            // 캐시 폴더가 없으면 만든다
            string cache3 = Path.Combine(NewDir("cache3"), "audio_cache");
            new UpdateService(dl, Base).InstallAudioPack(good, cache3, NewDir("work3"), null, CancellationToken.None);
            Check(AudioPack.Installed(cache3) == "kokoro-v016", "새 캐시 폴더에 설치");

            Throws(UpdateErrorKind.BadManifest, () => svc.InstallAudioPack(ReleaseManifest.Parse(
                "format=1\nversion=0.16.0\ntag=v1\nplugin_asset=p.zip\nplugin_sha256=" + ShaA), cache, work, null,
                CancellationToken.None), "팩 없는 매니페스트로 설치");
        }

        // -- 확인 주기 ---------------------------------------------------------

        private static void Schedule()
        {
            var now = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
            Check(UpdateService.IsCheckDue(null, now) && UpdateService.IsCheckDue("", now), "기록 없음 → 확인");
            Check(UpdateService.IsCheckDue("garbage", now), "해석 불가 → 확인");
            Check(!UpdateService.IsCheckDue(UpdateService.FormatCheckTime(now.AddHours(-1)), now), "1시간 전 → 안 함");
            Check(!UpdateService.IsCheckDue(UpdateService.FormatCheckTime(now), now), "방금 → 안 함");
            Check(UpdateService.IsCheckDue(UpdateService.FormatCheckTime(now.AddHours(-25)), now), "25시간 전 → 확인");
            Check(UpdateService.IsCheckDue(UpdateService.FormatCheckTime(now.AddDays(-1)), now), "정확히 하루 → 확인");
            Check(UpdateService.IsCheckDue(UpdateService.FormatCheckTime(now.AddHours(2)), now), "미래 시각(시계 변경) → 확인");
        }

        // -- 컨트롤러 ----------------------------------------------------------

        private static void Controller()
        {
            TimeSpan wait = TimeSpan.FromSeconds(10);
            byte[] plugin = ZipBytes((CoreDll, B("MZ new core")), (SimHubDll, B("MZ new simhub")));
            byte[] pack = ZipBytes(("x1.wav", B("X1")));
            string manifest = "format=1\nversion=0.16.0\ntag=v0.16.0\n"
                + "plugin_asset=teamradio56-plugin-0.16.0.zip\nplugin_sha256=" + Sha256Util.OfBytes(plugin) + "\n"
                + "audio_pack=kokoro-v016\naudio_tag=v0.16.0\naudio_assets=pk.zip\n"
                + "audio_sha256=" + Sha256Util.OfBytes(pack) + "\naudio_size=" + pack.Length + "\n";
            string latestUrl = Base + "/latest/download/teamradio56-release.txt";

            string dir = PluginDir();
            string cache = NewDir("ctl-cache");
            MakeSegments(cache);
            var dl = new FakeDownloader();
            dl.Files[latestUrl] = B(manifest);
            dl.Files[PluginUrl] = plugin;
            dl.Files[Base + "/download/v0.16.0/pk.zip"] = pack;
            var ctl = new UpdateController(new UpdateService(dl, Base), "0.15.0-kokoro", dir, () => cache);
            var attempts = new List<DateTime>();
            int changed = 0;
            ctl.CheckAttempted += t => { lock (attempts) attempts.Add(t); };
            ctl.Changed += () => Interlocked.Increment(ref changed);

            Check(!ctl.State.Busy && ctl.State.Check == null, "처음: 대기, 확인 결과 없음");
            Check(!ctl.StartInstallPlugin() && !ctl.StartInstallAudio(), "확인 전엔 설치 거부");

            // 확인 중엔 다른 작업 거부, 취소하면 기록하지 않음
            dl.Gate = new ManualResetEventSlim(false);
            Check(ctl.StartCheck(), "확인 시작");
            Check(ctl.State.Busy && ctl.State.Activity == UpdateActivity.Checking, "확인 중 상태 (동기 반영)");
            Check(!ctl.StartCheck() && !ctl.StartInstallPlugin(), "작업 중 중복 시작 거부");
            ctl.Cancel();
            Check(ctl.Wait(wait), "취소 후 종료");
            Check(!ctl.State.Busy && ctl.State.ErrorKind == UpdateErrorKind.Cancelled
                  && ctl.State.FailedActivity == UpdateActivity.Checking, "취소 → Cancelled");
            Check(attempts.Count == 0, "취소한 확인은 마지막 확인 시각에 안 남김");
            dl.Gate = null;

            // 시작 지연 중 취소 → 즉시 끝남
            Check(ctl.StartCheck(TimeSpan.FromMinutes(5)), "지연 확인 시작");
            ctl.Cancel();
            Check(ctl.Wait(TimeSpan.FromSeconds(3)) && ctl.State.ErrorKind == UpdateErrorKind.Cancelled,
                  "지연 중 취소 즉시 반영");
            Check(!dl.Requests.Contains(latestUrl), "지연 중 취소 → 요청 안 함");

            // 정상 확인
            Check(ctl.StartCheck() && ctl.Wait(wait), "확인 완료");
            UpdateState st = ctl.State;
            Check(st.ErrorKind == null && st.Check != null && st.CheckedAtUtc.HasValue, "확인 결과 저장, 오류 지움");
            Check(st.Check.PluginUpdateAvailable && st.CanInstallPlugin, "플러그인 업데이트 가능");
            Check(st.Check.InstalledAudioPack == AudioPack.LegacyPackId && st.CanInstallAudio,
                  "수동 설치 팩 감지 → 음성 팩 업데이트 가능");
            Check(attempts.Count == 1, "확인 시각 기록 1회");
            Check(Volatile.Read(ref changed) > 0, "상태 변경 알림");

            // 플러그인 설치 → 재시작 대기, 다시 설치 불가
            Check(ctl.StartInstallPlugin() && ctl.Wait(wait), "플러그인 설치 실행");
            st = ctl.State;
            Check(st.ErrorKind == null && st.RestartPendingVersion == "0.16.0" && !st.CanInstallPlugin,
                  "설치 후 재시작 대기, 재설치 버튼 꺼짐");
            Check(Text(Path.Combine(dir, CoreDll)) == "MZ new core" && File.Exists(Path.Combine(dir, CoreDll + ".old")),
                  "컨트롤러 경유 DLL 교체");
            Check(st.Progress == null, "끝나면 진행률 비움");

            // 음성 팩 설치 → 확인 결과 갱신
            Check(ctl.StartInstallAudio() && ctl.Wait(wait), "음성 팩 설치 실행");
            st = ctl.State;
            Check(st.ErrorKind == null && st.AudioInstalledPack == "kokoro-v016"
                  && !st.Check.AudioUpdateAvailable && !st.CanInstallAudio, "음성 팩 설치 후 업데이트 없음");
            Check(Text(Path.Combine(cache, "x1.wav")) == "X1" && AudioPack.Installed(cache) == "kokoro-v016",
                  "컨트롤러 경유 팩 설치");
            Check(st.RestartPending, "재시작 대기 표시 유지");

            // 실패는 상태로만 — 예외가 밖으로 나오지 않음, 플러그인 그대로
            string dir2 = PluginDir();
            var dl2 = new FakeDownloader();
            dl2.Files[latestUrl] = B(manifest);
            dl2.Files[PluginUrl] = ZipBytes((CoreDll, B("MZ tampered")), (SimHubDll, B("MZ x")));
            var ctl2 = new UpdateController(new UpdateService(dl2, Base), "0.15.0", dir2, () => null);
            Check(ctl2.StartCheck() && ctl2.Wait(wait) && ctl2.State.Check.AudioUpdateAvailable,
                  "캐시 폴더 없음 → 팩 업데이트 권유");
            Check(ctl2.StartInstallPlugin() && ctl2.Wait(wait), "변조 zip 설치 시도");
            Check(ctl2.State.ErrorKind == UpdateErrorKind.Integrity
                  && ctl2.State.FailedActivity == UpdateActivity.InstallingPlugin
                  && !ctl2.State.RestartPending && ctl2.State.CanInstallPlugin, "sha 불일치 → 상태에 기록, 재시도 가능");
            Check(Untouched(dir2), "컨트롤러 실패: 플러그인 그대로");
            Check(ctl2.StartInstallAudio() && ctl2.Wait(wait) && ctl2.State.ErrorKind == UpdateErrorKind.Io,
                  "캐시 폴더를 못 정하면 Io 오류");

            // 풀기 도중 실패 → 확인 결과의 설치된 팩을 '미완료'로 갱신
            string cache5 = NewDir("ctl-cache5");
            MakeSegments(cache5);
            byte[] slipPack = ZipBytes(("ok.wav", B("ok")), ("../escape.wav", B("x")));
            var dl5 = new FakeDownloader();
            dl5.Files[latestUrl] = B(manifest.Replace(Sha256Util.OfBytes(pack), Sha256Util.OfBytes(slipPack)));
            dl5.Files[Base + "/download/v0.16.0/pk.zip"] = slipPack;
            var ctl5 = new UpdateController(new UpdateService(dl5, Base), "0.16.0", PluginDir(), () => cache5);
            Check(ctl5.StartCheck() && ctl5.Wait(wait) && ctl5.State.Check.InstalledAudioPack == AudioPack.LegacyPackId,
                  "확인: 수동 설치 팩");
            Check(!ctl5.State.CanCancel, "대기 중엔 취소 버튼 없음");
            Check(ctl5.StartInstallAudio() && ctl5.Wait(wait) && ctl5.State.ErrorKind == UpdateErrorKind.BadPackage,
                  "풀기 도중 실패");
            Check(ctl5.State.Check.InstalledAudioPack != null
                  && ctl5.State.Check.InstalledAudioPack.StartsWith(AudioPack.IncompletePrefix)
                  && ctl5.State.CanInstallAudio, "실패 후 설치된 팩 = 미완료, 다시 설치 가능");

            // 되돌리기 실패 → 수동 복구 안내 유지, 재설치 막음
            string dir6 = PluginDir();
            var dl6 = new FakeDownloader();
            dl6.Files[latestUrl] = B(manifest);
            dl6.Files[PluginUrl] = plugin;
            string live6 = Path.Combine(dir6, SimHubDll);
            string staged6 = Path.Combine(UpdateService.DefaultWorkDir(dir6), "staging", CoreDll);
            var ops6 = new FaultyFileOps();
            FaultyFileOps ref6 = ops6;
            ops6.FailMoveTo = dst => (ref6.Moves == 4 && dst == live6) || (ref6.Moves > 4 && dst == staged6);
            var ctl6 = new UpdateController(new UpdateService(dl6, Base, ops6), "0.15.0", dir6, () => cache);
            Check(ctl6.StartCheck() && ctl6.Wait(wait) && ctl6.StartInstallPlugin() && ctl6.Wait(wait),
                  "되돌리기 실패 설치 시도");
            Check(ctl6.State.ErrorKind == UpdateErrorKind.RollbackFailed && ctl6.State.ManualRestorePending
                  && ctl6.State.ManualRestore.Count == 1 && !ctl6.State.CanInstallPlugin,
                  "되돌리기 실패: 수동 복구 대기, 재설치 막음");
            Check(ctl6.StartCheck() && ctl6.Wait(wait) && ctl6.State.ManualRestorePending && ctl6.State.ErrorKind == null,
                  "다시 확인해도 수동 복구 안내 유지");

            // 오프라인 확인 실패도 시도로 기록 (하루 한 번 원칙)
            dl2.FailAll = true;
            var attempts2 = 0;
            ctl2.CheckAttempted += _ => Interlocked.Increment(ref attempts2);
            Check(ctl2.StartCheck() && ctl2.Wait(wait), "오프라인 확인");
            Check(ctl2.State.ErrorKind == UpdateErrorKind.Network && ctl2.State.Check != null,
                  "오프라인 → Network, 이전 확인 결과 유지");
            Check(attempts2 == 1, "실패한 확인도 시도로 기록");

            // 작업 폴더: 플러그인 폴더에 못 만들면 %TEMP%
            Check(ctl.WorkDir() == UpdateService.DefaultWorkDir(dir), "작업 폴더 기본: 플러그인 폴더 아래");
            string notADir = Path.Combine(NewDir("ctl-file"), "plugin.bin");
            File.WriteAllText(notADir, "x");
            var ctl3 = new UpdateController(new UpdateService(dl, Base), "0.15.0", notADir, () => cache);
            Check(ctl3.WorkDir() == UpdateController.TempWorkDir(), "작업 폴더를 못 만들면 TEMP로");

            // 종료 후엔 새 작업 거부
            ctl.Shutdown(TimeSpan.FromSeconds(1));
            Check(!ctl.StartCheck(), "종료 후 새 작업 거부");

            // 알림 처리기가 던져도 작업은 끝까지
            var ctl4 = new UpdateController(new UpdateService(dl, Base), "0.15.0", PluginDir(), () => cache);
            ctl4.Changed += () => throw new InvalidOperationException("UI 오류 흉내");
            Check(ctl4.StartCheck() && ctl4.Wait(wait) && ctl4.State.Check != null && !ctl4.State.Busy,
                  "알림 처리기 예외 무시");
        }

        // -- HTTP ----------------------------------------------------------

        private static void Http()
        {
            int port;
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            var listener = new HttpListener();
            string prefix = "http://127.0.0.1:" + port + "/";
            listener.Prefixes.Add(prefix);
            try
            {
                listener.Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine("    ⏭  로컬 HTTP 서버를 못 띄움 — 건너뜀: " + ex.Message);
                return;
            }

            var blob = new byte[300_000];
            new Random(5).NextBytes(blob);
            var agents = new List<string>();
            var server = new Thread(() =>
            {
                while (listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = listener.GetContext(); }
                    catch (Exception) { return; }
                    try
                    {
                        lock (agents) agents.Add(ctx.Request.UserAgent ?? "");
                        string path = ctx.Request.Url.AbsolutePath;
                        HttpListenerResponse r = ctx.Response;
                        if (path == "/latest/download/teamradio56-release.txt")
                        {
                            r.StatusCode = 302;
                            r.RedirectLocation = prefix + "objects/manifest";
                            r.Close();
                        }
                        else if (path == "/objects/manifest")
                        {
                            byte[] body = B("format=1\nversion=0.16.0\n");
                            r.ContentLength64 = body.Length;
                            r.OutputStream.Write(body, 0, body.Length);
                            r.Close();
                        }
                        else if (path == "/download/v1/blob.zip")
                        {
                            r.StatusCode = 302;
                            r.RedirectLocation = prefix + "objects/blob";
                            r.Close();
                        }
                        else if (path == "/objects/blob")
                        {
                            r.ContentLength64 = blob.Length;
                            r.OutputStream.Write(blob, 0, blob.Length);
                            r.Close();
                        }
                        else if (path == "/error")
                        {
                            r.StatusCode = 500;
                            r.Close();
                        }
                        else
                        {
                            r.StatusCode = 404;
                            r.Close();
                        }
                    }
                    catch (Exception) { }
                }
            }) { IsBackground = true };
            server.Start();

            try
            {
                var http = new HttpDownloader { UseSystemProxy = false, TimeoutMs = 10000 };
                string root = prefix.TrimEnd('/');
                string text = http.DownloadText(root + "/latest/download/teamradio56-release.txt", CancellationToken.None);
                Check(text == "format=1\nversion=0.16.0\n", "리다이렉트 따라 텍스트 받음");
                lock (agents)
                    Check(agents.Count >= 2 && agents.All(a => a == "teamradio56-updater"), "User-Agent 전송 (리다이렉트 후 포함)");

                string dir = NewDir("http");
                string dest = Path.Combine(dir, "blob.zip");
                long lastDone = -1, lastTotal = -2;
                http.DownloadFile(root + "/download/v1/blob.zip", dest, (d, t) => { lastDone = d; lastTotal = t; },
                                  CancellationToken.None);
                Check(File.Exists(dest) && Sha256Util.OfFile(dest) == Sha256Util.OfBytes(blob), "파일 다운로드 내용 일치");
                Check(lastDone == blob.Length && lastTotal == blob.Length, "다운로드 진행률 끝값");
                Check(!File.Exists(dest + ".part"), ".part 정리");

                Throws(UpdateErrorKind.NotFound, () => http.DownloadFile(root + "/missing", Path.Combine(dir, "m.zip"),
                    null, CancellationToken.None), "HTTP 404");
                Check(!File.Exists(Path.Combine(dir, "m.zip")) && !File.Exists(Path.Combine(dir, "m.zip.part")),
                      "404: 파일 안 남김");
                Throws(UpdateErrorKind.Network, () => http.DownloadText(root + "/error", CancellationToken.None), "HTTP 500");

                var cts = new CancellationTokenSource();
                cts.Cancel();
                try
                {
                    http.DownloadText(root + "/objects/manifest", cts.Token);
                    Failures.Add("취소된 토큰인데 받음");
                }
                catch (OperationCanceledException)
                {
                    _pass++;
                }

                // 서비스 경유: 실제 다운로더로 매니페스트 → 파싱 오류 분류
                Throws(UpdateErrorKind.BadManifest, () => new UpdateService(http, root).FetchLatest(CancellationToken.None),
                    "필수 키 빠진 매니페스트");
            }
            finally
            {
                listener.Stop();
                listener.Close();
            }

            // 닫힌 포트 → 네트워크 오류 (SimHub를 죽이지 않고 분류된 예외)
            var offline = new HttpDownloader { UseSystemProxy = false, TimeoutMs = 5000 };
            Throws(UpdateErrorKind.Network, () => offline.DownloadText("http://127.0.0.1:" + port + "/x", CancellationToken.None),
                "연결 거부");
        }

        // -- 가짜 구현 ---------------------------------------------------------

        private sealed class FakeDownloader : IDownloader
        {
            public readonly Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>();
            public readonly List<string> Requests = new List<string>();
            public bool FailAll;
            /// <summary>설정하면 요청이 이 신호를 기다린다 — 작업 중 상태를 붙잡아 두는 용도.</summary>
            public ManualResetEventSlim Gate;

            private byte[] Get(string url, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                Gate?.Wait(ct);
                Requests.Add(url);
                if (FailAll)
                    throw new UpdateException(UpdateErrorKind.Network, "오프라인 (가짜)");
                if (!Files.TryGetValue(url, out byte[] data))
                    throw new UpdateException(UpdateErrorKind.NotFound, "404 (가짜): " + url);
                return data;
            }

            public string DownloadText(string url, CancellationToken ct) => Encoding.UTF8.GetString(Get(url, ct));

            public void DownloadFile(string url, string destPath, Action<long, long> progress, CancellationToken ct)
            {
                byte[] data = Get(url, ct);
                File.WriteAllBytes(destPath + ".part", data);
                progress?.Invoke(data.Length / 2, data.Length);
                progress?.Invoke(data.Length, data.Length);
                if (File.Exists(destPath))
                    File.Delete(destPath);
                File.Move(destPath + ".part", destPath);
            }
        }

        /// <summary>잠긴 파일(삭제·이동 불가)과 특정 이동 실패를 흉내 내는 파일 조작.</summary>
        private sealed class FaultyFileOps : IFileOps
        {
            public readonly HashSet<string> Locked = new HashSet<string>();
            public Func<string, bool> FailMoveTo = _ => false;
            public bool Denied;
            public int Moves;

            public bool Exists(string path) => File.Exists(path);

            public void Move(string src, string dst)
            {
                Moves++;
                if (FailMoveTo(dst))
                {
                    if (Denied)
                        throw new UnauthorizedAccessException("가짜 권한 없음: " + dst);
                    throw new IOException("가짜 이동 실패: " + dst);
                }
                if (Locked.Contains(src))
                    throw new IOException("가짜 잠금: " + src);
                File.Move(src, dst);
            }

            public void Delete(string path)
            {
                if (Locked.Contains(path))
                    throw new IOException("가짜 잠금: " + path);
                File.Delete(path);
            }

            public string[] List(string dir) => Directory.GetFiles(dir);
        }
    }
}
