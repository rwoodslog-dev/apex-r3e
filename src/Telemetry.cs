// R3E Telemetry Logger — version C#
// Aucune installation requise : compile avec le .NET Framework livre avec Windows.
//
// Lecture directe de la shared memory "$R3E" a des offsets verifies
// depuis le r3e.h officiel v3.5 (kwstudios-sweden/r3e-api).
//
// On lit les octets bruts plutot que de marshaller la struct : le R3E.cs
// officiel utilise des structs generiques (TireData<T>, Vector3<T>) que
// Marshal.PtrToStructure ne sait PAS convertir — ca planterait a l'execution.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading;

static class Telemetry
{
    // ---- offsets generes depuis r3e.h v3.5 (verifies) --------------------
    public const int OFF_VersionMajor = 0;
    public const int OFF_VersionMinor = 4;
    public const int OFF_GamePaused = 20;
    public const int OFF_GameInMenus = 24;
    public const int OFF_GameInReplay = 28;
    public const int OFF_GameInGarage = 36;
    public const int OFF_TrackName = 600;   // char[64]
    public const int OFF_LayoutName = 664;  // char[64]
    public const int OFF_LayoutLength = 736;
    public const int OFF_SessionType = 780;
    public const int OFF_InPitlane = 848;
    public const int OFF_PitState = 904;
    public const int OFF_CompletedLaps = 1028;
    public const int OFF_LapDistance = 1040;
    public const int OFF_LapDistFrac = 1044;
    public const int OFF_LapTimeBest = 1068;
    public const int OFF_LapTimePrev = 1084;
    public const int OFF_LapTimeCurrent = 1100;
    public const int OFF_CarSpeed = 1392;
    public const int OFF_EngineRps = 1396;
    public const int OFF_Gear = 1408;
    public const int OFF_FuelLeft = 1456;
    public const int OFF_Throttle = 1500;
    public const int OFF_Brake = 1508;
    public const int OFF_Clutch = 1516;
    public const int OFF_SteerRaw = 1524;
    public const int OFF_BrakeBias = 1596;
    public const int OFF_TireSpeed = 1648;      // float[4]
    public const int OFF_TireGrip = 1664;       // float[4]
    public const int OFF_TirePressure = 1712;   // float[4]
    public const int OFF_TireTemp = 1744;       // struct[4], 24 o chacun, current[3] en tete
    public const int OFF_BrakeTemp = 1856;      // struct[4], 16 o chacun, current en tete
    public const int OFF_BrakePressure = 1920;  // float[4]
    public const int OFF_TireLoad = 1968;       // float[4]

    public const int OFF_SimTime = 48;          // double, dans Player
    public const int OFF_PosX = 56;             // double x,y,z
    public const int OFF_LocalGX = 296;         // double x,y,z

    // ---- donnees de course (offsets verifies depuis r3e.h v3.5) ----------
    public const int OFF_Position = 988;
    public const int OFF_PositionClass = 992;
    public const int OFF_NumberOfLaps = 812;      // -1 si course au temps
    public const int OFF_RaceSessionLaps = 752;   // int[3]
    public const int OFF_FuelCapacity = 1460;
    public const int OFF_FuelPerLap = 1464;
    public const int OFF_FuelUseActive = 808;
    public const int OFF_DeltaFront = 1124;
    public const int OFF_DeltaBehind = 1128;
    public const int OFF_DeltaLeader = 1116;
    public const int OFF_SessionTimeRemaining = 820;
    public const int OFF_SessionPhase = 796;
    public const int OFF_NumCars = 2008;
    public const int OFF_NumPenalties = 1024;
    public const int OFF_TireWear = 1680;         // float[4]
    public const int OFF_TireWearActive = 804;
    public const int OFF_PitWindowStatus = 836;
    public const int OFF_PitWindowStart = 840;
    public const int OFF_PitWindowEnd = 844;
    public const int OFF_NumPitstops = 920;
    public const int OFF_EngineTemp = 1480;
    // drapeaux (la struct flags commence a 932)
    public const int OFF_FlagYellow = 932;
    public const int OFF_FlagBlue = 964;
    public const int OFF_FlagBlack = 968;
    public const int OFF_FlagCheckered = 976;
    public const int OFF_FlagWhite = 980;
    public const int OFF_FlagBlackWhite = 984;

    public const int SHARED_SIZE = 44000;
    public const int EXPECT_MAJOR = 3;
    public const int EXPECT_MINOR = 5;

    static readonly string[] WHEELS = { "fl", "fr", "rl", "rr" };

    public static string[] BuildHeader()
    {
        var c = new List<string> {
            "t","sim_time","lap_distance","lap_dist_frac","speed_kmh",
            "throttle","brake","clutch","steer","gear","rpm","fuel_left",
            "pos_x","pos_y","pos_z","g_lon","g_lat","g_vert","brake_bias"
        };
        foreach (var w in WHEELS) c.Add("brake_press_" + w);
        foreach (var w in WHEELS) c.Add("tire_grip_" + w);
        foreach (var w in WHEELS) c.Add("tire_press_" + w);
        foreach (var w in WHEELS) c.Add("tire_load_" + w);
        foreach (var w in WHEELS) c.Add("tire_speed_" + w);
        foreach (var w in WHEELS) c.Add("slip_" + w);
        foreach (var w in WHEELS) c.Add("tire_temp_" + w);
        foreach (var w in WHEELS) c.Add("brake_temp_" + w);
        return c.ToArray();
    }

    static readonly CultureInfo INV = CultureInfo.InvariantCulture;
    public static string F(double v, int d) { return Math.Round(v, d).ToString(INV); }

    public static string ReadStr(byte[] b, int off, int len)
    {
        int n = 0;
        while (n < len && b[off + n] != 0) n++;
        return Encoding.UTF8.GetString(b, off, n).Trim();
    }

    public static string Safe(string s)
    {
        if (string.IsNullOrEmpty(s)) s = "unknown";
        var sb = new StringBuilder();
        foreach (char ch in s)
            sb.Append(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.' ? ch : '_');
        return sb.ToString().Trim('_');
    }

    public static string FmtTime(double sec)
    {
        if (sec <= 0) return "invalide";
        int m = (int)(sec / 60);
        double s = sec - m * 60;
        return m + ":" + s.ToString("00.000", INV);
    }

    public static string FmtTimeFile(double sec)
    {
        if (sec <= 0) return "invalid";
        int m = (int)(sec / 60);
        double s = sec - m * 60;
        return m + "m" + s.ToString("00.000", INV).Replace(".", "_") + "s";
    }

    public static double lastSimT = double.MinValue;

    public static string BuildRow(byte[] b, double tLap, double simT)
    {
        float speedMs = BitConverter.ToSingle(b, OFF_CarSpeed);
        var sb = new StringBuilder(512);

        sb.Append(F(tLap, 4)).Append(',');
        sb.Append(F(simT, 4)).Append(',');
        sb.Append(F(BitConverter.ToSingle(b, OFF_LapDistance), 3)).Append(',');
        sb.Append(F(BitConverter.ToSingle(b, OFF_LapDistFrac), 6)).Append(',');
        sb.Append(F(speedMs * 3.6, 3)).Append(',');
        sb.Append(F(BitConverter.ToSingle(b, OFF_Throttle), 4)).Append(',');
        sb.Append(F(BitConverter.ToSingle(b, OFF_Brake), 4)).Append(',');
        sb.Append(F(BitConverter.ToSingle(b, OFF_Clutch), 4)).Append(',');
        sb.Append(F(BitConverter.ToSingle(b, OFF_SteerRaw), 4)).Append(',');
        sb.Append(BitConverter.ToInt32(b, OFF_Gear)).Append(',');
        sb.Append(F(BitConverter.ToSingle(b, OFF_EngineRps) * 9.5492966, 1)).Append(',');
        sb.Append(F(BitConverter.ToSingle(b, OFF_FuelLeft), 3)).Append(',');
        // position (double x,y,z)
        sb.Append(F(BitConverter.ToDouble(b, OFF_PosX), 3)).Append(',');
        sb.Append(F(BitConverter.ToDouble(b, OFF_PosX + 8), 3)).Append(',');
        sb.Append(F(BitConverter.ToDouble(b, OFF_PosX + 16), 3)).Append(',');
        // local g : z = longitudinal, x = lateral, y = vertical
        sb.Append(F(BitConverter.ToDouble(b, OFF_LocalGX + 16), 4)).Append(',');
        sb.Append(F(BitConverter.ToDouble(b, OFF_LocalGX), 4)).Append(',');
        sb.Append(F(BitConverter.ToDouble(b, OFF_LocalGX + 8), 4)).Append(',');
        sb.Append(F(BitConverter.ToSingle(b, OFF_BrakeBias), 4));

        AppendFloat4(sb, b, OFF_BrakePressure, 3);
        AppendFloat4(sb, b, OFF_TireGrip, 4);
        AppendFloat4(sb, b, OFF_TirePressure, 2);
        AppendFloat4(sb, b, OFF_TireLoad, 2);
        AppendFloat4(sb, b, OFF_TireSpeed, 3);

        // slip derive : (vitesse roue - vitesse voiture) / vitesse voiture
        for (int i = 0; i < 4; i++)
        {
            float ts = BitConverter.ToSingle(b, OFF_TireSpeed + i * 4);
            double slip = (speedMs > 3.0 && ts > -100) ? (ts - speedMs) / speedMs : 0.0;
            sb.Append(',').Append(F(slip, 5));
        }

        // temperature pneu = moyenne des 3 zones (struct de 24 o, current[3] en tete)
        for (int i = 0; i < 4; i++)
        {
            int bas = OFF_TireTemp + i * 24;
            double sum = 0; int n = 0;
            for (int j = 0; j < 3; j++)
            {
                float v = BitConverter.ToSingle(b, bas + j * 4);
                if (v > -100) { sum += v; n++; }
            }
            sb.Append(',').Append(F(n > 0 ? sum / n : -1.0, 2));
        }

        // temperature frein (struct de 16 o, current en tete)
        for (int i = 0; i < 4; i++)
            sb.Append(',').Append(F(BitConverter.ToSingle(b, OFF_BrakeTemp + i * 16), 2));

        return sb.ToString();
    }

    public static void AppendFloat4(StringBuilder sb, byte[] b, int off, int dec)
    {
        for (int i = 0; i < 4; i++)
            sb.Append(',').Append(F(BitConverter.ToSingle(b, off + i * 4), dec));
    }
}
