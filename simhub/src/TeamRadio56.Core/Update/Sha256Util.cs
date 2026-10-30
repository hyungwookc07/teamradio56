using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TeamRadio56.Core.Update
{
    public static class Sha256Util
    {
        public static string OfFile(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                           FileShare.Read, 1 << 16))
                return Hex(sha.ComputeHash(fs));
        }

        public static string OfBytes(byte[] data)
        {
            using (var sha = SHA256.Create())
                return Hex(sha.ComputeHash(data));
        }

        public static bool IsHex64(string s)
        {
            if (s == null || s.Length != 64)
                return false;
            foreach (char c in s)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                    return false;
            return true;
        }

        public static bool FileMatches(string path, string expected)
        {
            return File.Exists(path)
                && string.Equals(OfFile(path), expected, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>불일치면 Integrity 예외 (파일은 지우지 않는다 — 호출부 몫).</summary>
        public static void Verify(string path, string expected)
        {
            string actual = OfFile(path);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new UpdateException(UpdateErrorKind.Integrity,
                    "sha256 불일치: " + Path.GetFileName(path)
                    + " (기대 " + expected + ", 실제 " + actual + ")");
        }

        private static string Hex(byte[] hash)
        {
            var sb = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
