using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace TeamRadio56.Core.Update
{
    /// <summary>
    /// 릴리스 zip 풀기. 대상 폴더 밖을 가리키는 항목(zip-slip)이 하나라도 있으면
    /// 아무것도 쓰기 전에 zip 전체를 거부한다. 루트에 있는 파일만 다룬다.
    /// </summary>
    public static class SafeZip
    {
        private static readonly char[] BadChars = { ':', '*', '?', '"', '<', '>', '|' };

        /// <summary>
        /// 항목 이름 검사 — 탈출/절대 경로/드라이브/금지 문자면 예외.
        /// 루트 파일이면 파일명, 하위 폴더 항목이나 폴더 항목이면 null.
        /// </summary>
        public static string RootFileName(string entryName)
        {
            if (string.IsNullOrEmpty(entryName))
                throw Reject(entryName, "빈 이름");
            string n = entryName.Replace('\\', '/');
            if (n[0] == '/')
                throw Reject(entryName, "절대 경로");
            foreach (char c in n)
                if (c < 0x20 || Array.IndexOf(BadChars, c) >= 0)
                    throw Reject(entryName, "허용되지 않는 문자");
            string[] parts = n.Split('/');
            foreach (string p in parts)
                if (p == ".." || p.Trim() == "..")
                    throw Reject(entryName, "상위 폴더 경로");
            if (parts.Length != 1)
                return null;               // 하위 폴더 항목 또는 "dir/"
            if (n == "." || n.Trim().Length == 0 || n.EndsWith(".") || n.EndsWith(" "))
                throw Reject(entryName, "잘못된 파일명");
            return n;
        }

        /// <summary>
        /// 루트의 extension 파일만 targetDir에 덮어써 푼다 (그 밖의 항목은 건너뜀).
        /// 반환: 푼 파일 수. progress(done, total).
        /// </summary>
        public static int ExtractRootFiles(string zipPath, string targetDir, string extension,
                                           Action<int, int> progress = null)
        {
            Directory.CreateDirectory(targetDir);
            string root = FullDir(targetDir);
            using (var fs = File.OpenRead(zipPath))
            using (var zip = Open(fs, zipPath))
            {
                var picked = new List<KeyValuePair<ZipArchiveEntry, string>>();
                foreach (ZipArchiveEntry e in zip.Entries)
                {
                    string name = RootFileName(e.FullName);
                    if (name == null)
                        continue;
                    string dest = Path.Combine(root, name);
                    if (!IsInside(root, dest))
                        throw Reject(e.FullName, "대상 폴더 밖");
                    if (extension != null
                        && !name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                        continue;
                    picked.Add(new KeyValuePair<ZipArchiveEntry, string>(e, dest));
                }
                int done = 0;
                foreach (var kv in picked)
                {
                    WriteEntry(kv.Key, kv.Value);
                    progress?.Invoke(++done, picked.Count);
                }
                return picked.Count;
            }
        }

        /// <summary>
        /// 플러그인 zip에서 필수 파일(names)을 루트에서 찾아 stagingDir에 푼다.
        /// 하나라도 없거나 비었으면 아무것도 풀지 않고 BadPackage.
        /// </summary>
        public static void ExtractRequired(string zipPath, string stagingDir,
                                           IReadOnlyList<string> names)
        {
            string root = FullDir(stagingDir);
            using (var fs = File.OpenRead(zipPath))
            using (var zip = Open(fs, zipPath))
            {
                var found = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (ZipArchiveEntry e in zip.Entries)
                {
                    string name = RootFileName(e.FullName);
                    if (name != null)
                        found[name] = e;
                }
                foreach (string want in names)
                {
                    if (!found.TryGetValue(want, out ZipArchiveEntry e))
                        throw new UpdateException(UpdateErrorKind.BadPackage,
                            "플러그인 zip 루트에 " + want + " 없음");
                    if (e.Length <= 0)
                        throw new UpdateException(UpdateErrorKind.BadPackage,
                            "플러그인 zip의 " + want + "이(가) 비었음");
                }
                Directory.CreateDirectory(root);
                foreach (string want in names)
                    WriteEntry(found[want], Path.Combine(root, want));
            }
        }

        private static ZipArchive Open(Stream fs, string zipPath)
        {
            try
            {
                return new ZipArchive(fs, ZipArchiveMode.Read);
            }
            catch (InvalidDataException ex)
            {
                throw new UpdateException(UpdateErrorKind.BadPackage,
                    "zip을 읽을 수 없음: " + Path.GetFileName(zipPath), ex);
            }
        }

        private static void WriteEntry(ZipArchiveEntry e, string dest)
        {
            // 반쯤 쓰인 파일이 정식 이름으로 남지 않게 .part → 교체
            string tmp = dest + ".part";
            using (Stream src = e.Open())
            using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                src.CopyTo(dst);
            if (File.Exists(dest))
                File.Delete(dest);
            File.Move(tmp, dest);
        }

        private static string FullDir(string dir)
        {
            string full = Path.GetFullPath(dir);
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static bool IsInside(string rootDir, string path)
        {
            string full = Path.GetFullPath(path);
            string dir = Path.GetDirectoryName(full);
            return dir != null && string.Equals(
                dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                rootDir, StringComparison.OrdinalIgnoreCase);
        }

        private static UpdateException Reject(string entry, string why) =>
            new UpdateException(UpdateErrorKind.BadPackage,
                "zip 항목 거부 (" + why + "): " + entry);
    }
}
