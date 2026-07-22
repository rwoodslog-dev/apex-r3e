// Hook clavier global Windows.
// Permet de capter une touche meme quand RaceRoom est au premier plan.
//
// Attention : le delegue doit rester reference cote managed pour toute la
// duree du hook, sinon le GC le collecte et Windows appelle un pointeur mort
// -> crash. C'est le piege classique de SetWindowsHookEx en C#.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

class KeyHook : IDisposable
{
    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x0100;
    const int WM_SYSKEYDOWN = 0x0104;

    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern IntPtr GetModuleHandle(string lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("user32.dll")]
    static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr w, IntPtr l);

    // IMPORTANT : champ d'instance, pas une variable locale.
    // Garde le delegue vivant tant que le hook existe.
    readonly HookProc _proc;
    IntPtr _hook = IntPtr.Zero;
    Thread _thread;
    volatile bool _running;

    /// <summary>Code de touche virtuelle surveille (VK). 0 = desactive.</summary>
    public int WatchedKey;

    /// <summary>Appele (sur le thread du hook) quand la touche est pressee.</summary>
    public event Action Triggered;

    /// <summary>Renseigne quand on est en mode apprentissage de touche.</summary>
    public event Action<int> KeyLearned;

    volatile bool _learning;

    public KeyHook(int vk)
    {
        WatchedKey = vk;
        _proc = Callback;   // reference conservee
    }

    public bool Start()
    {
        if (_hook != IntPtr.Zero) return true;
        _running = true;

        // Le hook bas niveau exige une pompe de messages : on lui donne
        // son propre thread pour ne pas bloquer la boucle telemetrie.
        var ready = new ManualResetEventSlim(false);
        bool ok = false;

        _thread = new Thread(() =>
        {
            try
            {
                using (var mod = Process.GetCurrentProcess().MainModule)
                {
                    _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc,
                                             GetModuleHandle(mod.ModuleName), 0);
                }
                ok = _hook != IntPtr.Zero;
            }
            catch { ok = false; }

            ready.Set();
            if (!ok) return;

            // pompe de messages Win32 (indispensable pour un hook bas niveau)
            MSG msg;
            while (_running && GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        });
        _thread.IsBackground = true;
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        ready.Wait(3000);
        return ok;
    }

    /// <summary>Prochaine touche pressee = nouvelle touche surveillee.</summary>
    public void LearnNextKey() { _learning = true; }

    IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
            {
                int vk = Marshal.ReadInt32(lParam);
                if (_learning)
                {
                    _learning = false;
                    WatchedKey = vk;
                    var h = KeyLearned;
                    if (h != null) { try { h(vk); } catch { } }
                }
                else if (vk == WatchedKey && WatchedKey != 0)
                {
                    var h = Triggered;
                    if (h != null) { try { h(); } catch { } }
                }
            }
        }
        // On ne consomme jamais la touche : le jeu doit la recevoir aussi.
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        _running = false;
        if (_hook != IntPtr.Zero)
        {
            try { UnhookWindowsHookEx(_hook); } catch { }
            _hook = IntPtr.Zero;
        }
    }

    /// <summary>Nom lisible d'un code de touche virtuelle.</summary>
    public static string KeyName(int vk)
    {
        if (vk >= 'A' && vk <= 'Z') return ((char)vk).ToString();
        if (vk >= '0' && vk <= '9') return ((char)vk).ToString();
        if (vk >= 0x70 && vk <= 0x7B) return "F" + (vk - 0x6F);      // F1..F12
        if (vk >= 0x60 && vk <= 0x69) return "Num" + (vk - 0x60);    // pave num
        switch (vk)
        {
            case 0x20: return "Espace";
            case 0x0D: return "Entree";
            case 0x09: return "Tab";
            case 0x1B: return "Echap";
            case 0x2D: return "Inser";
            case 0x2E: return "Suppr";
            case 0x24: return "Origine";
            case 0x23: return "Fin";
            case 0x21: return "PagePrec";
            case 0x22: return "PageSuiv";
            case 0x6A: return "Num*";
            case 0x6B: return "Num+";
            case 0x6D: return "Num-";
            case 0x6E: return "Num.";
            case 0x6F: return "Num/";
            default: return "Touche " + vk;
        }
    }

    /// <summary>Convertit un nom simple ("C", "F9", "Num0") en code VK.</summary>
    public static int ParseKey(string name)
    {
        if (string.IsNullOrEmpty(name)) return 0;
        name = name.Trim();
        if (name.Length == 1)
        {
            char c = char.ToUpperInvariant(name[0]);
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) return (int)c;
        }
        string up = name.ToUpperInvariant();
        if (up.Length >= 2 && up[0] == 'F')
        {
            int n;
            if (int.TryParse(up.Substring(1), out n) && n >= 1 && n <= 12) return 0x6F + n;
        }
        if (up.StartsWith("NUM"))
        {
            int n;
            if (int.TryParse(up.Substring(3), out n) && n >= 0 && n <= 9) return 0x60 + n;
        }
        switch (up)
        {
            case "ESPACE": case "SPACE": return 0x20;
            case "ENTREE": case "ENTER": return 0x0D;
            case "TAB": return 0x09;
            case "ECHAP": case "ESC": return 0x1B;
            case "INSER": case "INSERT": return 0x2D;
            case "SUPPR": case "DELETE": return 0x2E;
        }
        return 0;
    }
}
