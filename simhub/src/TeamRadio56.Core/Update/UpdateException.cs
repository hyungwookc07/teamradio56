using System;
using System.Collections.Generic;

namespace TeamRadio56.Core.Update
{
    /// <summary>업데이트 실패 분류 — UI가 종류별로 현지화 문구를 고른다.</summary>
    public enum UpdateErrorKind
    {
        Network,            // 오프라인·프록시·타임아웃·서버 오류
        NotFound,           // 릴리스/자산 없음 (404)
        BadManifest,        // 매니페스트 형식 오류
        UnsupportedFormat,  // 이 버전이 모르는 새 매니페스트 형식 — 수동 업데이트 필요
        Integrity,          // sha256 불일치
        BadPackage,         // zip 내용이 규약과 다름 (DLL 누락, 경로 탈출 등)
        AccessDenied,       // 폴더 쓰기 권한 없음
        Io,                 // 그 밖의 디스크 오류 (잠긴 파일 등)
        Cancelled,
        RollbackFailed,     // DLL 교체 실패 후 되돌리기도 실패 — 플러그인 폴더가 섞인 상태, 수동 복구 필요
    }

    public sealed class UpdateException : Exception
    {
        public UpdateErrorKind Kind { get; }

        /// <summary>
        /// RollbackFailed일 때 사용자가 직접 되돌려야 할 (백업 파일, 원래 이름) 쌍 — 전체 경로.
        /// 원래 이름의 파일은 먼저 지워야 한다. 그 밖의 종류에선 빈 목록.
        /// </summary>
        public IReadOnlyList<KeyValuePair<string, string>> ManualRestore { get; }

        public UpdateException(UpdateErrorKind kind, string message, Exception inner = null,
                               IReadOnlyList<KeyValuePair<string, string>> manualRestore = null)
            : base(message, inner)
        {
            Kind = kind;
            ManualRestore = manualRestore ?? new KeyValuePair<string, string>[0];
        }

        /// <summary>디스크/권한/취소 예외를 분류된 UpdateException으로 바꾼다.</summary>
        public static UpdateException From(Exception ex, string what)
        {
            if (ex is UpdateException ue)
                return ue;
            if (ex is OperationCanceledException)
                return new UpdateException(UpdateErrorKind.Cancelled, "취소됨", ex);
            if (ex is UnauthorizedAccessException
                || ex is System.Security.SecurityException)
                return new UpdateException(UpdateErrorKind.AccessDenied,
                    what + ": 쓰기 권한 없음 — " + ex.Message, ex);
            return new UpdateException(UpdateErrorKind.Io, what + ": " + ex.Message, ex);
        }
    }
}
