// HUD superpose au jeu.
//
// Fenetre transparente, toujours au premier plan, click-through : les clics
// passent au jeu en dessous. Rendu GDI+ via UpdateLayeredWindow, ce qui donne
// une vraie transparence par pixel (pas de couleur-cle qui bave).
//
// IMPORTANT : ne fonctionne PAS en plein ecran exclusif. Le jeu doit tourner
// en fenetre sans bordure. C'est une contrainte Windows, pas du code.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

class HudOverlay : IDisposable
{
    // ---- Win32 ----------------------------------------------------------
    const int WS_EX_LAYERED = 0x80000;
    const int WS_EX_TRANSPARENT = 0x20;
    const int WS_EX_TOPMOST = 0x8;
    const int WS_EX_TOOLWINDOW = 0x80;      // pas d'icone dans la barre des taches
    const int WS_EX_NOACTIVATE = 0x8000000; // ne vole jamais le focus au jeu
    const int WS_POPUP = unchecked((int)0x80000000);
    const int ULW_ALPHA = 2;
    const int AC_SRC_OVER = 0;
    const int AC_SRC_ALPHA = 1;
    const int SW_SHOWNOACTIVATE = 4;
    static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    const uint SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10;

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct BLENDFUNCTION
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WNDCLASSEX
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)] struct MSG
    {
        public IntPtr hwnd; public uint message; public IntPtr wParam, lParam;
        public uint time; public POINT pt;
    }

    delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowEx(int exStyle, string cls, string name, int style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after,
        int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG m, IntPtr h, uint a, uint b);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG m);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dcDst,
        ref POINT ptDst, ref SIZE size, IntPtr dcSrc, ref POINT ptSrc, int key,
        ref BLENDFUNCTION blend, int flags);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);

    // ---- etat -----------------------------------------------------------
    IntPtr hwnd = IntPtr.Zero;
    Thread uiThread;
    volatile bool running;
    WndProcDelegate wndProcRef;      // garder la reference vivante (sinon GC -> crash)
    readonly object drawLock = new object();

    public int Width = 300, Height = 132;

    /// <summary>Hauteur necessaire selon les widgets actifs. Evite les textes coupes.</summary>
    public int NeededHeight()
    {
        int h = 16;
        if (ShowDelta)  h += 36;
        if (ShowTimes)  h += 21;
        if (ShowRace)   h += 19;
        if (ShowInputs) h += 24;
        if (ShowCoach)  h += 44;
        return Math.Max(46, h);
    }
    public int PosX = -1, PosY = -1;   // -1 = coin haut droit par defaut
    public double Opacity = 0.92;
    public bool ShowDelta = true, ShowTimes = true, ShowInputs = false,
                ShowRace = false, ShowCoach = true;

    // donnees affichees
    public volatile string CoachLine1 = "", CoachLine2 = "";
    public double Delta = double.NaN, CurLap = -1, BestLap = -1;
    public double Throttle, Brake, Speed;
    public int Gear, Pos, Cars, Lap, TotalLaps;
    public double FuelPct = -1;
    long coachUntil = 0;   // ticks jusqu'auxquels le conseil reste affiche

    public bool IsRunning { get { return running && hwnd != IntPtr.Zero; } }

    public void SetCoach(string l1, string l2, int seconds)
    {
        CoachLine1 = l1 ?? "";
        CoachLine2 = l2 ?? "";
        coachUntil = DateTime.UtcNow.Ticks + TimeSpan.TicksPerSecond * seconds;
    }

    public bool Start()
    {
        if (running) return true;
        running = true;
        bool ok = false;
        var ready = new ManualResetEventSlim(false);

        uiThread = new Thread(() =>
        {
            try
            {
                wndProcRef = new WndProcDelegate(DefWindowProc);
                string cls = "ApexHudOverlay";
                var wc = new WNDCLASSEX();
                wc.cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEX));
                wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProcRef);
                wc.hInstance = GetModuleHandle(null);
                wc.lpszClassName = cls;
                RegisterClassEx(ref wc);   // echoue sans dommage si deja enregistree

                int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
                int x = PosX >= 0 ? PosX : Math.Max(0, sw - Width - 28);
                int y = PosY >= 0 ? PosY : 28;

                hwnd = CreateWindowEx(
                    WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST
                        | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                    cls, "APEX HUD", WS_POPUP,
                    x, y, Width, Height,
                    IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

                ok = hwnd != IntPtr.Zero;
                if (ok)
                {
                    ShowWindow(hwnd, SW_SHOWNOACTIVATE);
                    SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                                 SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                }
            }
            catch { ok = false; }

            ready.Set();
            if (!ok) { running = false; return; }

            MSG msg;
            while (running && GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        });
        uiThread.IsBackground = true;
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();

        ready.Wait(3000);
        if (!ok) running = false;
        return ok;
    }

    /// <summary>Redessine le HUD et l'envoie a l'ecran.</summary>
    public void Render()
    {
        if (!IsRunning) return;
        lock (drawLock)
        {
            using (var bmp = DrawBitmap())
            {
                if (bmp == null) return;
                Push(bmp);
            }
        }
    }

    /// <summary>Construit l'image du HUD. Expose pour pouvoir la tester.</summary>
    public Bitmap DrawBitmap()
    {
        var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(Color.Transparent);

            int a = (int)Math.Round(Math.Max(0, Math.Min(1, Opacity)) * 255);
            var bg = new SolidBrush(Color.FromArgb((int)(a * 0.86), 8, 9, 11));
            var edge = new Pen(Color.FromArgb((int)(a * 0.55), 40, 43, 48), 1f);
            var yellow = Color.FromArgb(a, 255, 198, 42);
            var white = Color.FromArgb(a, 244, 245, 246);
            var dim = Color.FromArgb((int)(a * 0.72), 154, 160, 166);
            var red = Color.FromArgb(a, 224, 80, 60);
            var green = Color.FromArgb(a, 95, 191, 106);

            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = RoundRect(rect, 9))
            {
                g.FillPath(bg, path);
                g.DrawPath(edge, path);
            }
            // liseré jaune a gauche : marque APEX
            using (var accent = new SolidBrush(yellow))
                g.FillRectangle(accent, 0, 9, 2, Height - 18);

            using (var fSmall = new Font("Segoe UI", 6.8f, FontStyle.Bold))
            using (var fMono  = new Font("Consolas", 15f, FontStyle.Bold))
            using (var fMonoS = new Font("Consolas", 9.5f, FontStyle.Regular))
            using (var fText  = new Font("Segoe UI", 8.6f, FontStyle.Regular))
            using (var bWhite = new SolidBrush(white))
            using (var bDim   = new SolidBrush(dim))
            using (var bY     = new SolidBrush(yellow))
            {
                int x = 11, y = 8;

                if (ShowDelta)
                {
                    g.DrawString("DELTA", fSmall, bDim, x, y);
                    string dtxt = (double.IsNaN(Delta)) ? "--.---"
                        : (Delta > 0 ? "+" : "") + Delta.ToString("0.000",
                            System.Globalization.CultureInfo.InvariantCulture);
                    var col = double.IsNaN(Delta) ? dim : (Delta <= 0 ? green : red);
                    using (var b2 = new SolidBrush(col))
                        g.DrawString(dtxt, fMono, b2, x - 2, y + 10);
                    y += 36;
                }

                if (ShowTimes)
                {
                    g.DrawString("TOUR", fSmall, bDim, x, y + 3);
                    g.DrawString(Fmt(CurLap), fMonoS, bWhite, x + 34, y);
                    // la 2e colonne demarre apres la largeur reelle du 1er temps
                    int col2 = x + 34 + (int)g.MeasureString("0:00.000", fMonoS).Width + 14;
                    if (col2 + 96 <= Width - 8)
                    {
                        g.DrawString("BEST", fSmall, bDim, col2, y + 3);
                        g.DrawString(Fmt(BestLap), fMonoS, bY, col2 + 32, y);
                    }
                    y += 21;
                }

                if (ShowRace)
                {
                    string s = (Pos > 0 ? "P" + Pos + (Cars > 0 ? "/" + Cars : "") : "--")
                             + "   T" + (Lap >= 0 ? Lap.ToString() : "-")
                             + (TotalLaps > 0 ? "/" + TotalLaps : "")
                             + (FuelPct >= 0 ? "   " + Math.Round(FuelPct) + "%" : "");
                    g.DrawString(s, fMonoS, bDim, x, y);
                    y += 19;
                }

                if (ShowInputs)
                {
                    int bw = Width - 22, bh = 5;
                    using (var track = new SolidBrush(Color.FromArgb((int)(a * .5), 30, 32, 36)))
                    {
                        g.FillRectangle(track, x, y + 2, bw, bh);
                        g.FillRectangle(track, x, y + 11, bw, bh);
                    }
                    using (var bt = new SolidBrush(green))
                        g.FillRectangle(bt, x, y + 2, (int)(bw * Clamp01(Throttle)), bh);
                    using (var bb = new SolidBrush(red))
                        g.FillRectangle(bb, x, y + 11, (int)(bw * Clamp01(Brake)), bh);
                    y += 24;
                }

                // conseil du coach : reste affiche quelques secondes
                bool coachOn = ShowCoach && DateTime.UtcNow.Ticks < coachUntil
                               && !string.IsNullOrEmpty(CoachLine1);
                if (coachOn)
                {
                    using (var sep = new Pen(Color.FromArgb((int)(a * .35), 40, 43, 48), 1f))
                        g.DrawLine(sep, x, y, Width - 11, y);
                    y += 6;
                    g.DrawString(CoachLine1, fText, bY, x - 2, y);
                    if (!string.IsNullOrEmpty(CoachLine2))
                        g.DrawString(CoachLine2, fText, bWhite, x - 2, y + 14);
                }
            }
            bg.Dispose(); edge.Dispose();
        }
        return bmp;
    }

    static double Clamp01(double v){ return v < 0 ? 0 : (v > 1 ? 1 : v); }

    static string Fmt(double s)
    {
        if (s <= 0 || double.IsNaN(s)) return "--:--.---";
        int m = (int)(s / 60);
        double r = s - m * 60;
        return m + ":" + r.ToString("00.000",
            System.Globalization.CultureInfo.InvariantCulture);
    }

    static GraphicsPath RoundRect(Rectangle r, int rad)
    {
        var p = new GraphicsPath();
        int d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>Envoie le bitmap a la fenetre en conservant l'alpha par pixel.</summary>
    void Push(Bitmap bmp)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBitmap = IntPtr.Zero, oldBitmap = IntPtr.Zero;
        try
        {
            hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
            oldBitmap = SelectObject(memDc, hBitmap);

            var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
            var src = new POINT { X = 0, Y = 0 };
            var dst = new POINT { X = 0, Y = 0 };
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AC_SRC_ALPHA
            };
            // dst a 0,0 avec SWP_NOMOVE : la position vient de CreateWindowEx
            UpdateLayeredWindow(hwnd, screenDc, ref dst, ref size, memDc, ref src,
                                0, ref blend, ULW_ALPHA);
        }
        catch { }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
            if (hBitmap != IntPtr.Zero)
            {
                SelectObject(memDc, oldBitmap);
                DeleteObject(hBitmap);
            }
            DeleteDC(memDc);
        }
    }

    /// <summary>Place le HUD dans un coin de l'ecran.</summary>
    public void SetCorner(string corner)
    {
        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
        int m = 28;
        int x, y;
        switch ((corner ?? "").ToLowerInvariant())
        {
            case "tl": x = m; y = m; break;
            case "bl": x = m; y = Math.Max(0, sh - Height - m); break;
            case "br": x = Math.Max(0, sw - Width - m); y = Math.Max(0, sh - Height - m); break;
            case "tc": x = Math.Max(0, (sw - Width) / 2); y = m; break;
            case "bc": x = Math.Max(0, (sw - Width) / 2); y = Math.Max(0, sh - Height - m); break;
            default:   x = Math.Max(0, sw - Width - m); y = m; break;   // tr
        }
        Move(x, y);
    }

    /// <summary>Repositionne le HUD.</summary>
    public void Move(int x, int y)
    {
        PosX = x; PosY = y;
        if (hwnd != IntPtr.Zero)
            SetWindowPos(hwnd, HWND_TOPMOST, x, y, Width, Height, SWP_NOACTIVATE);
    }

    public void Dispose()
    {
        running = false;
        if (hwnd != IntPtr.Zero)
        {
            try { PostMessage(hwnd, 0x0012, IntPtr.Zero, IntPtr.Zero); } catch { } // WM_QUIT
            try { DestroyWindow(hwnd); } catch { }
            hwnd = IntPtr.Zero;
        }
    }
}
