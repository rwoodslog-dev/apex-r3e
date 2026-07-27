/* Ams2Reader.cs — Lit la telemetrie AMS2 via CREST2 (localhost:8180).
 * Produit les memes variables que la lecture R3E pour que le reste
 * d'APEX fonctionne identiquement. */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

class Ams2Reader
{
    static readonly CultureInfo INV = CultureInfo.InvariantCulture;
    const string CREST_URL = "http://localhost:8180/crest2/v1/api";
    const int POLL_MS = 50;       // ~20 Hz comme R3E

    public bool Connected { get; private set; }
    public string Error { get; private set; }

    // --- derniere lecture (memes noms que le broadcast APEX) ---
    public double Speed;           // km/h
    public int    Gear;
    public double Rpm, MaxRpm;
    public double Throttle, Brake, Steer;
    public double PosX, PosZ;      // plan horizontal
    public double Dist;            // lap distance (m)
    public double CurLap, BestLap;
    public int    RacePos, NumCars, LapsCompleted, TotalLaps;
    public double FuelLeft, FuelCapacity, FuelPct;
    public string Track = "", Layout = "", CarName = "", CarClass = "";
    public int    SessionState, GameState;
    public double[] TyreTemp = new double[4];
    public double[] TyreWear = new double[4];
    public double[] BrakeTemp = new double[4];
    public int    TractionControl;
    public bool   AbsActive;
    public double TrackLength;

    // --- detection changement de tour ---
    int prevLaps = -999;
    public bool NewLap { get; private set; }

    /// <summary>
    /// Lit une frame depuis CREST2. Retourne true si des donnees valides
    /// ont ete lues, false sinon (CREST2 pas lance, AMS2 pas en course).
    /// </summary>
    public bool Poll()
    {
        NewLap = false;
        try
        {
            var req = (HttpWebRequest)WebRequest.Create(CREST_URL);
            req.Timeout = 200;
            req.ReadWriteTimeout = 200;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                string json = sr.ReadToEnd();
                if (string.IsNullOrEmpty(json) || !json.Contains("\"mSpeed\""))
                {
                    Connected = false;
                    Error = "AMS2 pas en course";
                    return false;
                }
                Connected = true;
                Error = null;
                Parse(json);
                return true;
            }
        }
        catch (WebException)
        {
            Connected = false;
            Error = "CREST2 introuvable (localhost:8180)";
            return false;
        }
        catch (Exception ex)
        {
            Connected = false;
            Error = ex.Message;
            return false;
        }
    }

    // --- mini parseur JSON sans dependance (pas de Newtonsoft) ---
    void Parse(string json)
    {
        Speed     = JNum(json, "\"mSpeed\"") * 3.6;    // m/s -> km/h
        Gear      = (int)JNum(json, "\"mGear\"");
        Rpm       = JNum(json, "\"mRpm\"");
        MaxRpm    = JNum(json, "\"mMaxRPM\"");
        Throttle  = JNum(json, "\"mThrottle\"");
        Brake     = JNum(json, "\"mBrake\"");
        Steer     = JNum(json, "\"mSteering\"");
        Dist      = JNum(json, "\"mCurrentLapDistance\"");
        CurLap    = JNum(json, "\"mCurrentTime\"");
        BestLap   = JNum(json, "\"mBestLapTime\"");
        RacePos   = (int)JNum(json, "\"mRacePosition\"");
        TrackLength = JNum(json, "\"mTrackLength\"");
        FuelLeft  = JNum(json, "\"mFuelLevel\"");
        FuelCapacity = JNum(json, "\"mFuelCapacity\"");
        FuelPct   = FuelCapacity > 0 ? FuelLeft / FuelCapacity * 100.0 : -1;
        TractionControl = (int)JNum(json, "\"mTractionControlSetting\"");
        AbsActive = JNum(json, "\"mAntiLockActive\"") > 0.5;
        SessionState = (int)JNum(json, "\"mSessionState\"");
        GameState = (int)JNum(json, "\"mGameState\"");

        // position X/Z depuis mWorldPosition [x, y, z]
        // mWorldPosition est un tableau; on le parse manuellement
        int wpIdx = json.IndexOf("\"mWorldPosition\"");
        if (wpIdx >= 0)
        {
            int bra = json.IndexOf('[', wpIdx);
            int ket = json.IndexOf(']', bra);
            if (bra >= 0 && ket > bra)
            {
                string[] parts = json.Substring(bra + 1, ket - bra - 1).Split(',');
                if (parts.Length >= 3)
                {
                    double.TryParse(parts[0].Trim(), NumberStyles.Float, INV, out PosX);
                    double.TryParse(parts[2].Trim(), NumberStyles.Float, INV, out PosZ);
                }
            }
        }

        // track + car
        Track    = JStr(json, "\"mTrackLocation\"");
        Layout   = JStr(json, "\"mTrackVariation\"");
        CarName  = JStr(json, "\"mCarName\"");
        CarClass = JStr(json, "\"mCarClassName\"");

        // laps
        int newLaps = (int)JNum(json, "\"mLapsCompleted\"");
        NumCars   = (int)JNum(json, "\"mNumParticipants\"");
        TotalLaps = (int)JNum(json, "\"mLapsInEvent\"");
        if (newLaps > prevLaps && prevLaps >= 0) NewLap = true;
        LapsCompleted = newLaps;
        prevLaps = newLaps;

        // pneus : mTyreTemp est un tableau [FL, FR, RL, RR]
        ParseArray(json, "\"mTyreTemp\"", TyreTemp);
        ParseArray(json, "\"mTyreWear\"", TyreWear);
        ParseArray(json, "\"mBrakeTempCelsius\"", BrakeTemp);
    }

    static double JNum(string json, string key)
    {
        int i = json.IndexOf(key);
        if (i < 0) return 0;
        int c = json.IndexOf(':', i) + 1;
        if (c <= 0) return 0;
        // skip whitespace
        while (c < json.Length && (json[c] == ' ' || json[c] == '\t')) c++;
        // find end of number
        int e = c;
        while (e < json.Length && (char.IsDigit(json[e]) || json[e] == '.' || json[e] == '-' || json[e] == 'e' || json[e] == 'E' || json[e] == '+'))
            e++;
        double val;
        double.TryParse(json.Substring(c, e - c), NumberStyles.Float, INV, out val);
        return val;
    }

    static string JStr(string json, string key)
    {
        int i = json.IndexOf(key);
        if (i < 0) return "";
        int q1 = json.IndexOf('"', json.IndexOf(':', i) + 1);
        if (q1 < 0) return "";
        int q2 = json.IndexOf('"', q1 + 1);
        if (q2 < 0) return "";
        return json.Substring(q1 + 1, q2 - q1 - 1);
    }

    static void ParseArray(string json, string key, double[] arr)
    {
        int i = json.IndexOf(key);
        if (i < 0) return;
        int bra = json.IndexOf('[', i);
        int ket = json.IndexOf(']', bra);
        if (bra < 0 || ket < 0) return;
        string[] parts = json.Substring(bra + 1, ket - bra - 1).Split(',');
        for (int k = 0; k < Math.Min(parts.Length, arr.Length); k++)
            double.TryParse(parts[k].Trim(), NumberStyles.Float, INV, out arr[k]);
    }
}
