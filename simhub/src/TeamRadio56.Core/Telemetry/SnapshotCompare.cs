using System;
using System.Collections.Generic;
using System.Globalization;

namespace TeamRadio56.Core.Telemetry
{
    /// <summary>
    /// 진단 전용: 같은 순간의 두 스냅샷(LMU 내장 메모리 / rF2 DLL 버퍼)을 대조한다.
    /// 엔진에는 영향이 없다 — 내장 메모리를 쓰는 중에 DLL도 있으면 TelemetrySource가 세션마다
    /// 한 번 결과를 로그로 남겨, 실차에서 두 경로가 같은 값을 내는지 확인할 수 있게 한다.
    ///
    /// 두 버퍼는 갱신 주기가 달라(텔레메트리 ~100Hz / ~50Hz) 같은 순간이 아니므로 연속값은
    /// 허용 오차로, 구조 값(트랙·차량 ID·클래스·플래그)은 정확히 비교한다.
    /// </summary>
    public static class SnapshotCompare
    {
        public static List<string> Diff(Snapshot a, Snapshot b)
        {
            var d = new List<string>();
            if (a == null || b == null)
            {
                d.Add("스냅샷 없음");
                return d;
            }
            SessionInfo sa = a.Session, sb = b.Session;
            Eq(d, "트랙", sa.Track, sb.Track);
            Eq(d, "세션", sa.SessionType, sb.SessionType);
            Eq(d, "페이즈", sa.GamePhase, sb.GamePhase);
            Eq(d, "최대 랩", sa.MaxLaps, sb.MaxLaps);
            Near(d, "트랙 길이", sa.TrackLength, sb.TrackLength, 0.5);
            Near(d, "세션 시각", sa.CurrentEt, sb.CurrentEt, 1.0);
            Near(d, "종료 시각", sa.EndEt, sb.EndEt, 0.5);
            Eq(d, "섹터 플래그", Join(sa.SectorFlags), Join(sb.SectorFlags));
            Eq(d, "옐로 상태", sa.YellowState, sb.YellowState);
            Near(d, "기온", sa.AmbientTemp, sb.AmbientTemp, 0.5);
            Near(d, "노면 온도", sa.TrackTemp, sb.TrackTemp, 0.5);
            Eq(d, "차량 수", sa.NumVehicles, sb.NumVehicles);
            int n = Math.Min(a.Vehicles.Count, b.Vehicles.Count);
            for (int i = 0; i < n && d.Count < 20; i++)
            {
                VehicleInfo va = a.Vehicles[i], vb = b.Vehicles[i];
                string p = "차량" + i + " ";
                Eq(d, p + "ID", va.Id, vb.Id);
                Eq(d, p + "클래스", va.Class, vb.Class);
                Eq(d, p + "플레이어", va.IsPlayer, vb.IsPlayer);
                Eq(d, p + "순위", va.Place, vb.Place);
                Eq(d, p + "랩", va.TotalLaps, vb.TotalLaps);
                Near(d, p + "베스트랩", va.BestLap, vb.BestLap, 0.001);
            }
            PlayerInfo pa = a.Player, pb = b.Player;
            Eq(d, "플레이어 텔레메트리", pa != null, pb != null);
            if (pa != null && pb != null)
            {
                Eq(d, "플레이어 ID", pa.Id, pb.Id);
                Near(d, "연료", pa.Fuel, pb.Fuel, 0.5);
                Near(d, "연료 탱크", pa.FuelCapacity, pb.FuelCapacity, 0.01);
                Near(d, "최대 RPM", pa.MaxRpm, pb.MaxRpm, 1.0);
                Near(d, "수온", pa.WaterTemp, pb.WaterTemp, 2.0);
                Eq(d, "피트 레인", pa.InPitLane, pb.InPitLane);
                for (int w = 0; w < Math.Min(pa.Wheels.Length, pb.Wheels.Length); w++)
                {
                    WheelInfo wa = pa.Wheels[w], wb = pb.Wheels[w];
                    string p = "휠" + w + " ";
                    Near(d, p + "공기압", wa.Pressure, wb.Pressure, 3.0);
                    Near(d, p + "타이어 온도", wa.Temps[1], wb.Temps[1], 5.0);
                    Near(d, p + "마모", wa.Wear, wb.Wear, 0.01);
                    Near(d, p + "브레이크 온도", wa.BrakeTemp, wb.BrakeTemp, 60.0);
                }
            }
            return d;
        }

        private static string Join(byte[] bytes)
        {
            return bytes == null ? "" : string.Join(",", Array.ConvertAll(bytes, x => x.ToString(CultureInfo.InvariantCulture)));
        }

        private static void Eq<T>(List<string> d, string what, T a, T b)
        {
            if (!EqualityComparer<T>.Default.Equals(a, b))
                d.Add(what + " " + a + " / " + b);
        }

        private static void Near(List<string> d, string what, double a, double b, double tol)
        {
            if (double.IsNaN(a) || double.IsNaN(b) || Math.Abs(a - b) > tol)
                d.Add(string.Format(CultureInfo.InvariantCulture, "{0} {1:0.###} / {2:0.###}", what, a, b));
        }
    }
}
