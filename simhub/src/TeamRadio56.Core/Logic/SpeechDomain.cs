using System;
using System.Collections.Generic;
using System.Globalization;

namespace TeamRadio56.Core.Logic
{
    /// <summary>
    /// speech_domain.py 포팅 — 음성 팩(문장 세그먼트)과 런타임 판단이 같은 값을 보는 단일 출처.
    ///
    ///   - Seg*: 문장 단위 사전 생성 도메인 (PregenSentences가 열거).
    ///   - 한계/술어: 분석기가 무의미한 값(피트·FCY·스핀·센서 튐)을 말하지 않게 거르는 기준.
    ///     술어는 모두 표시 반올림한 값으로 판단한다 — 사전 생성 문장과 같은 숫자를 봐야
    ///     "도메인 안 = 팩에 있음"이 성립한다.
    ///
    /// 파이썬과 값·순서가 같아야 한다 (replay-check가 문장 집합과 퍼즈 결과를 대조).
    /// </summary>
    public static class SpeechDomain
    {
        // -- 표시 반올림 ------------------------------------------------------

        /// <summary>파이썬 float(f"{x:.{d}f}") — 말하는 값.</summary>
        public static double Fixed(double x, int d)
        {
            // PyFixed는 비유한 값을 "nan"/"inf"로 내는데 double.Parse가 못 읽는다 (net48은 nan도)
            if (double.IsNaN(x) || double.IsInfinity(x))
                return x;
            return double.Parse(Messages.PyFixed(x, d), CultureInfo.InvariantCulture);
        }

        /// <summary>SecondsText와 같은 분기 — 10초 미만은 소수 1자리, 이상은 정수.</summary>
        public static double GapTextValue(double g)
        {
            return g < 10 ? Fixed(g, 1) : Fixed(g, 0);
        }

        /// <summary>LapsText가 말하는 정수 (짝수 반올림).</summary>
        public static int LapsValue(double n)
        {
            return (int)Math.Round(n, MidpointRounding.ToEven);
        }

        static bool Finite(double x)
        {
            return !double.IsNaN(x) && !double.IsInfinity(x);
        }

        // -- 한계 (술어 기준) ---------------------------------------------------

        public const int GapSegMax = 600;           // 팩이 말하는 갭 상한 (정수 초) — 그 위는 kokoro 세션에서 자막만
        public const double RateMax = 9.9;
        public const double PaceMin = 0.7, PaceMax = 9.9;
        public const double RivalDiffMin = 0.3, RivalDiffMax = 5.0;
        public const int RivalLapsMin = 1, RivalLapsMax = 16;
        public const int RivalPitWholeMax = 30;     // 통문장 P1~30, 그 위는 문장 조립
        public const int TyreHotMin = 12, TyreHotMax = 80;
        public const int TyreWearMax = 30;
        public const int PitWindowWholeMax = 40;    // 통문장 0~40, 그 위는 문장 조립
        public const int PitWindowMax = 99;
        public const int WingMmMin = 10, WingMmMax = 100;
        public const int BrakeTMin = 700, BrakeTMax = 1500, BrakeTStep = 10;
        public const double RepairMin = 0.5, RepairMax = 9.9;
        public const double FuelTargetMin = 0.5, FuelTargetMax = 15.0;
        public const double FuelSaveMin = 0.1, FuelSaveMax = 3.0;
        public const int ClassPosMax = 60;
        public const int BriefMinutesMax = 1440;
        public const int BriefLapsMax = 100;
        public const int BriefCarsMin = 2, BriefCarsMax = 60;
        public const int BriefPosMax = 60;
        public const int BriefTempMin = 1, BriefTempMax = 60;
        public const int BriefFuelMin = 1, BriefFuelMax = 130;
        public const int LaptimeT10Min = 600, LaptimeT10Max = 2699;   // 1:00.0 ~ 4:29.9
        public const int StatusFuelMax = 99;
        public const int StatusTyreMax = 20;
        public const double CoachMin = 0.1, CoachMax = 2.9;
        public const int MultiMaxCars = 3;
        public const int DentMaxZones = 3;
        public static readonly string[] MultiClasses =
            { "Hypercar", "LMP2", "GT3", "GTE", "faster car" };

        // -- 문장 단위 사전 생성 도메인 (파이썬 SEG_*와 값·순서 동일) -------------

        public static readonly double[] SegGaps = Gaps();
        public static readonly double[] SegRates = Tenths(1, 99);
        public static readonly double[] SegPace = Tenths(7, 99);
        public static readonly double[] SegRivalDiff = Tenths(3, 50);
        public const int SegTyreHotMin = TyreHotMin, SegTyreHotMax = TyreHotMax;
        public const int SegTyreLapsMin = 0, SegTyreLapsMax = TyreWearMax;
        // 2단계 — 차량 상태/연료 세이브/세션 브리핑
        public const int SegWingMmMin = WingMmMin, SegWingMmMax = WingMmMax;
        public const int SegBrakeTMin = BrakeTMin, SegBrakeTMax = BrakeTMax, SegBrakeTStep = BrakeTStep;
        public static readonly double[] SegRepair = Tenths(5, 99);       // 10.0 이상은 repair_cost_big
        public static readonly double[] SegFuelTarget = Tenths(5, 150);
        public static readonly double[] SegFuelSave = Tenths(1, 30);
        public const int SegPitWindowMin = PitWindowWholeMax + 1;        // 0~40은 통문장
        public const int SegRivalPitMin = RivalPitWholeMax + 1;          // 1~30은 통문장
        public static readonly double[] SegCoach = Tenths(1, 29);
        public static readonly int[] CoachSectors = { 1, 2, 3 };

        // -- 술어 -----------------------------------------------------------

        /// <summary>변화율 랩당 10초 이상은 피트/FCY 산물 — 말하지 않는다.
        /// 큰 갭 자체는 내구 레이스에서 정상이라 거르지 않는다.</summary>
        public static bool GapRateOk(double rate)
        {
            return Fixed(Math.Abs(rate), 1) <= RateMax;
        }

        public static bool GapInPack(double gap)
        {
            return GapTextValue(gap) <= GapSegMax;
        }

        public static bool PaceOk(double delta)
        {
            return Fixed(Math.Abs(delta), 1) <= PaceMax;
        }

        public static bool RivalDiffOk(double diff)
        {
            return Fixed(diff, 1) <= RivalDiffMax;
        }

        public static bool RivalPitOk(int p)
        {
            return p <= ClassPosMax;
        }

        public static bool TyreHotOk(double delta)
        {
            return Fixed(delta, 0) <= TyreHotMax;
        }

        public static bool TyreWearOk(double lapsLeft)
        {
            // NaN/Inf(센서 튐) — (int) 변환은 런타임마다 값이 달라 판정 전에 거른다 (파이썬과 같은 판정)
            return Finite(lapsLeft) && LapsValue(lapsLeft) <= TyreWearMax;
        }

        public static bool PitWindowOk(int n)
        {
            return n <= PitWindowMax;
        }

        public static bool WingOk(double dropM)
        {
            return Fixed(dropM * 1000, 0) <= WingMmMax;
        }

        public static bool BrakeOk(int t)
        {
            return t <= BrakeTMax;
        }

        public static bool RepairBig(double delta)
        {
            return Fixed(delta, 1) > RepairMax;
        }

        public static bool FuelSaveOk(double target, double delta)
        {
            double t = Fixed(target, 1);
            return t >= FuelTargetMin && t <= FuelTargetMax && Fixed(delta, 1) <= FuelSaveMax;
        }

        public static bool CoachOk(double delta)
        {
            return Fixed(Math.Abs(delta), 1) <= CoachMax;
        }

        public static bool LaptimeInPack(double sec)
        {
            int t10 = Messages.LaptimeT10(sec);
            return t10 >= LaptimeT10Min && t10 <= LaptimeT10Max;
        }

        public static bool StatusFuelOk(double fuelLaps)
        {
            if (!Finite(fuelLaps))
                return false;
            int n = LapsValue(fuelLaps);
            return n >= 0 && n <= StatusFuelMax;
        }

        public static bool BriefGridOk(int n, int cp)
        {
            return n >= BriefCarsMin && n <= BriefCarsMax && cp >= 1 && cp <= BriefPosMax;
        }

        public static bool BriefTempOk(double t)
        {
            double v = Fixed(t, 0);
            return v >= BriefTempMin && v <= BriefTempMax;
        }

        public static bool BriefFuelOk(double f)
        {
            double v = Fixed(f, 0);
            return v >= BriefFuelMin && v <= BriefFuelMax;
        }

        // -- 트랙 이름 목록 (en 브리핑 첫 문장) ----------------------------------

        private static readonly HashSet<string> Tracks =
            new HashSet<string>(PhraseLines.TrackNamesEn, StringComparer.Ordinal);

        /// <summary>voice_lines/track_names_en.txt (생성 시 내장) — en 브리핑이 이름을 말할 수 있는가.</summary>
        public static bool KnownTrackEn(string name)
        {
            return name != null && Tracks.Contains(name);
        }

        private static double[] Tenths(int lo, int hi)
        {
            var v = new double[hi - lo + 1];
            for (int i = lo; i <= hi; i++)
                v[i - lo] = i / 10.0;
            return v;
        }

        private static double[] Gaps()
        {
            var v = new List<double>(Tenths(0, 99));
            for (int s = 10; s <= GapSegMax; s++)
                v.Add(s);
            return v.ToArray();
        }
    }
}
