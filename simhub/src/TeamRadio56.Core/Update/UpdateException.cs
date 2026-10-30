using System;

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
    }

    public sealed class UpdateException : Exception
    {
        public UpdateErrorKind Kind { get; }

        public UpdateException(UpdateErrorKind kind, string message, Exception inner = null)
            : base(message, inner)
        {
            Kind = kind;
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
