// SimHub 검증용 스텁 — 실제 SimHub 어셈블리가 없는 환경에서
// 플러그인 코드를 컴파일 검증하기 위한 최소 정의. 배포물에 포함 금지.
namespace GameReaderCommon
{
    public class StatusDataBase
    {
        public double SpeedKmh { get; set; }
        public int CurrentLap { get; set; }
    }

    /// <summary>
    /// 실제 GameReaderCommon에 존재하는 타입 (2026-08 사용자 빌드에서 확인).
    /// 우리 Core에도 같은 이름이 있어 충돌했었다 — 스텁에 넣어두면
    /// 같은 실수를 개발 환경에서 잡는다.
    /// </summary>
    public class SharedMemoryReader { }

    public class GameData
    {
        public string GameName { get; set; }
        public bool GameRunning { get; set; }
        public StatusDataBase NewData { get; set; }
    }
}
