// APEX — R3E Telemetry Coach
// Un seul exe : lit la shared memory RaceRoom, enregistre les tours en CSV,
// et sert un dashboard temps reel sur http://localhost:8422
//
// Aucune dependance externe. Compile avec le .NET livre avec Windows.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading;

static class Program
{
    static readonly CultureInfo INV = CultureInfo.InvariantCulture;
    const int DEFAULT_PORT = 8422;

    class LapRecord
    {
        public int Lap;
        public double Time;
        public bool Valid;
        public double TopSpeed;
        public double CoastPct;
    }

    static string J(double v, int d)
    {
        double r = Math.Round(v, d);
        if (double.IsNaN(r) || double.IsInfinity(r)) return "null";
        return r.ToString(INV);
    }


    /// <summary>Extrait la valeur texte d'un champ JSON simple. null si absent.</summary>
    static string JsonField(string json, string field)
    {
        if (string.IsNullOrEmpty(json)) return null;
        string needle = "\"" + field + "\"";
        int i = json.IndexOf(needle, StringComparison.Ordinal);
        if (i < 0) return null;
        i += needle.Length;
        // saute espaces et deux-points
        while (i < json.Length && (json[i] == ' ' || json[i] == ':' || json[i] == '\t')) i++;
        if (i >= json.Length) return null;

        var sb = new StringBuilder();
        if (json[i] == '"')
        {
            i++;
            while (i < json.Length && json[i] != '"')
            {
                if (json[i] == '\\' && i + 1 < json.Length) i++;   // echappement
                sb.Append(json[i]);
                i++;
            }
            return sb.ToString();
        }
        // valeur non quotee : nombre, true/false, null
        while (i < json.Length && json[i] != ',' && json[i] != '}' && json[i] != ']')
        {
            sb.Append(json[i]);
            i++;
        }
        string raw = sb.ToString().Trim();
        return raw.Length > 0 ? raw : null;
    }

    /// <summary>Lit toutes les fiches du disque et les renvoie au navigateur.</summary>
    static void SendFiches(WebServer server, string fichesDir)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append("{\"type\":\"fiches\",\"list\":[");
            bool first = true;
            foreach (var f in Directory.GetFiles(fichesDir, "*.json"))
            {
                string content;
                try { content = File.ReadAllText(f, Encoding.UTF8); }
                catch { continue; }
                if (string.IsNullOrEmpty(content)) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append(content);
            }
            sb.Append("]}");
            server.Broadcast(sb.ToString());
        }
        catch { }
    }

    /// <summary>Temperature moyenne des 4 pneus, pour l'affichage mobile.</summary>
    static double TireTempAvg(byte[] b)
    {
        double sum = 0; int n = 0;
        for (int i = 0; i < 4; i++)
        {
            int bas = Telemetry.OFF_TireTemp + i * 24;
            for (int j = 0; j < 3; j++)
            {
                float v = BitConverter.ToSingle(b, bas + j * 4);
                if (v > 0 && v < 250) { sum += v; n++; }
            }
        }
        return n > 0 ? sum / n : -1.0;
    }

    /// <summary>Trouve l'IP locale du PC (192.168.x.x) pour l'acces mobile.</summary>
    static string GetLanIp()
    {
        string fallback = null;
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                var t = ni.NetworkInterfaceType;
                if (t == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    string ip = ua.Address.ToString();
                    if (ip.StartsWith("127.")) continue;
                    // priorise les adresses de reseau local classiques
                    if (ip.StartsWith("192.168.") || ip.StartsWith("10.") ||
                        (ip.StartsWith("172.") && IsPrivate172(ip)))
                        return ip;
                    if (fallback == null) fallback = ip;   // sinon, garde la 1ere IPv4 valide
                }
            }
        }
        catch { }
        return fallback;
    }
    static bool IsPrivate172(string ip)
    {
        var parts = ip.Split('.');
        if (parts.Length < 2) return false;
        int b; return int.TryParse(parts[1], out b) && b >= 16 && b <= 31;
    }

    static string JsonStr(string s)
    {
        if (s == null) return "\"\"";
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            if (c == '"') sb.Append("\\\"");
            else if (c == '\\') sb.Append("\\\\");
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else if (c == '\t') sb.Append("\\t");
            else if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4"));
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    static void Main(string[] args)
    {
        string outDir = "laps";
        double hz = 60.0;
        int port = DEFAULT_PORT;
        bool keepInvalid = false, noBrowser = false;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--out" && i + 1 < args.Length) outDir = args[++i];
            else if (args[i] == "--hz" && i + 1 < args.Length)
                double.TryParse(args[++i], NumberStyles.Any, INV, out hz);
            else if (args[i] == "--port" && i + 1 < args.Length)
                int.TryParse(args[++i], out port);
            else if (args[i] == "--keep-invalid") keepInvalid = true;
            else if (args[i] == "--no-browser") noBrowser = true;
            else if (args[i] == "--help" || args[i] == "-h")
            {
                Console.WriteLine("APEX — options :");
                Console.WriteLine("  --out DOSSIER      dossier de sortie (defaut: laps)");
                Console.WriteLine("  --hz N             frequence d'echantillonnage (defaut: 60)");
                Console.WriteLine("  --port N           port du dashboard (defaut: 8422)");
                Console.WriteLine("  --keep-invalid     garde les tours passes par les stands");
                Console.WriteLine("  --no-browser       n'ouvre pas le navigateur");
                Console.WriteLine("  --key TOUCHE       raccourci d'annonce (defaut: C)");
                return;
            }
        }
        if (hz < 1) hz = 1;
        int periodMs = (int)Math.Max(1, 1000.0 / hz);

        Directory.CreateDirectory(outDir);
        string fichesDir = Path.Combine(outDir, "fiches");
        Directory.CreateDirectory(fichesDir);
        string sessionLog = Path.Combine(outDir, "_session.jsonl");
        var header = Telemetry.BuildHeader();

        // ---- dashboard embarque -----------------------------------------
        string html = EmbeddedDashboard.Html;
        var server = new WebServer(html, port);
        server.Start();
        string url = "http://localhost:" + server.Port + "/";
        string lanIp = GetLanIp();

        Console.Title = "APEX — R3E Telemetry Coach";
        Console.WriteLine(new string('=', 64));
        Console.WriteLine("  APEX — R3E Telemetry Coach");
        Console.WriteLine(new string('=', 64));
        Console.WriteLine("  Dashboard PC : " + url);
        if (lanIp != null)
            Console.WriteLine("  Sur le reseau : http://" + lanIp + ":" + server.Port + "/");
        Console.WriteLine("  Sortie       : " + Path.GetFullPath(outDir));
        Console.WriteLine("  Frequence    : " + hz.ToString(INV) + " Hz");
        Console.WriteLine(new string('=', 64));
        Console.WriteLine();

        if (!noBrowser)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { Console.WriteLine("  (ouvre " + url + " dans ton navigateur)"); }
        }

        Console.WriteLine("En attente de RaceRoom...  (Ctrl+C pour arreter)");
        Console.WriteLine();

        // ---- fiches de session (module Anti-Oubli) -----------------------
        // Le navigateur calcule la fiche (il a le moteur d'analyse) et l'envoie
        // ici ; l'exe l'ecrit sur disque. Au demarrage on renvoie la liste.
        server.MessageReceived += delegate(string msg)
        {
            if (msg == null) return;

            if (msg.IndexOf("\"savefiche\"", StringComparison.Ordinal) >= 0)
            {
                string key = JsonField(msg, "key");
                string payload = JsonField(msg, "data");
                if (key == null || payload == null) return;
                try
                {
                    string safe = Telemetry.Safe(key);
                    if (safe.Length == 0) return;
                    File.WriteAllText(Path.Combine(fichesDir, safe + ".json"), payload,
                                      new UTF8Encoding(false));
                    Console.WriteLine("  Fiche enregistree : " + safe);
                }
                catch (Exception ex) { Console.WriteLine("  [!] Fiche non sauvee : " + ex.Message); }
            }
            else if (msg.IndexOf("\"listfiches\"", StringComparison.Ordinal) >= 0)
            {
                SendFiches(server, fichesDir);
            }
        };

        MemoryMappedFile mmf = null;
        MemoryMappedViewAccessor acc = null;
        byte[] buf = new byte[Telemetry.SHARED_SIZE];

        bool connected = false, warnedVersion = false, lapValid = true;
        var rows = new List<string>();
        var lapHistory = new List<LapRecord>();

        // Reference pour le delta en direct : temps du meilleur tour a chaque
        // distance. On compare la position courante a ce qu'on faisait au meme
        // endroit sur le meilleur tour. Grille de 5 m sur 12 km max (Nordschleife).
        const double DELTA_STEP = 5.0;
        const int DELTA_SLOTS = 2600;
        var refTimeByDist = new double[DELTA_SLOTS];
        var curTimeByDist = new double[DELTA_SLOTS];
        for (int k = 0; k < DELTA_SLOTS; k++) { refTimeByDist[k] = double.NaN; curTimeByDist[k] = double.NaN; }
        double refBestTime = double.MaxValue;
        double lapStartSim = -1;
        double? t0 = null;
        int lastLaps = -999;
        double lastDist = 0, lastSim = double.MinValue;
        string trackLabel = "", trackDir = null, curTrack = "", curLayout = "";
        int saved = 0;
        double bestTimeSeen = 0;

        // metriques du tour en cours
        double coastAcc = 0, topSpeed = 0, prevT = -1;

        var sw = Stopwatch.StartNew();
        long lastPush = 0;
        // etat de course, partage entre la diffusion et le HUD
        int pos = -1, nCars = 0, totalLaps = -1;
        double fuelPct = -1;
        int lastClients = 0;

        Console.CancelKeyPress += (s, e) =>
        {
            Console.WriteLine();
            Console.WriteLine("Arret. " + saved + " tour(s) dans " + Path.GetFullPath(outDir));
            try { server.Stop(); } catch { }
        };

        while (true)
        {
            if (acc == null)
            {
                try
                {
                    mmf = MemoryMappedFile.OpenExisting("$R3E", MemoryMappedFileRights.Read);
                    acc = mmf.CreateViewAccessor(0, Telemetry.SHARED_SIZE, MemoryMappedFileAccess.Read);
                }
                catch
                {
                    if (connected)
                    {
                        Console.WriteLine();
                        Console.WriteLine("[!] Connexion perdue. En attente...");
                        connected = false;
                        rows.Clear(); t0 = null; lastLaps = -999;
                        server.Broadcast("{\"type\":\"state\",\"msg\":\"RaceRoom ferme — en attente...\"}");
                    }
                    Thread.Sleep(500);
                    continue;
                }
            }

            try { acc.ReadArray(0, buf, 0, Telemetry.SHARED_SIZE); }
            catch
            {
                try { acc.Dispose(); mmf.Dispose(); } catch { }
                acc = null; mmf = null;
                continue;
            }

            int vMaj = BitConverter.ToInt32(buf, Telemetry.OFF_VersionMajor);
            int vMin = BitConverter.ToInt32(buf, Telemetry.OFF_VersionMinor);
            if (vMaj == 0) { Thread.Sleep(300); continue; }

            if (!connected)
            {
                connected = true;
                Console.WriteLine("[OK] Connecte a la shared memory R3E.");
            }
            if (!warnedVersion && vMaj != Telemetry.EXPECT_MAJOR)
            {
                warnedVersion = true;
                Console.WriteLine("[!] Version SHM " + vMaj + "." + vMin + " != " +
                    Telemetry.EXPECT_MAJOR + "." + Telemetry.EXPECT_MINOR + " attendue.");
                Console.WriteLine("    Les donnees peuvent etre decalees.");
            }

            bool skip = BitConverter.ToInt32(buf, Telemetry.OFF_GameInMenus) == 1
                     || BitConverter.ToInt32(buf, Telemetry.OFF_GameInReplay) == 1
                     || BitConverter.ToInt32(buf, Telemetry.OFF_GamePaused) == 1
                     || BitConverter.ToInt32(buf, Telemetry.OFF_GameInGarage) == 1;
            if (skip)
            {
                if (rows.Count > 0)
                {
                    rows.Clear(); t0 = null; lastLaps = -999;
                    coastAcc = 0; topSpeed = 0; prevT = -1;
                }
                Thread.Sleep(200);
                continue;
            }

            string track = Telemetry.ReadStr(buf, Telemetry.OFF_TrackName, 64);
            string layout = Telemetry.ReadStr(buf, Telemetry.OFF_LayoutName, 64);
            string label = track + " - " + layout;
            if (label != trackLabel)
            {
                trackLabel = label; curTrack = track; curLayout = layout;
                trackDir = Path.Combine(outDir, Telemetry.Safe(track) + "__" + Telemetry.Safe(layout));
                Directory.CreateDirectory(trackDir);
                float len = BitConverter.ToSingle(buf, Telemetry.OFF_LayoutLength);
                Console.WriteLine();
                Console.WriteLine(">>> Circuit : " + label + "  (" + len.ToString("0", INV) + " m)");
                rows.Clear(); t0 = null; lastLaps = -999;
                lapHistory.Clear(); bestTimeSeen = 0;
                coastAcc = 0; topSpeed = 0; prevT = -1;
            }

            double simT = BitConverter.ToDouble(buf, Telemetry.OFF_SimTime);
            int laps = BitConverter.ToInt32(buf, Telemetry.OFF_CompletedLaps);
            float dist = BitConverter.ToSingle(buf, Telemetry.OFF_LapDistance);
            float speedMs = BitConverter.ToSingle(buf, Telemetry.OFF_CarSpeed);
            float thr = BitConverter.ToSingle(buf, Telemetry.OFF_Throttle);
            float brk = BitConverter.ToSingle(buf, Telemetry.OFF_Brake);
            float steer = BitConverter.ToSingle(buf, Telemetry.OFF_SteerRaw);
            int gear = BitConverter.ToInt32(buf, Telemetry.OFF_Gear);
            float rps = BitConverter.ToSingle(buf, Telemetry.OFF_EngineRps);
            float curLap = BitConverter.ToSingle(buf, Telemetry.OFF_LapTimeCurrent);
            float bestLap = BitConverter.ToSingle(buf, Telemetry.OFF_LapTimeBest);
            double speedKmh = speedMs * 3.6;

            // ---- delta en direct : temps a cette distance vs meilleur tour ----
            double liveDelta = double.NaN;
            if (lapStartSim < 0 || (lastDist > 50 && dist < lastDist - 50)) lapStartSim = simT;
            double lapElapsed = (lapStartSim >= 0) ? simT - lapStartSim : 0;
            int slot = (int)(dist / DELTA_STEP);
            if (slot >= 0 && slot < DELTA_SLOTS && lapElapsed >= 0)
            {
                // enregistre le temps courant a cette distance
                if (double.IsNaN(curTimeByDist[slot]) || lapElapsed < curTimeByDist[slot])
                    curTimeByDist[slot] = lapElapsed;
                // compare a la reference, interpolee entre deux points de grille
                // pour lisser l'effet d'escalier du pas de 5 m.
                if (!double.IsNaN(refTimeByDist[slot]))
                {
                    double refHere = refTimeByDist[slot];
                    if (slot + 1 < DELTA_SLOTS && !double.IsNaN(refTimeByDist[slot + 1]))
                    {
                        double frac = (dist - slot * DELTA_STEP) / DELTA_STEP;
                        refHere = refTimeByDist[slot] * (1 - frac) + refTimeByDist[slot + 1] * frac;
                    }
                    liveDelta = lapElapsed - refHere;
                }
            }

            // ---- passage de ligne ----------------------------------------
            bool crossed = false;
            if (lastLaps != -999 && laps > lastLaps) crossed = true;
            else if (lastDist > 50 && dist < lastDist - 50 && dist < 50) crossed = true;
            lastLaps = laps;

            if (crossed && rows.Count > 0)
            {
                float lapTime = BitConverter.ToSingle(buf, Telemetry.OFF_LapTimePrev);
                bool ok = lapValid && lapTime > 0;
                double lapDur = prevT > 0 ? prevT : 0;
                double coastPct = lapDur > 0 ? coastAcc / lapDur * 100.0 : 0;

                // si ce tour est le meilleur et valide, il devient la reference
                // du delta en direct pour les tours suivants.
                if (ok && lapTime < refBestTime)
                {
                    refBestTime = lapTime;
                    Array.Copy(curTimeByDist, refTimeByDist, DELTA_SLOTS);
                }
                // reinitialise l'enregistrement du tour a venir
                for (int k = 0; k < DELTA_SLOTS; k++) curTimeByDist[k] = double.NaN;
                lapStartSim = simT;

                if (ok || keepInvalid)
                {
                    string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    string fname = "lap_" + laps.ToString("000") + "_" +
                        Telemetry.FmtTimeFile(lapTime) + (ok ? "" : "_INVALIDE") + "_" + stamp + ".csv";
                    string path = Path.Combine(trackDir, fname);

                    try
                    {
                        using (var w = new StreamWriter(path, false, new UTF8Encoding(false)))
                        {
                            w.WriteLine(string.Join(",", header));
                            foreach (var r in rows) w.WriteLine(r);
                        }

                        string json = "{\"time\":" + JsonStr(stamp)
                            + ",\"track\":" + JsonStr(track)
                            + ",\"layout\":" + JsonStr(layout)
                            + ",\"layout_length_m\":" + J(BitConverter.ToSingle(buf, Telemetry.OFF_LayoutLength), 1)
                            + ",\"lap_number\":" + laps
                            + ",\"lap_time_s\":" + (lapTime > 0 ? J(lapTime, 3) : "null")
                            + ",\"valid\":" + (ok ? "true" : "false")
                            + ",\"samples\":" + rows.Count
                            + ",\"file\":" + JsonStr(Path.GetFileName(trackDir) + "/" + fname) + "}";
                        File.AppendAllText(sessionLog, json + Environment.NewLine, new UTF8Encoding(false));
                        saved++;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("  [!] Ecriture impossible : " + ex.Message);
                    }

                    if (ok && (bestTimeSeen == 0 || lapTime < bestTimeSeen)) bestTimeSeen = lapTime;
                    string flag = ok ? "" : "  [INVALIDE]";
                    string star = (ok && Math.Abs(lapTime - bestTimeSeen) < 0.001) ? "  <-- MEILLEUR" : "";
                    Console.WriteLine("  Tour " + laps.ToString().PadLeft(3) + "  " +
                        Telemetry.FmtTime(lapTime) + "  (" + rows.Count + " pts)" + flag + star);

                    lapHistory.Add(new LapRecord {
                        Lap = laps, Time = lapTime, Valid = ok,
                        TopSpeed = topSpeed, CoastPct = coastPct });

                    // trace compacte du tour, pour l'analyse des virages cote navigateur.
                    // On sous-echantillonne a ~10 Hz : suffisant pour detecter les
                    // virages et les points de freinage, et ca garde le message leger.
                    var tr = new StringBuilder(8192);
                    tr.Append(",\"trace\":[");
                    int step = Math.Max(1, (int)Math.Round(hz / 10.0));
                    bool first = true;
                    for (int i = 0; i < rows.Count; i += step)
                    {
                        var f = rows[i].Split(',');
                        if (f.Length < 9) continue;
                        if (!first) tr.Append(',');
                        first = false;
                        // [t, distance, vitesse, gaz, frein, direction, x, z, rapport]
                        tr.Append('[').Append(f[0]).Append(',').Append(f[2]).Append(',')
                          .Append(f[4]).Append(',').Append(f[5]).Append(',')
                          .Append(f[6]).Append(',').Append(f[8]).Append(',')
                          .Append(f[12]).Append(',').Append(f[14]).Append(',')
                          .Append(f[9]).Append(']');
                    }
                    tr.Append(']');

                    server.Broadcast("{\"type\":\"lap\",\"lap\":" + laps
                        + ",\"time\":" + J(lapTime, 3)
                        + ",\"valid\":" + (ok ? "true" : "false")
                        + ",\"topSpeed\":" + J(topSpeed, 1)
                        + ",\"coastPct\":" + J(coastPct, 2)
                        + tr + "}");
                }
                else
                {
                    Console.WriteLine("  Tour " + laps.ToString().PadLeft(3) + "  ignore (invalide)");
                }

                rows.Clear();
                t0 = null;
                lapValid = true;
                coastAcc = 0; topSpeed = 0; prevT = -1;
            }

            // ---- validite --------------------------------------------------
            int pitState = BitConverter.ToInt32(buf, Telemetry.OFF_PitState);
            if (BitConverter.ToInt32(buf, Telemetry.OFF_InPitlane) == 1 ||
                pitState == 2 || pitState == 3 || pitState == 4)
                lapValid = false;

            // ---- echantillon ------------------------------------------------
            if (t0 == null) { t0 = simT; lastSim = double.MinValue; }
            if (!(rows.Count > 0 && simT <= lastSim))
            {
                double tLap = simT - t0.Value;
                rows.Add(Telemetry.BuildRow(buf, tLap, simT));
                lastSim = simT;

                // metriques a la volee
                if (speedKmh > topSpeed) topSpeed = speedKmh;
                if (prevT >= 0)
                {
                    double dt = tLap - prevT;
                    if (dt > 0 && dt < 1 && thr < 0.05 && brk < 0.05 && speedKmh > 30)
                        coastAcc += dt;
                }
                prevT = tLap;
            }

            lastDist = dist;

            // annonce la touche courante quand un nouveau client arrive
            if (server.ClientCount > 0 && server.ClientCount != lastClients)
            {
                lastClients = server.ClientCount;
                SendFiches(server, fichesDir);
            }
            else if (server.ClientCount == 0) lastClients = 0;

            // ---- diffusion live (limitee a ~20 Hz) ---------------------------
            if (sw.ElapsedMilliseconds - lastPush >= 50 && server.ClientCount > 0)
            {
                lastPush = sw.ElapsedMilliseconds;
                double delta = liveDelta;   // delta positionnel en direct

                // ---- donnees de course ----
                pos = BitConverter.ToInt32(buf, Telemetry.OFF_Position);
                int posCls = BitConverter.ToInt32(buf, Telemetry.OFF_PositionClass);
                nCars = BitConverter.ToInt32(buf, Telemetry.OFF_NumCars);
                totalLaps = BitConverter.ToInt32(buf, Telemetry.OFF_NumberOfLaps);
                float fuelLeft = BitConverter.ToSingle(buf, Telemetry.OFF_FuelLeft);
                float fuelCap = BitConverter.ToSingle(buf, Telemetry.OFF_FuelCapacity);
                float fuelPerLap = BitConverter.ToSingle(buf, Telemetry.OFF_FuelPerLap);
                float sessRemain = BitConverter.ToSingle(buf, Telemetry.OFF_SessionTimeRemaining);
                float dFront = BitConverter.ToSingle(buf, Telemetry.OFF_DeltaFront);
                float dBehind = BitConverter.ToSingle(buf, Telemetry.OFF_DeltaBehind);
                int penalties = BitConverter.ToInt32(buf, Telemetry.OFF_NumPenalties);
                int pitWin = BitConverter.ToInt32(buf, Telemetry.OFF_PitWindowStatus);
                int sessPhase = BitConverter.ToInt32(buf, Telemetry.OFF_SessionPhase);

                // carburant : pourcentage + autonomie estimee
                fuelPct = (fuelCap > 0 && fuelLeft >= 0) ? fuelLeft / fuelCap * 100.0 : -1;
                double lapsLeft = (fuelPerLap > 0.01 && fuelLeft > 0) ? fuelLeft / fuelPerLap : -1;

                // tours restants : soit compte de tours, soit estime au temps
                double lapsRemain = -1;
                if (totalLaps > 0) lapsRemain = totalLaps - laps;

                // usure pneus : on prend la plus usee des 4
                double wearWorst = -1;
                if (BitConverter.ToInt32(buf, Telemetry.OFF_TireWearActive) == 1)
                {
                    wearWorst = 1.0;
                    for (int i = 0; i < 4; i++)
                    {
                        float w = BitConverter.ToSingle(buf, Telemetry.OFF_TireWear + i * 4);
                        if (w >= 0 && w < wearWorst) wearWorst = w;
                    }
                    wearWorst = wearWorst * 100.0;
                }

                // drapeaux
                string flag = "";
                if (BitConverter.ToInt32(buf, Telemetry.OFF_FlagCheckered) == 1) flag = "damier";
                else if (BitConverter.ToInt32(buf, Telemetry.OFF_FlagBlack) == 1) flag = "noir";
                else if (BitConverter.ToInt32(buf, Telemetry.OFF_FlagBlue) == 1) flag = "bleu";
                else if (BitConverter.ToInt32(buf, Telemetry.OFF_FlagYellow) == 1) flag = "jaune";
                else if (BitConverter.ToInt32(buf, Telemetry.OFF_FlagWhite) == 1) flag = "blanc";

                var sb = new StringBuilder(512);
                sb.Append("{\"type\":\"telemetry\",\"speed\":").Append(J(speedKmh, 1))
                  .Append(",\"gear\":").Append(gear)
                  .Append(",\"rpm\":").Append(J(rps * 9.5492966, 0))
                  .Append(",\"throttle\":").Append(J(thr, 3))
                  .Append(",\"brake\":").Append(J(brk, 3))
                  .Append(",\"steer\":").Append(J(steer, 3))
                  .Append(",\"cur\":").Append(curLap > 0 ? J(curLap, 3) : "null")
                  .Append(",\"best\":").Append(bestLap > 0 ? J(bestLap, 3) : "null")
                  .Append(",\"delta\":").Append(double.IsNaN(delta) ? "null" : J(delta, 3))
                  .Append(",\"px\":").Append(J(BitConverter.ToDouble(buf, Telemetry.OFF_LocalGX + 16), 1))
                  .Append(",\"pz\":").Append(J(BitConverter.ToDouble(buf, Telemetry.OFF_LocalGX), 1))
                  .Append(",\"dist\":").Append(J(dist, 1))
                  .Append(",\"track\":").Append(JsonStr(curTrack))
                  .Append(",\"layout\":").Append(JsonStr(curLayout))
                  .Append(",\"pos\":").Append(pos)
                  .Append(",\"posClass\":").Append(posCls)
                  .Append(",\"cars\":").Append(nCars)
                  .Append(",\"lap\":").Append(laps)
                  .Append(",\"totalLaps\":").Append(totalLaps)
                  .Append(",\"lapsRemain\":").Append(J(lapsRemain, 0))
                  .Append(",\"fuelLeft\":").Append(J(fuelLeft, 2))
                  .Append(",\"fuelPct\":").Append(J(fuelPct, 1))
                  .Append(",\"fuelLaps\":").Append(J(lapsLeft, 1))
                  .Append(",\"sessRemain\":").Append(J(sessRemain, 0))
                  .Append(",\"dFront\":").Append(J(dFront, 2))
                  .Append(",\"dBehind\":").Append(J(dBehind, 2))
                  .Append(",\"penalties\":").Append(penalties)
                  .Append(",\"tireWear\":").Append(J(wearWorst, 1))
                  .Append(",\"tireTemp\":").Append(J(TireTempAvg(buf), 0))
                  .Append(",\"pitWindow\":").Append(pitWin)
                  .Append(",\"phase\":").Append(sessPhase)
                  .Append(",\"flag\":").Append(JsonStr(flag))
                  .Append("}");
                server.Broadcast(sb.ToString());
            }

            Thread.Sleep(periodMs);
        }
    }
}
