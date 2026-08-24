using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using TeamRadio56.Core.Telemetry;

namespace TeamRadio56.Replay
{
    /// <summary>
    /// 파이썬 --record가 저장한 JSONL 스냅샷(telemetry.py의 Snapshot dict)을
    /// C# Snapshot으로 읽는다. .gz도 지원 (리플레이 자산은 압축 보관).
    /// </summary>
    public static class ReplayLoader
    {
        public static IEnumerable<(Snapshot Snap, bool Connected)> Read(string path)
        {
            using Stream raw = File.OpenRead(path);
            using Stream stream = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
                ? new GZipStream(raw, CompressionMode.Decompress)
                : raw;
            using var reader = new StreamReader(stream);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0)
                    continue;
                using JsonDocument doc = JsonDocument.Parse(line);
                yield return Parse(doc.RootElement);
            }
        }

        private static (Snapshot, bool) Parse(JsonElement root)
        {
            var snap = new Snapshot
            {
                T = D(root, "t"),
                InSession = B(root, "in_session"),
                Session = ParseSession(Obj(root, "session")),
                Player = ParsePlayer(Obj(root, "player")),
                Vehicles = new List<VehicleInfo>(),
            };
            if (root.TryGetProperty("vehicles", out JsonElement vehicles)
                && vehicles.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement v in vehicles.EnumerateArray())
                    snap.Vehicles.Add(ParseVehicle(v));
            }
            return (snap, B(root, "connected"));
        }

        private static SessionInfo ParseSession(JsonElement? e)
        {
            if (e == null)
                return new SessionInfo();
            JsonElement s = e.Value;
            return new SessionInfo
            {
                Track = S(s, "track"),
                SessionType = I(s, "session_type"),
                CurrentEt = D(s, "current_et"),
                EndEt = D(s, "end_et"),
                MaxLaps = I(s, "max_laps"),
                TrackLength = D(s, "track_len"),
                GamePhase = I(s, "game_phase"),
                YellowState = I(s, "yellow_state"),
                SectorFlags = Bytes(s, "sector_flags"),
                PitSpeedLimitKmh = D(s, "pit_speed_limit"),
                InRealtime = B(s, "in_realtime"),
                Raining = D(s, "raining"),
                DarkCloud = D(s, "dark_cloud"),
                AmbientTemp = D(s, "ambient_temp"),
                TrackTemp = D(s, "track_temp"),
                AvgWetness = D(s, "avg_wetness"),
                NumVehicles = I(s, "num_vehicles"),
                StatusMessage = S(s, "status_message"),
                HistoryMessage = S(s, "history_message"),
            };
        }

        private static PlayerInfo ParsePlayer(JsonElement? e)
        {
            if (e == null || e.Value.ValueKind != JsonValueKind.Object
                || !e.Value.TryGetProperty("id", out _))
            {
                return null;   // 파이썬은 빈 dict — C#은 null이 규약
            }
            JsonElement p = e.Value;
            var info = new PlayerInfo
            {
                Id = I(p, "id"),
                LapNumber = I(p, "lap_number"),
                SpeedKmh = D(p, "speed_kmh"),
                Rpm = D(p, "rpm"),
                MaxRpm = D(p, "max_rpm"),
                Gear = I(p, "gear"),
                Fuel = D(p, "fuel"),
                FuelCapacity = D(p, "fuel_capacity"),
                WaterTemp = D(p, "water_temp"),
                OilTemp = D(p, "oil_temp"),
                Overheating = B(p, "overheating"),
                Detached = B(p, "detached"),
                FrontWingHeight = D(p, "front_wing_height"),
                DentSeverity = Bytes(p, "dent_severity"),
                LastImpactEt = D(p, "last_impact_et"),
                LastImpactMag = D(p, "last_impact_mag"),
                InPitLane = B(p, "in_pitlane"),
                SpeedLimiter = B(p, "speed_limiter"),
                Steering = D(p, "steering"),
                YawRate = D(p, "yaw_rate"),
                LatVel = D(p, "lat_vel"),
                Wheels = ParseWheels(p),
            };
            return info;
        }

        private static WheelInfo[] ParseWheels(JsonElement p)
        {
            if (!p.TryGetProperty("wheels", out JsonElement arr)
                || arr.ValueKind != JsonValueKind.Array)
            {
                return new WheelInfo[0];
            }
            var wheels = new List<WheelInfo>();
            foreach (JsonElement w in arr.EnumerateArray())
            {
                var temps = new List<double>();
                if (w.TryGetProperty("temps", out JsonElement t)
                    && t.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement x in t.EnumerateArray())
                        temps.Add(x.GetDouble());
                }
                wheels.Add(new WheelInfo
                {
                    BrakeTemp = D(w, "brake_temp"),
                    Pressure = D(w, "pressure"),
                    Temps = temps.ToArray(),
                    CarcassTemp = D(w, "carcass_temp"),
                    Wear = D(w, "wear"),
                    Flat = B(w, "flat"),
                    Detached = B(w, "detached"),
                });
            }
            return wheels.ToArray();
        }

        private static VehicleInfo ParseVehicle(JsonElement v)
        {
            return new VehicleInfo
            {
                Id = I(v, "id"),
                Driver = S(v, "driver"),
                VehicleName = S(v, "vehicle"),
                Class = S(v, "cls"),
                IsPlayer = B(v, "is_player"),
                Place = I(v, "place"),
                TotalLaps = I(v, "total_laps"),
                LapDist = D(v, "lap_dist"),
                PathLateral = D(v, "path_lat"),
                Sector = I(v, "sector"),
                LastLap = D(v, "last_lap"),
                BestLap = D(v, "best_lap"),
                LastS1 = D(v, "last_s1"),
                LastS2 = D(v, "last_s2"),
                TimeBehindNext = D(v, "time_behind_next"),
                LapsBehindNext = I(v, "laps_behind_next"),
                TimeBehindLeader = D(v, "time_behind_leader"),
                InPits = B(v, "in_pits"),
                PitState = I(v, "pit_state"),
                NumPitstops = I(v, "num_pitstops"),
                NumPenalties = I(v, "num_penalties"),
                FinishStatus = I(v, "finish_status"),
                FlagBlue = B(v, "flag_blue"),
                EstimatedLap = D(v, "estimated_lap"),
                TimeIntoLap = D(v, "time_into_lap"),
                InGarage = B(v, "in_garage"),
            };
        }

        // -- JSON 헬퍼 (없는 키는 기본값) --------------------------------------

        private static JsonElement? Obj(JsonElement e, string key)
        {
            return e.TryGetProperty(key, out JsonElement v)
                   && v.ValueKind == JsonValueKind.Object
                ? v : (JsonElement?)null;
        }

        private static double D(JsonElement e, string key)
        {
            return e.TryGetProperty(key, out JsonElement v)
                   && v.ValueKind == JsonValueKind.Number
                ? v.GetDouble() : 0.0;
        }

        private static int I(JsonElement e, string key)
        {
            if (!e.TryGetProperty(key, out JsonElement v)
                || v.ValueKind != JsonValueKind.Number)
            {
                return 0;
            }
            // max_laps 같은 2147483647은 int 범위 내, 그 외 실수는 절사
            long l = (long)v.GetDouble();
            if (l > int.MaxValue) return int.MaxValue;
            if (l < int.MinValue) return int.MinValue;
            return (int)l;
        }

        private static bool B(JsonElement e, string key)
        {
            return e.TryGetProperty(key, out JsonElement v)
                   && (v.ValueKind == JsonValueKind.True
                       || (v.ValueKind == JsonValueKind.Number && v.GetDouble() != 0));
        }

        private static string S(JsonElement e, string key)
        {
            return e.TryGetProperty(key, out JsonElement v)
                   && v.ValueKind == JsonValueKind.String
                ? v.GetString() : "";
        }

        private static byte[] Bytes(JsonElement e, string key)
        {
            if (!e.TryGetProperty(key, out JsonElement v)
                || v.ValueKind != JsonValueKind.Array)
            {
                return new byte[0];
            }
            var list = new List<byte>();
            foreach (JsonElement x in v.EnumerateArray())
                list.Add((byte)x.GetDouble());
            return list.ToArray();
        }
    }
}
