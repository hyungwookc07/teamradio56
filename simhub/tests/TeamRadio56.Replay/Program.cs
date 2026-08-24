using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using TeamRadio56.Core.Logic;
using TeamRadio56.Core.Telemetry;

namespace TeamRadio56.Replay
{
    /// <summary>
    /// 리플레이 회귀 러너 — 파이썬 tools/replay_calls.py와 짝.
    ///
    /// 녹화 JSONL을 C# 파이프라인(현재: 트래픽 분석기)에 먹이고, 이벤트
    /// 버스가 "수락한" 이벤트를 JSONL로 출력한다. 두 출력이 같으면 포팅이
    /// 파이썬과 같은 판단을 한다는 뜻 (멘트 문구/랜덤은 비교 대상 아님 —
    /// 판단·타이밍·데이터가 비교 대상).
    ///
    /// 사용법: dotnet run -- --replay race.jsonl[.gz] [--out calls.jsonl]
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            string replayPath = null;
            string outPath = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--replay" && i + 1 < args.Length)
                    replayPath = args[++i];
                else if (args[i] == "--out" && i + 1 < args.Length)
                    outPath = args[++i];
            }
            if (replayPath == null)
            {
                Console.Error.WriteLine("사용법: --replay <file.jsonl[.gz]> [--out calls.jsonl]");
                return 2;
            }

            double clock = 0.0;
            var bus = new EventBus(Cooldowns.Default(), () => clock);
            var state = new RaceState();
            var traffic = new TrafficAnalyzer(new TrafficSettings());

            var lines = new List<string>();
            bus.Accepted = ev => lines.Add(Format(clock, ev));

            int ticks = 0;
            foreach ((Snapshot snap, bool connected) in ReplayLoader.Read(replayPath))
            {
                if (!connected)
                    continue;
                clock = snap.T;
                state.Observe(snap);
                traffic.OnTick(state, snap, bus);
                ticks++;
            }

            TextWriter writer = outPath != null
                ? new StreamWriter(outPath, false, new UTF8Encoding(false))
                : Console.Out;
            foreach (string line in lines)
                writer.WriteLine(line);
            if (outPath != null)
            {
                writer.Dispose();
                Console.Error.WriteLine(
                    $"틱 {ticks}개 처리, 수락 이벤트 {lines.Count}개 → {outPath}");
            }
            return 0;
        }

        private static string Format(double t, RadioEvent ev)
        {
            // 파이썬 쪽과 같은 형식: 키 정렬된 JSON 한 줄
            var sb = new StringBuilder();
            sb.Append('{');
            AppendKv(sb, "data", DataJson(ev.Data), raw: true);
            sb.Append(',');
            AppendKv(sb, "key", ev.Key);
            sb.Append(',');
            AppendKv(sb, "message", ev.Message);
            sb.Append(',');
            AppendKv(sb, "prio", ((int)ev.Priority).ToString(CultureInfo.InvariantCulture), raw: true);
            sb.Append(',');
            AppendKv(sb, "t", Num(Math.Round(t, 2)), raw: true);
            sb.Append(',');
            AppendKv(sb, "tone", ev.Tone);
            sb.Append(',');
            AppendKv(sb, "type", ev.Type);
            sb.Append('}');
            return sb.ToString();
        }

        private static string DataJson(Dictionary<string, object> data)
        {
            var keys = new List<string>(data.Keys);
            keys.Sort(StringComparer.Ordinal);
            var sb = new StringBuilder();
            sb.Append('{');
            for (int i = 0; i < keys.Count; i++)
            {
                if (i > 0)
                    sb.Append(',');
                sb.Append(Quote(keys[i])).Append(':');
                object v = data[keys[i]];
                if (v is string s)
                    sb.Append(Quote(s));
                else if (v is int n)
                    sb.Append(n.ToString(CultureInfo.InvariantCulture));
                else if (v is double d)
                    sb.Append(Num(d));
                else if (v is bool b)
                    sb.Append(b ? "true" : "false");
                else
                    sb.Append(Quote(v == null ? "" : v.ToString()));
            }
            sb.Append('}');
            return sb.ToString();
        }

        private static void AppendKv(StringBuilder sb, string key, string value, bool raw = false)
        {
            sb.Append(Quote(key)).Append(':');
            if (value == null)
                sb.Append("null");
            else if (raw)
                sb.Append(value);
            else
                sb.Append(Quote(value));
        }

        private static string Num(double d)
        {
            if (d == Math.Floor(d) && Math.Abs(d) < 1e15)
                return ((long)d).ToString(CultureInfo.InvariantCulture) + ".0";
            return d.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string Quote(string s)
        {
            var sb = new StringBuilder();
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
