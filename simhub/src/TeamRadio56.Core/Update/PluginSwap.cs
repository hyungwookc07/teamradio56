using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace TeamRadio56.Core.Update
{
    /// <summary>파일 조작 추상화 — 셀프테스트가 잠긴 파일·이동 실패를 흉내 낸다.</summary>
    public interface IFileOps
    {
        bool Exists(string path);
        void Move(string src, string dst);
        void Delete(string path);
        string[] List(string dir);
    }

    public sealed class RealFileOps : IFileOps
    {
        public static readonly RealFileOps Instance = new RealFileOps();

        public bool Exists(string path) => File.Exists(path);
        public void Move(string src, string dst) => File.Move(src, dst);
        public void Delete(string path) => File.Delete(path);
        public string[] List(string dir) => Directory.GetFiles(dir);
    }

    public sealed class SwapStep
    {
        public string Name { get; }
        public string LivePath { get; }
        /// <summary>현재 DLL을 옮겨 둘 이름 — 현재 DLL이 없으면 null.</summary>
        public string BackupPath { get; }
        public string StagedPath { get; }

        public SwapStep(string name, string livePath, string backupPath, string stagedPath)
        {
            Name = name;
            LivePath = livePath;
            BackupPath = backupPath;
            StagedPath = stagedPath;
        }
    }

    /// <summary>
    /// 플러그인 DLL 교체. 로드된 DLL은 NTFS에서 덮어쓰기·삭제는 안 되지만 이름 변경은
    /// 된다 → 현재 DLL을 .old로 옮기고 새 DLL을 넣는다. 한 단계라도 실패하면
    /// 그때까지의 이동을 전부 되돌려 플러그인이 반쯤 바뀐 채로 남지 않게 한다.
    /// </summary>
    public static class PluginSwap
    {
        public static readonly string[] DllNames = { "TeamRadio56.Core.dll", "TeamRadio56.SimHub.dll" };

        private const int MaxBackupSlots = 50;

        private static readonly Regex Leftover = new Regex(
            @"^TeamRadio56\.[^\\/]+\.dll\.old\d*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// 백업 이름 고르기: .old가 있으면 지워 보고, 잠겨 있으면(이전 업데이트 후
        /// 재시작 전이라 아직 로드된 DLL) .old1, .old2 ... 를 쓴다.
        /// </summary>
        public static string ChooseBackupPath(string livePath, IFileOps ops)
        {
            for (int i = 0; i < MaxBackupSlots; i++)
            {
                string candidate = livePath + ".old" + (i == 0 ? "" : i.ToString());
                if (!ops.Exists(candidate))
                    return candidate;
                try
                {
                    ops.Delete(candidate);
                    if (!ops.Exists(candidate))
                        return candidate;
                }
                catch (Exception) { }
            }
            throw new UpdateException(UpdateErrorKind.Io,
                "백업 이름을 정할 수 없음 (.old 파일이 너무 많음) — SimHub 재시작 후 다시 시도");
        }

        /// <summary>
        /// 교체 계획. 새 DLL이 전부 스테이징에 있는지 먼저 확인한다 (없으면 아무것도 안 건드림).
        /// 기존 .old 정리 외에는 파일을 건드리지 않는다.
        /// </summary>
        public static IList<SwapStep> Prepare(string pluginDir, string stagingDir,
                                              IReadOnlyList<string> names, IFileOps ops)
        {
            foreach (string name in names)
            {
                string staged = Path.Combine(stagingDir, name);
                if (!ops.Exists(staged))
                    throw new UpdateException(UpdateErrorKind.BadPackage,
                        "스테이징에 " + name + " 없음");
            }
            var steps = new List<SwapStep>();
            foreach (string name in names)
            {
                string live = Path.Combine(pluginDir, name);
                string backup = ops.Exists(live) ? ChooseBackupPath(live, ops) : null;
                steps.Add(new SwapStep(name, live, backup, Path.Combine(stagingDir, name)));
            }
            return steps;
        }

        /// <summary>
        /// 실행. 실패하면 완료된 이동을 역순으로 되돌린 뒤 예외 — 되돌리기까지
        /// 실패하면 수동 복구 방법을 메시지에 담는다.
        /// </summary>
        public static void Execute(IList<SwapStep> steps, IFileOps ops)
        {
            var done = new List<KeyValuePair<string, string>>();   // (원래 위치, 옮긴 위치)
            try
            {
                foreach (SwapStep s in steps)
                {
                    if (s.BackupPath != null)
                    {
                        ops.Move(s.LivePath, s.BackupPath);
                        done.Add(new KeyValuePair<string, string>(s.LivePath, s.BackupPath));
                    }
                    ops.Move(s.StagedPath, s.LivePath);
                    done.Add(new KeyValuePair<string, string>(s.StagedPath, s.LivePath));
                }
            }
            catch (Exception ex)
            {
                var failedUndo = new List<string>();
                for (int i = done.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        ops.Move(done[i].Value, done[i].Key);
                    }
                    catch (Exception)
                    {
                        failedUndo.Add(done[i].Value + " → " + done[i].Key);
                    }
                }
                UpdateException ue = UpdateException.From(ex, "DLL 교체");
                if (failedUndo.Count == 0)
                    throw new UpdateException(ue.Kind,
                        ue.Message + " — 원래 DLL로 되돌렸음", ex);
                throw new UpdateException(UpdateErrorKind.Io,
                    ue.Message + " — 되돌리기 실패, 수동으로 이름을 바꿔 주세요: "
                    + string.Join("; ", failedUndo), ex);
            }
        }

        public static IList<SwapStep> Install(string pluginDir, string stagingDir,
                                              IReadOnlyList<string> names, IFileOps ops)
        {
            IList<SwapStep> steps = Prepare(pluginDir, stagingDir, names, ops);
            Execute(steps, ops);
            return steps;
        }

        /// <summary>플러그인 시작 시 지난 업데이트의 TeamRadio56.*.dll.old* 정리 (실패는 무시).</summary>
        public static int CleanupLeftovers(string pluginDir, IFileOps ops)
        {
            int removed = 0;
            string[] files;
            try
            {
                files = ops.List(pluginDir);
            }
            catch (Exception)
            {
                return 0;
            }
            foreach (string f in files)
            {
                if (!Leftover.IsMatch(Path.GetFileName(f)))
                    continue;
                try
                {
                    ops.Delete(f);
                    removed++;
                }
                catch (Exception) { }
            }
            return removed;
        }
    }
}
