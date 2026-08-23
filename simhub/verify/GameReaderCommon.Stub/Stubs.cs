// SimHub 검증용 스텁 — 실제 SimHub 어셈블리가 없는 환경에서
// 플러그인 코드를 컴파일 검증하기 위한 최소 정의. 배포물에 포함 금지.
namespace GameReaderCommon
{
    public class StatusDataBase
    {
        public double SpeedKmh { get; set; }
        public int CurrentLap { get; set; }
    }

    public class GameData
    {
        public string GameName { get; set; }
        public bool GameRunning { get; set; }
        public StatusDataBase NewData { get; set; }
    }
}
