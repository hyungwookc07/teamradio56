using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace TeamRadio56.Core.Update
{
    public enum UpdateStage { Downloading, Verifying, Extracting, Installing, Done }

    /// <summary>진행 상황 — 문구는 UI(Loc)가 만든다.</summary>
    public sealed class UpdateProgress
    {
        public UpdateStage Stage { get; }
        public int Item { get; }            // 1부터
        public int ItemCount { get; }
        public string Name { get; }
        public long Done { get; }           // 다운로드 바이트 / 푼 파일 수
        public long Total { get; }          // 모르면 -1

        public UpdateProgress(UpdateStage stage, int item, int itemCount, string name,
                              long done, long total)
        {
            Stage = stage;
            Item = item;
            ItemCount = itemCount;
            Name = name;
            Done = done;
            Total = total;
        }
    }

    public sealed class UpdateCheck
    {
        public ReleaseManifest Latest { get; }
        public string CurrentVersion { get; }
        public bool PluginUpdateAvailable { get; }
        /// <summary>설치된 음성 팩 ID (없으면 null).</summary>
        public string InstalledAudioPack { get; }
        public bool AudioUpdateAvailable { get; }

        public UpdateCheck(ReleaseManifest latest, string currentVersion, string installedPack)
        {
            Latest = latest;
            CurrentVersion = currentVersion;
            PluginUpdateAvailable = PluginVersion.IsNewer(latest.Version, currentVersion);
            InstalledAudioPack = installedPack;
            AudioUpdateAvailable = latest.HasAudioPack
                && !string.Equals(installedPack, latest.AudioPack, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 업데이트 확인·설치. 자동 설치는 하지 않는다 — 호출부(UI)가 사용자 확인 후 부른다.
    /// 모든 메서드는 블로킹이므로 UI 스레드 밖에서 호출할 것.
    /// </summary>
    public sealed class UpdateService
    {
        public const string DefaultReleasesUrl = "https://github.com/hyungwookc07/teamradio56/releases";
        public const string WorkDirName = "teamradio56_update";
        public static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

        private readonly IDownloader _downloader;
        private readonly string _base;
        private readonly IFileOps _ops;

        public UpdateService(IDownloader downloader, string releasesBaseUrl = DefaultReleasesUrl,
                             IFileOps ops = null)
        {
            _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
            _base = (releasesBaseUrl ?? DefaultReleasesUrl).TrimEnd('/');
            _ops = ops ?? RealFileOps.Instance;
        }

        public string LatestManifestUrl => _base + "/latest/download/" + ReleaseManifest.FileName;

        public string AssetUrl(string tag, string asset) =>
            _base + "/download/" + Uri.EscapeDataString(tag) + "/" + Uri.EscapeDataString(asset);

        public ReleaseManifest FetchLatest(CancellationToken ct)
        {
            string text;
            try
            {
                text = _downloader.DownloadText(LatestManifestUrl, ct);
            }
            catch (Exception ex)
            {
                throw UpdateException.From(ex, "매니페스트 받기");
            }
            return ReleaseManifest.Parse(text);
        }

        public UpdateCheck Check(string currentVersion, string cacheDir, CancellationToken ct)
        {
            ReleaseManifest m = FetchLatest(ct);
            string installed = null;
            try
            {
                installed = AudioPack.Installed(cacheDir);
            }
            catch (Exception) { }
            return new UpdateCheck(m, currentVersion, installed);
        }

        /// <summary>
        /// 플러그인 DLL 교체. 성공하면 SimHub 재시작 후 적용된다.
        /// zip 검증(sha256, DLL 2개 존재)이 끝나기 전엔 플러그인 폴더를 건드리지 않는다.
        /// </summary>
        public void InstallPlugin(ReleaseManifest m, string pluginDir, string workDir,
                                  Action<UpdateProgress> progress, CancellationToken ct)
        {
            Guard("플러그인 업데이트", () => InstallPluginCore(m, pluginDir, workDir, progress, ct));
        }

        private void InstallPluginCore(ReleaseManifest m, string pluginDir, string workDir,
                                       Action<UpdateProgress> progress, CancellationToken ct)
        {
            string zip = Path.Combine(workDir, m.PluginAsset);
            string staging = Path.Combine(workDir, "staging");
            Guard("작업 폴더 준비", () => Directory.CreateDirectory(workDir));
            FetchVerified(AssetUrl(m.Tag, m.PluginAsset), zip, m.PluginSha256, m.PluginAsset,
                          1, 1, progress, ct);

            progress?.Invoke(new UpdateProgress(UpdateStage.Extracting, 1, 1, m.PluginAsset, 0, -1));
            Guard("스테이징", () =>
            {
                if (Directory.Exists(staging))
                    Directory.Delete(staging, true);
                SafeZip.ExtractRequired(zip, staging, PluginSwap.DllNames);
                foreach (string name in PluginSwap.DllNames)
                    RequirePeImage(Path.Combine(staging, name));
            });
            ct.ThrowIfCancellationRequested();
            ProbeWritable(pluginDir);

            // 여기부터는 취소하지 않는다 — 교체는 짧고, 중간 중단은 롤백으로만 처리
            progress?.Invoke(new UpdateProgress(UpdateStage.Installing, 1, 1, m.PluginAsset, 0, -1));
            IList<SwapStep> steps;
            try
            {
                steps = PluginSwap.Install(pluginDir, staging, PluginSwap.DllNames, _ops);
            }
            catch (UpdateException ue) when (ue.Kind == UpdateErrorKind.RollbackFailed)
            {
                PluginSwap.WriteBackupRecord(pluginDir, m.Version, ue.ManualRestore);
                throw;
            }
            PluginSwap.WriteBackupRecord(pluginDir, m.Version, PluginSwap.BackupPairs(steps));

            TryDeleteDir(staging);
            TryDeleteFile(zip);
            progress?.Invoke(new UpdateProgress(UpdateStage.Done, 1, 1, m.PluginAsset, 1, 1));
        }

        /// <summary>
        /// 음성 팩 설치: 전부 받고 검증한 뒤에 풀고, 표식은 마지막에 쓴다.
        /// 이미 받아 둔 zip이 sha와 맞으면 다시 받지 않는다 (재시도 시 이어받기 대용).
        /// </summary>
        public void InstallAudioPack(ReleaseManifest m, string cacheDir, string workDir,
                                     Action<UpdateProgress> progress, CancellationToken ct)
        {
            Guard("음성 팩 업데이트", () => InstallAudioPackCore(m, cacheDir, workDir, progress, ct));
        }

        private void InstallAudioPackCore(ReleaseManifest m, string cacheDir, string workDir,
                                          Action<UpdateProgress> progress, CancellationToken ct)
        {
            if (!m.HasAudioPack)
                throw new UpdateException(UpdateErrorKind.BadManifest, "매니페스트에 음성 팩이 없음");
            Guard("작업 폴더 준비", () => Directory.CreateDirectory(workDir));
            Guard("오디오 캐시 폴더 준비", () => Directory.CreateDirectory(cacheDir));
            ProbeWritable(cacheDir);

            int n = m.AudioAssets.Count;
            var zips = new List<string>();
            for (int i = 0; i < n; i++)
            {
                AudioAsset a = m.AudioAssets[i];
                string zip = Path.Combine(workDir, a.Name);
                FetchVerified(AssetUrl(m.AudioTag, a.Name), zip, a.Sha256, a.Name, i + 1, n,
                              progress, ct);
                zips.Add(zip);
            }

            // 여기부터는 취소하지 않는다 — 풀기는 로컬이라 몇 초면 끝나고, 중간에 멈추면
            // 두 팩이 섞인 캐시가 남는다
            ct.ThrowIfCancellationRequested();
            Guard("음성 팩 표식", () => AudioPack.WriteMarker(cacheDir, AudioPack.IncompletePrefix + m.AudioPack));
            for (int i = 0; i < n; i++)
            {
                int item = i + 1;
                string name = m.AudioAssets[i].Name;
                progress?.Invoke(new UpdateProgress(UpdateStage.Extracting, item, n, name, 0, -1));
                Guard("음성 팩 풀기", () => SafeZip.ExtractRootFiles(zips[item - 1], cacheDir, ".wav",
                    (done, total) => progress?.Invoke(
                        new UpdateProgress(UpdateStage.Extracting, item, n, name, done, total))));
            }
            Guard("음성 팩 표식", () => AudioPack.WriteMarker(cacheDir, m.AudioPack));
            foreach (string zip in zips)
                TryDeleteFile(zip);
            progress?.Invoke(new UpdateProgress(UpdateStage.Done, n, n, m.AudioPack, 1, 1));
        }

        private void FetchVerified(string url, string dest, string sha, string name,
                                   int item, int count, Action<UpdateProgress> progress,
                                   CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            bool cached;
            try
            {
                cached = Sha256Util.FileMatches(dest, sha);
            }
            catch (Exception)
            {
                cached = false;
            }
            if (!cached)
            {
                try
                {
                    _downloader.DownloadFile(url, dest,
                        (done, total) => progress?.Invoke(
                            new UpdateProgress(UpdateStage.Downloading, item, count, name, done, total)),
                        ct);
                }
                catch (Exception ex)
                {
                    throw UpdateException.From(ex, "다운로드");
                }
            }
            progress?.Invoke(new UpdateProgress(UpdateStage.Verifying, item, count, name, 0, -1));
            try
            {
                Sha256Util.Verify(dest, sha);
            }
            catch (Exception ex)
            {
                TryDeleteFile(dest);
                throw UpdateException.From(ex, "검증");
            }
        }

        private static void RequirePeImage(string path)
        {
            var head = new byte[2];
            using (var fs = File.OpenRead(path))
                if (fs.Read(head, 0, 2) != 2 || head[0] != (byte)'M' || head[1] != (byte)'Z')
                    throw new UpdateException(UpdateErrorKind.BadPackage,
                        Path.GetFileName(path) + "이(가) DLL이 아님");
        }

        /// <summary>쓰기 권한을 미리 확인 — 교체 도중에 권한 문제로 실패하지 않게.</summary>
        private static void ProbeWritable(string dir)
        {
            string probe = Path.Combine(dir, "teamradio56_write_probe.tmp");
            Guard("쓰기 확인 (" + dir + ")", () =>
            {
                File.WriteAllText(probe, "");
                File.Delete(probe);
            });
        }

        private static void Guard(string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                throw UpdateException.From(ex, what);
            }
        }

        // -- 시작 시 정리 / 확인 주기 -------------------------------------

        public static string DefaultWorkDir(string pluginDir) => Path.Combine(pluginDir, WorkDirName);

        /// <summary>플러그인 Init에서 호출: .old DLL과 남은 작업 폴더 정리 (실패 무시).</summary>
        public static int CleanupOnStartup(string pluginDir, IFileOps ops = null)
        {
            int removed = PluginSwap.CleanupLeftovers(pluginDir, ops ?? RealFileOps.Instance);
            TryDeleteDir(DefaultWorkDir(pluginDir));
            return removed;
        }

        /// <summary>마지막 확인(왕복 형식 문자열)에서 하루 지났으면 true. 해석 불가·미래 시각도 true.</summary>
        public static bool IsCheckDue(string lastCheck, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(lastCheck)
                || !DateTime.TryParse(lastCheck, CultureInfo.InvariantCulture,
                                      DateTimeStyles.RoundtripKind, out DateTime last))
                return true;
            last = last.ToUniversalTime();
            TimeSpan elapsed = nowUtc - last;
            return elapsed < TimeSpan.Zero || elapsed >= CheckInterval;
        }

        public static string FormatCheckTime(DateTime nowUtc) =>
            nowUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception) { }
        }

        private static void TryDeleteDir(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch (Exception) { }
        }
    }
}
