// MonitorSwitch - Tray-Tool zum Umschalten des Monitoreingangs per DDC/CI (VCP 0x60).
//
// Gedacht für einen Monitor an mehreren PCs: Das Tool laeuft auf jedem PC, und dort wählt
// man aus, wohin ein Klick wechselt (normalerweise der Eingang des jeweils anderen PCs).
//
// Bauen (kein SDK nötig, nutzt den csc.exe aus .NET Framework 4.x):  build.cmd
//
// Bedienung:
//   - Linksklick aufs Tray-Icon oder Hotkey (Standard Strg+Alt+F12) -> zum Ziel-Eingang wechseln
//   - Rechtsklick -> Menü: Ziel wählen, direkt auf einen Eingang wechseln, Eingänge umbenennen,
//                    Monitor wählen, Autostart, Desktop-Verknüpfung
//   - Pixelreinigung (ASUS ROG OLED) per Menue oder --clean anstossen, optional automatische
//     Erinnerung alle n Stunden Nutzung (Popup mit Jetzt / 5 / 15 / 30 Min / Abbrechen)
//   - Kommandozeile: MonitorSwitch.exe --target | --set 0x11 | --list | --clean
//
// Konfiguration: MonitorSwitch.ini neben der .exe (Fallback: %APPDATA%\MonitorSwitch),
// wird vom Tray-Menü gepflegt. Jeder PC hat seine eigene.
// Bewusst C# 5 / .NET Framework 4.x, damit der in Windows eingebaute Compiler reicht.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("MonitorSwitch")]
[assembly: AssemblyProduct("MonitorSwitch")]
[assembly: AssemblyVersion("2.2.0.0")]

namespace MonitorSwitch
{
    // ------------------------------------------------------------------ DDC/CI
    static class Ddc
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szPhysicalMonitorDescription;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int left, top, right, bottom; }

        delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

        [DllImport("user32.dll")]
        static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
        [DllImport("dxva2.dll")]
        static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);
        [DllImport("dxva2.dll", CharSet = CharSet.Unicode)]
        static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint size, [Out] PHYSICAL_MONITOR[] monitors);
        [DllImport("dxva2.dll")]
        static extern bool DestroyPhysicalMonitors(uint size, PHYSICAL_MONITOR[] monitors);
        [DllImport("dxva2.dll")]
        static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr hMonitor, byte code, IntPtr type, out uint current, out uint maximum);
        [DllImport("dxva2.dll")]
        static extern bool SetVCPFeature(IntPtr hMonitor, byte code, uint value);
        [DllImport("dxva2.dll")]
        static extern bool GetCapabilitiesStringLength(IntPtr hMonitor, out uint length);
        [DllImport("dxva2.dll", CharSet = CharSet.Ansi)]
        static extern bool CapabilitiesRequestAndCapabilitiesReply(IntPtr hMonitor, StringBuilder caps, uint length);

        const byte VCP_INPUT = 0x60;

        // Wenn der Monitor keine Eingangsliste liefert
        public static readonly int[] FallbackInputs = { 0x0F, 0x10, 0x11, 0x12, 0x1A };

        // DDC/CI ist ein langsamer I2C-Bus: nie zwei Abfragen gleichzeitig (Tray-Menue + Hintergrund-Timer).
        // All() nimmt die Sperre, Free() gibt sie wieder frei; beide stehen immer als Paar in try/finally.
        static readonly object Bus = new object();

        static List<PHYSICAL_MONITOR> All()
        {
            System.Threading.Monitor.Enter(Bus);
            List<PHYSICAL_MONITOR> list = new List<PHYSICAL_MONITOR>();
            MonitorEnumProc cb = delegate(IntPtr h, IntPtr dc, ref RECT r, IntPtr d)
            {
                uint n;
                if (GetNumberOfPhysicalMonitorsFromHMONITOR(h, out n) && n > 0)
                {
                    PHYSICAL_MONITOR[] arr = new PHYSICAL_MONITOR[n];
                    if (GetPhysicalMonitorsFromHMONITOR(h, n, arr)) list.AddRange(arr);
                }
                return true;
            };
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
            GC.KeepAlive(cb);
            return list;
        }

        // Achtung: 0 ist ein gueltiges Handle (der erste physische Monitor bekommt oft 0),
        // daher Erfolg separat zurückgeben statt IntPtr.Zero als "nicht gefunden".
        static bool Pick(List<PHYSICAL_MONITOR> list, string match, out IntPtr handle)
        {
            handle = IntPtr.Zero;
            if (string.IsNullOrEmpty(match)) return false;
            foreach (PHYSICAL_MONITOR m in list)   // exakter Name zuerst
                if (string.Equals(m.szPhysicalMonitorDescription, match, StringComparison.OrdinalIgnoreCase))
                { handle = m.hPhysicalMonitor; return true; }
            foreach (PHYSICAL_MONITOR m in list)   // dann Teilstring
                if (m.szPhysicalMonitorDescription != null &&
                    m.szPhysicalMonitorDescription.IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0)
                { handle = m.hPhysicalMonitor; return true; }
            return false;
        }

        static void Free(List<PHYSICAL_MONITOR> list)
        {
            try
            {
                PHYSICAL_MONITOR[] arr = list.ToArray();
                if (arr.Length > 0) DestroyPhysicalMonitors((uint)arr.Length, arr);
            }
            finally { System.Threading.Monitor.Exit(Bus); }
        }

        public static List<string> Names()
        {
            List<PHYSICAL_MONITOR> l = All();
            try
            {
                List<string> r = new List<string>();
                foreach (PHYSICAL_MONITOR m in l) r.Add(m.szPhysicalMonitorDescription ?? "");
                return r;
            }
            finally { Free(l); }
        }

        public static bool Found(string match)
        {
            List<PHYSICAL_MONITOR> l = All();
            IntPtr h;
            try { return Pick(l, match, out h); }
            finally { Free(l); }
        }

        public static bool SetInput(string match, int value)
        {
            List<PHYSICAL_MONITOR> l = All();
            try
            {
                IntPtr h;
                if (!Pick(l, match, out h)) return false;
                for (int i = 0; i < 3; i++)
                {
                    if (SetVCPFeature(h, VCP_INPUT, (uint)value)) return true;
                    Thread.Sleep(80);
                }
                return false;
            }
            finally { Free(l); }
        }

        /// <returns>aktueller Eingang (0x00-0xFF) oder -1, wenn nicht lesbar</returns>
        public static int GetInput(string match)
        {
            List<PHYSICAL_MONITOR> l = All();
            try
            {
                IntPtr h;
                if (!Pick(l, match, out h)) return -1;
                for (int i = 0; i < 2; i++)
                {
                    uint cur, max;
                    if (GetVCPFeatureAndVCPFeatureReply(h, VCP_INPUT, IntPtr.Zero, out cur, out max))
                        return (int)(cur & 0xFF);
                    Thread.Sleep(50);
                }
                return -1;
            }
            finally { Free(l); }
        }

        /// <summary>Eingänge laut Capabilities-String des Monitors (dauert ca. 1 s), leer wenn unbekannt.</summary>
        public static List<int> GetInputs(string match)
        {
            List<int> r = new List<int>();
            string caps = null;
            List<PHYSICAL_MONITOR> l = All();
            try
            {
                IntPtr h;
                if (!Pick(l, match, out h)) return r;
                for (int i = 0; i < 3 && caps == null; i++)
                {
                    uint len;
                    if (GetCapabilitiesStringLength(h, out len) && len > 0)
                    {
                        StringBuilder sb = new StringBuilder((int)len + 1);
                        if (CapabilitiesRequestAndCapabilitiesReply(h, sb, len)) caps = sb.ToString();
                    }
                    if (caps == null) Thread.Sleep(100);
                }
            }
            finally { Free(l); }
            if (caps == null) return r;

            int vcp = caps.IndexOf("vcp(", StringComparison.OrdinalIgnoreCase);
            Match mm = Regex.Match(vcp >= 0 ? caps.Substring(vcp + 4) : caps, @"(?<![0-9A-Fa-f])60\s*\(([^)]*)\)");
            if (!mm.Success) return r;
            foreach (string t in mm.Groups[1].Value.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int v;
                if (int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v) && !r.Contains(v & 0xFF))
                    r.Add(v & 0xFF);
            }
            return r;
        }

        // ---- Pixelreinigung (ASUS ROG OLED)
        // Herstellercode 0xFD ist ein Bitfeld mit Schaltern ("ToggleSettings2"). Bit 0x10 setzen startet
        // die Pixelreinigung; der Monitor setzt das Bit danach selbst zurueck.
        // Bestaetigt mit PG34WCDN; fuer PG49WCD siehe https://codeberg.org/beeb/asus-pixel-cleaner
        const byte VCP_ASUS_TOGGLES = 0xFD;
        const uint BIT_PIXEL_CLEANING = 0x10;
        public static readonly TimeSpan PixelCleaningDuration = TimeSpan.FromMinutes(7);   // Monitor braucht ca. 6 min

        public enum CleanResult { Started, NotFound, NotSupported, Failed }

        /// <summary>Nur ASUS-ROG-Monitore, deren 0xFD-Maske das Reinigungsbit enthaelt (andere Hersteller belegen 0xFD anders).</summary>
        public static bool SupportsPixelCleaning(string match)
        {
            List<PHYSICAL_MONITOR> l = All();
            try
            {
                IntPtr h;
                if (!Pick(l, match, out h)) return false;
                return IsAsusRog(l, h) && ReadToggles(h) >= 0 && (lastTogglesMax & BIT_PIXEL_CLEANING) != 0;
            }
            finally { Free(l); }
        }

        public static CleanResult StartPixelCleaning(string match)
        {
            List<PHYSICAL_MONITOR> l = All();
            try
            {
                IntPtr h;
                if (!Pick(l, match, out h)) return CleanResult.NotFound;
                if (!IsAsusRog(l, h)) return CleanResult.NotSupported;
                long cur = ReadToggles(h);
                if (cur < 0) return CleanResult.Failed;
                if ((lastTogglesMax & BIT_PIXEL_CLEANING) == 0) return CleanResult.NotSupported;
                uint value = ((uint)cur | BIT_PIXEL_CLEANING) & 0xFFFF;   // uebrige Schalter unveraendert lassen
                for (int i = 0; i < 3; i++)
                {
                    if (SetVCPFeature(h, VCP_ASUS_TOGGLES, value)) return CleanResult.Started;
                    Thread.Sleep(80);
                }
                return CleanResult.Failed;
            }
            finally { Free(l); }
        }

        static uint lastTogglesMax;

        static long ReadToggles(IntPtr h)
        {
            for (int i = 0; i < 3; i++)
            {
                uint cur, max;
                if (GetVCPFeatureAndVCPFeatureReply(h, VCP_ASUS_TOGGLES, IntPtr.Zero, out cur, out max))
                {
                    lastTogglesMax = max;
                    return cur;
                }
                Thread.Sleep(60);
            }
            return -1;
        }

        static bool IsAsusRog(List<PHYSICAL_MONITOR> l, IntPtr h)
        {
            foreach (PHYSICAL_MONITOR m in l)
                if (m.hPhysicalMonitor == h)
                    return m.szPhysicalMonitorDescription != null &&
                           m.szPhysicalMonitorDescription.IndexOf("ROG", StringComparison.OrdinalIgnoreCase) >= 0;
            return false;
        }

        public static string StandardName(int v)
        {
            switch (v)
            {
                case 0x01: return "VGA 1";
                case 0x02: return "VGA 2";
                case 0x03: return "DVI 1";
                case 0x04: return "DVI 2";
                case 0x05: return "Composite 1";
                case 0x06: return "Composite 2";
                case 0x07: return "S-Video 1";
                case 0x08: return "S-Video 2";
                case 0x0C: return "Component 1";
                case 0x0D: return "Component 2";
                case 0x0F: return "DisplayPort 1";
                case 0x10: return "DisplayPort 2";
                case 0x11: return "HDMI 1";
                case 0x12: return "HDMI 2";
                case 0x1A: return "USB-C";
                case 0x1B: return "USB-C 2";
                default:   return "Eingang 0x" + v.ToString("X2");
            }
        }
    }

    // ------------------------------------------------------------------ Konfiguration
    class Config
    {
        public string Monitor = "";
        public int Target = -1;
        public string Hotkey = "Ctrl+Alt+F12";
        public Dictionary<int, string> Names = new Dictionary<int, string>();
        public double CleanIntervalHours = 0;   // 0 = keine automatische Erinnerung
        public int CleanUsageMinutes = 0;       // Nutzungszeit seit der letzten Reinigung

        static string path;

        public static string FilePath
        {
            get { if (path == null) path = ResolvePath(); return path; }
        }

        static string ResolvePath()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            string local = Path.Combine(dir, "MonitorSwitch.ini");
            if (File.Exists(local)) return local;
            try   // Ordner der .exe beschreibbar?
            {
                string probe = Path.Combine(dir, "MonitorSwitch.write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return local;
            }
            catch (Exception)
            {
                string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MonitorSwitch");
                Directory.CreateDirectory(appData);
                return Path.Combine(appData, "MonitorSwitch.ini");
            }
        }

        public static bool Exists { get { return File.Exists(FilePath); } }

        public static Config Load()
        {
            Config c = new Config();
            if (!File.Exists(FilePath)) return c;

            foreach (string raw in File.ReadAllLines(FilePath))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#") || line.StartsWith("[")) continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string val = line.Substring(eq + 1).Trim();
                try
                {
                    if (key == "monitor") c.Monitor = val;
                    else if (key == "target") c.Target = ParseInt(val);
                    else if (key == "hotkey") c.Hotkey = val;
                    else if (key == "cleaninterval") c.CleanIntervalHours = Math.Max(0, double.Parse(val.Replace(',', '.'), CultureInfo.InvariantCulture));
                    else if (key == "cleanusageminutes") c.CleanUsageMinutes = Math.Max(0, ParseInt(val));
                    else if (key.StartsWith("name_") && val.Length > 0) c.Names[ParseInt(key.Substring(5))] = val;
                }
                catch (FormatException) { /* ungueltigen Wert ignorieren */ }
                catch (OverflowException) { }
            }
            return c;
        }

        public static int ParseInt(string s)
        {
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return Convert.ToInt32(s.Substring(2), 16);
            return int.Parse(s, CultureInfo.InvariantCulture);
        }

        public string InputName(int v)
        {
            string n;
            if (Names.TryGetValue(v, out n) && n.Length > 0) return n;
            return Ddc.StandardName(v);
        }

        public bool Save()
        {
            List<string> lines = new List<string>();
            lines.Add("; MonitorSwitch - Konfiguration (wird über das Tray-Menü gepflegt)");
            lines.Add("; Nach Änderungen von Hand im Tray-Menü 'Konfiguration neu laden' wählen.");
            lines.Add("[MonitorSwitch]");
            lines.Add("; Monitorname, wie Windows ihn meldet (Teilstring reicht)");
            lines.Add("Monitor=" + Monitor);
            lines.Add("; Eingang (VCP 0x60), auf den Klick, Hotkey und Desktop-Verknüpfung wechseln");
            lines.Add("; Üblich: 0x0F=DisplayPort 1, 0x10=DisplayPort 2, 0x11=HDMI 1, 0x12=HDMI 2, 0x1A=USB-C");
            lines.Add("Target=" + (Target >= 0 ? "0x" + Target.ToString("X2") : ""));
            lines.Add("; Modifier: Ctrl/Strg, Alt, Shift, Win  -  Taste: F1-F24, Pause, Scroll, A-Z, D0-D9 ...");
            lines.Add("Hotkey=" + Hotkey);
            lines.Add("; Erinnerung an die Pixelreinigung nach so vielen Stunden Nutzung (0 = aus)");
            lines.Add("CleanInterval=" + CleanIntervalHours.ToString("0.##", CultureInfo.InvariantCulture));
            lines.Add("; Bisher gezählte Nutzung seit der letzten Reinigung (wird automatisch gepflegt)");
            lines.Add("CleanUsageMinutes=" + CleanUsageMinutes.ToString(CultureInfo.InvariantCulture));
            lines.Add("; Eigene Namen für Eingänge, z. B. Name_0x11=Gaming-PC");
            List<int> keys = new List<int>(Names.Keys);
            keys.Sort();
            foreach (int k in keys) lines.Add("Name_0x" + k.ToString("X2") + "=" + Names[k]);
            try { File.WriteAllLines(FilePath, lines.ToArray()); return true; }
            catch (Exception) { return false; }
        }
    }

    // ------------------------------------------------------------------ Globaler Hotkey
    class HotkeyWindow : NativeWindow, IDisposable
    {
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        const int WM_HOTKEY = 0x0312;
        const int ID = 1;
        const uint MOD_NOREPEAT = 0x4000;
        bool registered;

        public event EventHandler Pressed;

        public HotkeyWindow() { CreateHandle(new CreateParams()); }

        public bool Register(uint mods, uint vk)
        {
            Unregister();
            registered = RegisterHotKey(Handle, ID, mods | MOD_NOREPEAT, vk);
            return registered;
        }

        public void Unregister()
        {
            if (registered) { UnregisterHotKey(Handle, ID); registered = false; }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && Pressed != null) Pressed(this, EventArgs.Empty);
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            Unregister();
            DestroyHandle();
        }

        public static bool Parse(string text, out uint mods, out uint vk)
        {
            mods = 0; vk = 0;
            foreach (string part in text.Split('+'))
            {
                string t = part.Trim();
                string l = t.ToLowerInvariant();
                if (l.Length == 0) continue;
                if (l == "ctrl" || l == "strg" || l == "control") mods |= 0x0002;
                else if (l == "alt") mods |= 0x0001;
                else if (l == "shift" || l == "umschalt") mods |= 0x0004;
                else if (l == "win") mods |= 0x0008;
                else
                {
                    Keys k;
                    if (!Enum.TryParse(t, true, out k)) return false;
                    vk = (uint)(k & Keys.KeyCode);
                }
            }
            return vk != 0;
        }
    }

    // ------------------------------------------------------------------ Gemeinsame Helfer
    static class Util
    {
        [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);

        [StructLayout(LayoutKind.Sequential)]
        struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
        [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
        [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);

        /// <summary>Zeit seit der letzten Maus-/Tastatureingabe.</summary>
        public static TimeSpan IdleTime()
        {
            LASTINPUTINFO li = new LASTINPUTINFO();
            li.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
            if (!GetLastInputInfo(ref li)) return TimeSpan.Zero;
            return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - li.dwTime));
        }

        /// <summary>false bei Vollbild-Spiel, Praesentation oder "Nicht stoeren" - dann lieber spaeter fragen.</summary>
        public static bool UserAcceptsPopups()
        {
            int state;
            if (SHQueryUserNotificationState(out state) != 0) return true;
            const int QUNS_ACCEPTS_NOTIFICATIONS = 5, QUNS_APP = 7;
            return state == QUNS_ACCEPTS_NOTIFICATIONS || state == QUNS_APP;
        }

        /// <summary>Haelt Bildschirm und PC wach (fuer die Pixelreinigung braucht der Monitor ein Signal). Gilt fuer den aufrufenden Thread.</summary>
        public static void KeepAwake(bool on)
        {
            const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2;
            SetThreadExecutionState(on ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED : ES_CONTINUOUS);
        }

        /// <summary>Waehlt einen Monitor mit mehreren Eingängen und als Ziel den ersten Eingang, der gerade nicht aktiv ist.</summary>
        public static void AutoDetect(Config c)
        {
            if (c.Monitor.Length == 0 || !Ddc.Found(c.Monitor))
            {
                c.Monitor = "";
                foreach (string n in Ddc.Names())
                {
                    if (n.Length == 0 || Ddc.GetInput(n) < 0) continue;
                    if (c.Monitor.Length == 0) c.Monitor = n;           // erster mit DDC/CI
                    if (Ddc.GetInputs(n).Count >= 2) { c.Monitor = n; break; }
                }
            }
            if (c.Target < 0 && c.Monitor.Length > 0)
            {
                int cur = Ddc.GetInput(c.Monitor);
                List<int> inputs = Ddc.GetInputs(c.Monitor);
                if (inputs.Count == 0) inputs.AddRange(Ddc.FallbackInputs);
                foreach (int v in inputs)
                    if (v != cur) { c.Target = v; break; }
            }
        }

        public static Bitmap DrawIcon(int size)
        {
            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                g.ScaleTransform(size / 32f, size / 32f);
                using (SolidBrush frame = new SolidBrush(Color.FromArgb(235, 235, 235)))
                using (SolidBrush screen = new SolidBrush(Color.FromArgb(0, 150, 255)))
                using (Pen arrow = new Pen(Color.White, 2.5f))
                {
                    g.FillRectangle(frame, 2, 4, 28, 19);
                    g.FillRectangle(screen, 4, 6, 24, 15);
                    g.FillRectangle(frame, 14, 23, 4, 4);
                    g.FillRectangle(frame, 9, 27, 14, 3);
                    // Wechselpfeile
                    g.DrawLine(arrow, 9, 11, 22, 11);
                    g.DrawLine(arrow, 19, 8, 22, 11);
                    g.DrawLine(arrow, 10, 16, 23, 16);
                    g.DrawLine(arrow, 10, 16, 13, 19);
                }
            }
            return bmp;
        }

        /// <summary>Schreibt das Symbol als .ico (PNG-Eintraege, ab Windows Vista).</summary>
        public static void WriteIco(string file)
        {
            int[] sizes = { 16, 24, 32, 48, 256 };
            List<byte[]> pngs = new List<byte[]>();
            foreach (int s in sizes)
                using (Bitmap b = DrawIcon(s))
                using (MemoryStream ms = new MemoryStream())
                {
                    b.Save(ms, ImageFormat.Png);
                    pngs.Add(ms.ToArray());
                }

            using (BinaryWriter w = new BinaryWriter(File.Create(file)))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0); w.Write((byte)0);
                    w.Write((short)1); w.Write((short)32);
                    w.Write(pngs[i].Length); w.Write(offset);
                    offset += pngs[i].Length;
                }
                foreach (byte[] p in pngs) w.Write(p);
            }
        }
    }

    // ------------------------------------------------------------------ Erinnerung an die Pixelreinigung
    /// <summary>
    /// Kleines Fenster unten rechts. Es liegt im Vordergrund, nimmt aber keinen Tastaturfokus,
    /// damit ein Tastendruck beim Tippen nichts ausloest - bestaetigt wird mit der Maus.
    /// Ergebnis: 0 = jetzt reinigen, 5/15/30 = so viele Minuten spaeter erinnern, -1 = abbrechen.
    /// </summary>
    class CleanPrompt : Form
    {
        public event Action<int> Answered;
        bool answered;

        public CleanPrompt(string monitor, int usageMinutes)
        {
            Text = "MonitorSwitch - Pixelreinigung";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            ControlBox = false;            // kein X: nur die eindeutigen Knoepfe
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            AutoScaleMode = AutoScaleMode.Font;
            Font = SystemFonts.MessageBoxFont;
            Padding = new Padding(12);

            TableLayoutPanel root = new TableLayoutPanel();
            root.AutoSize = true;
            root.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            root.ColumnCount = 1;
            root.Dock = DockStyle.Fill;

            Label title = new Label();
            title.AutoSize = true;
            title.Font = new Font(Font.FontFamily, Font.Size * 1.25f, FontStyle.Bold);
            title.Text = "Pixelreinigung fällig";
            title.Margin = new Padding(0, 0, 0, 6);

            Label text = new Label();
            text.AutoSize = true;
            text.MaximumSize = new Size(380, 0);
            text.Text = monitor + " war etwa " + FormatDuration(usageMinutes) + " in Benutzung.\n" +
                        "Die Reinigung dauert ca. 6 Minuten, der Bildschirm ist in der Zeit schwarz.";
            text.Margin = new Padding(0, 0, 0, 10);

            Button now = MakeButton("Jetzt reinigen", 0);
            now.Font = new Font(Font, FontStyle.Bold);
            now.Margin = new Padding(0, 0, 0, 10);

            Label later = new Label();
            later.AutoSize = true;
            later.Text = "Später erinnern:";
            later.Margin = new Padding(0, 0, 0, 4);

            FlowLayoutPanel row = new FlowLayoutPanel();
            row.AutoSize = true;
            row.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            row.WrapContents = false;
            row.Margin = new Padding(0);
            row.Controls.Add(MakeButton("5 Min", 5));
            row.Controls.Add(MakeButton("15 Min", 15));
            row.Controls.Add(MakeButton("30 Min", 30));
            Button cancel = MakeButton("Abbrechen", -1);
            cancel.Margin = new Padding(12, 0, 0, 0);
            row.Controls.Add(cancel);

            root.Controls.Add(title);
            root.Controls.Add(text);
            root.Controls.Add(now);
            root.Controls.Add(later);
            root.Controls.Add(row);
            Controls.Add(root);
        }

        Button MakeButton(string caption, int result)
        {
            Button b = new Button();
            b.Text = caption;
            b.AutoSize = true;
            b.MinimumSize = new Size(72, 28);
            b.Margin = new Padding(0, 0, 6, 0);
            b.Click += delegate { Answer(result); };
            return b;
        }

        void Answer(int result)
        {
            if (answered) return;
            answered = true;
            if (Answered != null) Answered(result);
            Close();
        }

        public static string FormatDuration(int minutes)
        {
            if (minutes < 60) return minutes + " Minuten";
            int h = minutes / 60, m = minutes % 60;
            return m == 0 ? h + " Stunden" : h + " Std. " + m + " Min.";
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_EX_TOPMOST = 0x8, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80;
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;   // unten rechts, wie eine Benachrichtigung
            Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);
        }
    }

    // ------------------------------------------------------------------ Tray-Anwendung
    class TrayApp : ApplicationContext
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "MonitorSwitch";
        const string ShortcutName = "Monitor wechseln.lnk";

        readonly NotifyIcon tray;
        readonly HotkeyWindow hotkey;
        static readonly double[] IntervalChoices = { 4, 6, 8, 12, 24 };

        readonly ToolStripMenuItem miMain, miSwitch, miClean, miAuto, miTarget, miRename, miMonitor, miAutostart;
        readonly ToolStripSeparator sepClean;
        readonly Dictionary<string, List<int>> inputCache = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, bool> cleanSupport = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        readonly System.Windows.Forms.Timer cleanTimer = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer usageTimer = new System.Windows.Forms.Timer();
        readonly SynchronizationContext ui;
        Config cfg;
        DateTime lastSwitch = DateTime.MinValue;
        DateTime cleaningUntil = DateTime.MinValue;
        DateTime snoozeUntil = DateTime.MinValue;
        CleanPrompt prompt;
        bool usageBusy;

        bool Cleaning { get { return DateTime.Now < cleaningUntil; } }

        public TrayApp()
        {
            bool firstRun = !Config.Exists;
            cfg = Config.Load();

            hotkey = new HotkeyWindow();
            hotkey.Pressed += delegate { SwitchToTarget(); };

            miMain      = new ToolStripMenuItem("Wechseln", null, delegate { SwitchToTarget(); });
            miMain.Font = new Font(miMain.Font, FontStyle.Bold);
            miSwitch    = new ToolStripMenuItem("Jetzt wechseln zu");
            miClean     = new ToolStripMenuItem("Pixelreinigung starten...", null, delegate { StartCleaning(true); });
            miAuto      = new ToolStripMenuItem("Automatisch erinnern");
            sepClean    = new ToolStripSeparator();
            miTarget    = new ToolStripMenuItem("Ziel für Klick und Hotkey");
            miRename    = new ToolStripMenuItem("Eingang umbenennen");
            miMonitor   = new ToolStripMenuItem("Monitor");
            miAutostart = new ToolStripMenuItem("Mit Windows starten", null, delegate { SetAutostart(!IsAutostart()); });
            ToolStripMenuItem miShortcut = new ToolStripMenuItem("Desktop-Verknüpfung erstellen", null, delegate { CreateShortcut(); });
            ToolStripMenuItem miEdit     = new ToolStripMenuItem("Konfiguration bearbeiten", null, delegate { EditConfig(); });
            ToolStripMenuItem miReload   = new ToolStripMenuItem("Konfiguration neu laden", null, delegate { Reload(); });
            ToolStripMenuItem miExit     = new ToolStripMenuItem("Beenden", null, delegate { ExitThread(); });

            // Platzhalter, damit die Untermenue-Pfeile sichtbar sind; Inhalt kommt beim Öffnen
            foreach (ToolStripMenuItem sub in new ToolStripMenuItem[] { miSwitch, miAuto, miTarget, miRename, miMonitor })
                sub.DropDownItems.Add("...");

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.AddRange(new ToolStripItem[] {
                miMain, miSwitch,
                sepClean, miClean, miAuto,
                new ToolStripSeparator(),
                miTarget, miRename, miMonitor,
                new ToolStripSeparator(),
                miAutostart, miShortcut, miEdit, miReload,
                new ToolStripSeparator(),
                miExit
            });
            menu.Opening += delegate { BuildMenu(); };

            tray = new NotifyIcon();
            using (Bitmap b = Util.DrawIcon(Math.Max(16, SystemInformation.SmallIconSize.Width)))
                tray.Icon = Icon.FromHandle(b.GetHicon());
            tray.ContextMenuStrip = menu;
            tray.MouseClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) SwitchToTarget(); };
            tray.Text = "MonitorSwitch";
            tray.Visible = true;

            cleanTimer.Tick += delegate { CleaningFinished(); };

            ui = new WindowsFormsSynchronizationContext();
            usageTimer.Interval = 60 * 1000;
            usageTimer.Tick += delegate { UsageTick(); };
            usageTimer.Start();
            SystemEvents.SessionEnding += OnSessionEnding;

            Startup(firstRun);
        }

        void Startup(bool firstRun)
        {
            string oldMonitor = cfg.Monitor;
            int oldTarget = cfg.Target;
            Util.AutoDetect(cfg);
            if (cfg.Monitor != oldMonitor || cfg.Target != oldTarget || firstRun) cfg.Save();

            ApplyHotkey();
            UpdateTexts();

            if (cfg.Monitor.Length == 0)
                Info("Kein Monitor mit DDC/CI gefunden. Ist DDC/CI im Monitor-Menü (OSD) aktiviert?", ToolTipIcon.Warning);
            else if (firstRun)
                Info("Klick aufs Icon oder " + cfg.Hotkey + " wechselt zu " + cfg.InputName(cfg.Target) +
                     ".\nDas Ziel kannst du per Rechtsklick ändern.", ToolTipIcon.Info);
        }

        void Reload()
        {
            cfg = Config.Load();
            inputCache.Clear();
            cleanSupport.Clear();
            Util.AutoDetect(cfg);
            ApplyHotkey();
            UpdateTexts();
            if (!Ddc.Found(cfg.Monitor))
                Info("Monitor '" + cfg.Monitor + "' nicht gefunden.", ToolTipIcon.Warning);
            else
                Info("Konfiguration geladen.", ToolTipIcon.Info);
        }

        void ApplyHotkey()
        {
            hotkey.Unregister();
            if (cfg.Hotkey.Length == 0) return;   // Hotkey= leer -> kein Hotkey
            uint mods, vk;
            if (!HotkeyWindow.Parse(cfg.Hotkey, out mods, out vk))
                Info("Hotkey '" + cfg.Hotkey + "' nicht erkannt. Bitte Konfiguration prüfen.", ToolTipIcon.Warning);
            else if (!hotkey.Register(mods, vk))
                Info("Hotkey " + cfg.Hotkey + " ist schon von einem anderen Programm belegt.", ToolTipIcon.Warning);
        }

        void UpdateTexts()
        {
            string target = cfg.Target >= 0 ? cfg.InputName(cfg.Target) : "(kein Ziel)";
            miMain.Text = "Wechseln zu " + target;
            miMain.ShortcutKeyDisplayString = cfg.Hotkey;
            string tip = "MonitorSwitch - Klick: zu " + target;
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }

        List<int> Inputs()
        {
            List<int> l;
            if (!inputCache.TryGetValue(cfg.Monitor, out l))
            {
                l = Ddc.GetInputs(cfg.Monitor);
                if (l.Count > 0) inputCache[cfg.Monitor] = l;
                else l = new List<int>(Ddc.FallbackInputs);
            }
            List<int> r = new List<int>(l);
            if (cfg.Target >= 0 && !r.Contains(cfg.Target)) r.Add(cfg.Target);
            return r;
        }

        void BuildMenu()
        {
            int cur = Ddc.GetInput(cfg.Monitor);
            List<int> inputs = Inputs();

            miSwitch.DropDownItems.Clear();
            miTarget.DropDownItems.Clear();
            miRename.DropDownItems.Clear();
            foreach (int v in inputs)
            {
                int value = v;   // für die Closures
                string name = cfg.InputName(v);

                ToolStripMenuItem sw = new ToolStripMenuItem(name + (v == cur ? "   (aktuell)" : ""), null, delegate { SwitchTo(value); });
                sw.Checked = v == cur;
                miSwitch.DropDownItems.Add(sw);

                ToolStripMenuItem tg = new ToolStripMenuItem(name, null, delegate { SetTarget(value); });
                tg.Checked = v == cfg.Target;
                miTarget.DropDownItems.Add(tg);

                miRename.DropDownItems.Add(new ToolStripMenuItem(name + "  (0x" + v.ToString("X2") + ")...", null, delegate { Rename(value); }));
            }

            miMonitor.DropDownItems.Clear();
            foreach (string n in Ddc.Names())
            {
                string monitor = n;
                ToolStripMenuItem mi = new ToolStripMenuItem(n.Length > 0 ? n : "(ohne Namen)", null, delegate { SetMonitor(monitor); });
                mi.Checked = string.Equals(n, cfg.Monitor, StringComparison.OrdinalIgnoreCase);
                miMonitor.DropDownItems.Add(mi);
            }

            bool supported;
            if (!cleanSupport.TryGetValue(cfg.Monitor, out supported))
            {
                supported = Ddc.SupportsPixelCleaning(cfg.Monitor);
                if (cur >= 0) cleanSupport[cfg.Monitor] = supported;   // nur merken, wenn der Monitor geantwortet hat
            }
            miClean.Visible = sepClean.Visible = miAuto.Visible = supported || Cleaning;
            miClean.Enabled = !Cleaning;
            miClean.Text = Cleaning ? "Pixelreinigung läuft (bis " + cleaningUntil.ToString("HH:mm") + ")" : "Pixelreinigung starten...";
            BuildAutoMenu();

            miAutostart.Checked = IsAutostart();
            UpdateTexts();
        }

        void BuildAutoMenu()
        {
            miAuto.DropDownItems.Clear();
            ToolStripMenuItem off = new ToolStripMenuItem("Aus", null, delegate { SetInterval(0); });
            off.Checked = cfg.CleanIntervalHours <= 0;
            miAuto.DropDownItems.Add(off);

            bool known = false;
            foreach (double h in IntervalChoices)
            {
                double hours = h;
                ToolStripMenuItem mi = new ToolStripMenuItem("Alle " + h + " Stunden Nutzung", null, delegate { SetInterval(hours); });
                mi.Checked = Math.Abs(cfg.CleanIntervalHours - h) < 0.001;
                known |= mi.Checked;
                miAuto.DropDownItems.Add(mi);
            }
            if (!known && cfg.CleanIntervalHours > 0)   // eigener Wert aus der .ini
            {
                ToolStripMenuItem custom = new ToolStripMenuItem("Alle " + cfg.CleanIntervalHours.ToString("0.##") + " Stunden Nutzung (eigener Wert)");
                custom.Checked = true;
                miAuto.DropDownItems.Add(custom);
            }

            if (cfg.CleanIntervalHours > 0)
            {
                miAuto.DropDownItems.Add(new ToolStripSeparator());
                miAuto.DropDownItems.Add(Disabled("Genutzt seit letzter Reinigung: " + CleanPrompt.FormatDuration(cfg.CleanUsageMinutes)));
                if (DateTime.Now < snoozeUntil)
                    miAuto.DropDownItems.Add(Disabled("Nächste Erinnerung frühestens " + snoozeUntil.ToString("HH:mm")));
                miAuto.DropDownItems.Add(new ToolStripMenuItem("Zähler zurücksetzen", null, delegate { ResetUsage(); }));
            }
            miAuto.Text = cfg.CleanIntervalHours > 0
                ? "Automatisch erinnern (alle " + cfg.CleanIntervalHours.ToString("0.##") + " h)"
                : "Automatisch erinnern";
        }

        static ToolStripMenuItem Disabled(string text)
        {
            ToolStripMenuItem mi = new ToolStripMenuItem(text);
            mi.Enabled = false;
            return mi;
        }

        void SetInterval(double hours)
        {
            cfg.CleanIntervalHours = hours;
            SaveConfig();
            if (hours > 0)
                Info("Erinnerung an die Pixelreinigung nach " + hours.ToString("0.##") + " Stunden Nutzung.\n" +
                     "Bisher gezählt: " + CleanPrompt.FormatDuration(cfg.CleanUsageMinutes) + ".", ToolTipIcon.Info);
        }

        void ResetUsage()
        {
            cfg.CleanUsageMinutes = 0;
            snoozeUntil = DateTime.MinValue;
            SaveConfig();
        }

        // Jede Minute: Nutzungszeit zaehlen, wenn jemand am PC ist und der Monitor diesen PC zeigt.
        void UsageTick()
        {
            if (usageBusy || cfg.CleanIntervalHours <= 0 || Cleaning || cfg.Monitor.Length == 0) return;
            if (Util.IdleTime() > TimeSpan.FromMinutes(5)) return;   // niemand da

            usageBusy = true;
            string monitor = cfg.Monitor;
            bool supported;
            bool known = cleanSupport.TryGetValue(monitor, out supported);
            ThreadPool.QueueUserWorkItem(delegate
            {
                int cur = -1;
                bool sup = supported;
                try
                {
                    cur = Ddc.GetInput(monitor);   // DDC/CI im Hintergrund, damit das Tray nicht haengt
                    if (!known && cur >= 0) sup = Ddc.SupportsPixelCleaning(monitor);
                }
                catch (Exception) { cur = -1; }
                ui.Post(delegate { UsageCounted(monitor, cur, sup, known); }, null);
            });
        }

        void UsageCounted(string monitor, int cur, bool supported, bool known)
        {
            usageBusy = false;
            if (!string.Equals(monitor, cfg.Monitor, StringComparison.OrdinalIgnoreCase)) return;
            if (!known && cur >= 0) cleanSupport[monitor] = supported;
            if (!supported) return;
            // Monitor aus/nicht erreichbar oder zeigt gerade den anderen PC -> zaehlt dort, nicht hier
            if (cur < 0 || cur == cfg.Target) return;

            cfg.CleanUsageMinutes++;
            if (cfg.CleanUsageMinutes % 10 == 0) cfg.Save();
            MaybePrompt();
        }

        void MaybePrompt()
        {
            if (prompt != null || Cleaning || cfg.CleanIntervalHours <= 0) return;
            if (cfg.CleanUsageMinutes < cfg.CleanIntervalHours * 60) return;
            if (DateTime.Now < snoozeUntil) return;
            if (!Util.UserAcceptsPopups()) return;   // Vollbild/Praesentation: naechste Minute erneut pruefen

            prompt = new CleanPrompt(cfg.Monitor, cfg.CleanUsageMinutes);
            prompt.Answered += OnPromptAnswered;
            prompt.FormClosed += delegate { prompt = null; };
            prompt.Show();
        }

        void OnPromptAnswered(int result)
        {
            if (result == 0)
            {
                if (!StartCleaning(false)) snoozeUntil = DateTime.Now.AddMinutes(15);   // nicht jede Minute neu fragen
            }
            else if (result > 0)
            {
                snoozeUntil = DateTime.Now.AddMinutes(result);
            }
            else
            {
                ResetUsage();
                Info("Pixelreinigung übersprungen. Nächste Erinnerung nach " +
                     cfg.CleanIntervalHours.ToString("0.##") + " Stunden Nutzung.", ToolTipIcon.Info);
            }
        }

        void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            cfg.Save();   // gezaehlte Nutzung nicht verlieren
        }

        bool StartCleaning(bool ask)
        {
            if (ask && MessageBox.Show(
                    "Pixelreinigung auf " + cfg.Monitor + " starten?\n\n" +
                    "Der Bildschirm ist dafür etwa 6 Minuten schwarz. In der Zeit den Monitor nicht ausschalten " +
                    "und nicht auf den anderen PC wechseln, sonst bricht die Reinigung ab.\n\n" +
                    "MonitorSwitch hält diesen PC so lange wach.",
                    "MonitorSwitch - Pixelreinigung", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return false;

            switch (Ddc.StartPixelCleaning(cfg.Monitor))
            {
                case Ddc.CleanResult.Started:
                    cleaningUntil = DateTime.Now + Ddc.PixelCleaningDuration;
                    Util.KeepAwake(true);
                    cleanTimer.Interval = (int)Ddc.PixelCleaningDuration.TotalMilliseconds;
                    cleanTimer.Start();
                    ResetUsage();   // Zaehler fuer die automatische Erinnerung beginnt neu
                    Info("Pixelreinigung gestartet, fertig gegen " + cleaningUntil.ToString("HH:mm") + ".", ToolTipIcon.Info);
                    return true;
                case Ddc.CleanResult.NotSupported:
                    Info("Dieser Monitor unterstützt das Starten der Pixelreinigung per DDC/CI nicht.", ToolTipIcon.Warning);
                    return false;
                case Ddc.CleanResult.NotFound:
                    Info("Monitor '" + cfg.Monitor + "' nicht gefunden.", ToolTipIcon.Warning);
                    return false;
                default:
                    Info("Pixelreinigung konnte nicht gestartet werden. Ist DDC/CI im Monitor-Menü aktiviert?", ToolTipIcon.Error);
                    return false;
            }
        }

        void CleaningFinished()
        {
            cleanTimer.Stop();
            cleaningUntil = DateTime.MinValue;
            Util.KeepAwake(false);
            Info("Pixelreinigung sollte abgeschlossen sein.", ToolTipIcon.Info);
        }

        void SwitchToTarget()
        {
            if ((DateTime.Now - lastSwitch).TotalMilliseconds < 1500) return;   // Doppelklick abfangen
            if (cfg.Target < 0) { Info("Noch kein Ziel gewählt (Rechtsklick > Ziel für Klick und Hotkey).", ToolTipIcon.Warning); return; }
            if (Ddc.GetInput(cfg.Monitor) == cfg.Target) { Info("Der Monitor zeigt schon " + cfg.InputName(cfg.Target) + ".", ToolTipIcon.Info); return; }
            SwitchTo(cfg.Target);
        }

        void SwitchTo(int value)
        {
            if (Cleaning && MessageBox.Show(
                    "Die Pixelreinigung läuft noch bis etwa " + cleaningUntil.ToString("HH:mm") + ".\n" +
                    "Ein Wechsel des Eingangs bricht sie ab. Trotzdem wechseln?",
                    "MonitorSwitch", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            lastSwitch = DateTime.Now;
            if (!Ddc.SetInput(cfg.Monitor, value))
                Info("Wechsel zu " + cfg.InputName(value) + " fehlgeschlagen. Ist DDC/CI im Monitor-Menü aktiviert?", ToolTipIcon.Error);
        }

        void SetTarget(int value)
        {
            cfg.Target = value;
            SaveConfig();
            UpdateTexts();
        }

        void SetMonitor(string name)
        {
            if (string.Equals(name, cfg.Monitor, StringComparison.OrdinalIgnoreCase)) return;
            cfg.Monitor = name;
            cfg.Target = -1;
            Util.AutoDetect(cfg);
            SaveConfig();
            UpdateTexts();
            Info("Monitor: " + name + "\nZiel: " + (cfg.Target >= 0 ? cfg.InputName(cfg.Target) : "-"), ToolTipIcon.Info);
        }

        void Rename(int value)
        {
            string current;
            cfg.Names.TryGetValue(value, out current);
            string answer = Microsoft.VisualBasic.Interaction.InputBox(
                "Name für " + Ddc.StandardName(value) + " (0x" + value.ToString("X2") + "), z. B. \"Gaming-PC\".\n\nLeer lassen für den Standardnamen.",
                "MonitorSwitch", current ?? Ddc.StandardName(value));
            // InputBox liefert "" bei Abbrechen und bei leerem Feld; beides heißt: Standardname
            answer = answer.Trim();
            if (answer.Length == 0 || answer == Ddc.StandardName(value)) cfg.Names.Remove(value);
            else cfg.Names[value] = answer;
            SaveConfig();
            UpdateTexts();
        }

        void SaveConfig()
        {
            if (!cfg.Save()) Info("Konfiguration konnte nicht gespeichert werden:\n" + Config.FilePath, ToolTipIcon.Error);
        }

        void CreateShortcut()
        {
            try
            {
                string exe = Application.ExecutablePath;
                string ico = Path.Combine(Path.GetDirectoryName(Config.FilePath), "MonitorSwitch.ico");
                Util.WriteIco(ico);
                string lnkPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutName);

                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(shellType);
                object lnk = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                Type t = lnk.GetType();
                t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { exe });
                t.InvokeMember("Arguments", BindingFlags.SetProperty, null, lnk, new object[] { "--target" });
                t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { Path.GetDirectoryName(exe) });
                t.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { ico + ",0" });
                t.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { "Monitor auf das eingestellte Ziel umschalten" });
                t.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
                Marshal.FinalReleaseComObject(lnk);
                Marshal.FinalReleaseComObject(shell);

                Info("Desktop-Verknüpfung \"" + Path.GetFileNameWithoutExtension(ShortcutName) + "\" erstellt.\nSie wechselt immer zum eingestellten Ziel.", ToolTipIcon.Info);
            }
            catch (Exception ex) { Info("Verknüpfung fehlgeschlagen: " + ex.Message, ToolTipIcon.Error); }
        }

        void Info(string text, ToolTipIcon icon)
        {
            tray.ShowBalloonTip(4000, "MonitorSwitch", text, icon);
        }

        void EditConfig()
        {
            try
            {
                if (!Config.Exists) cfg.Save();
                Process.Start("notepad.exe", "\"" + Config.FilePath + "\"");
            }
            catch (Exception ex) { Info(ex.Message, ToolTipIcon.Error); }
        }

        static bool IsAutostart()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(RunName) != null;
        }

        void SetAutostart(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue(RunName, false);
                }
                Info(on ? "Startet jetzt mit Windows." : "Autostart entfernt.", ToolTipIcon.Info);
            }
            catch (Exception ex) { Info(ex.Message, ToolTipIcon.Error); }
        }

        protected override void ExitThreadCore()
        {
            usageTimer.Stop();
            cleanTimer.Stop();
            SystemEvents.SessionEnding -= OnSessionEnding;
            if (prompt != null) prompt.Close();
            Util.KeepAwake(false);
            cfg.Save();   // gezaehlte Nutzung behalten
            hotkey.Dispose();
            tray.Visible = false;
            tray.Dispose();
            base.ExitThreadCore();
        }
    }

    // ------------------------------------------------------------------ Einstieg
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // Kommandozeilenmodus (ohne Tray), z. B. für Desktop-Verknüpfung, Stream Deck, Aufgabenplanung
            if (args.Length > 0)
            {
                try { return RunCommand(args); }
                catch (Exception ex) { Error(ex.Message); return 1; }
            }

            bool created;
            using (Mutex mutex = new Mutex(true, @"Local\MonitorSwitchTray", out created))
            {
                if (!created) return 0; // laeuft schon
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp());
            }
            return 0;
        }

        static int RunCommand(string[] args)
        {
            Config c = Config.Load();
            string a = args[0].ToLowerInvariant();

            if (a == "--list")
            {
                StringBuilder sb = new StringBuilder();
                foreach (string n in Ddc.Names())
                {
                    int cur = Ddc.GetInput(n);
                    sb.AppendLine(n + (cur >= 0 ? "" : "   (kein DDC/CI)"));
                    if (cur < 0) continue;
                    foreach (int v in Ddc.GetInputs(n))
                        sb.AppendLine("     0x" + v.ToString("X2") + "  " + c.InputName(v) + (v == cur ? "   <- aktuell" : ""));
                    sb.AppendLine();
                }
                sb.AppendLine("Konfiguration: " + Config.FilePath);
                MessageBox.Show(sb.ToString(), "MonitorSwitch - Monitore", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }

            if (a == "--clean")
            {
                if (c.Monitor.Length == 0) Util.AutoDetect(c);
                Ddc.CleanResult res = Ddc.StartPixelCleaning(c.Monitor);
                if (res == Ddc.CleanResult.Started)
                {
                    // Prozess laeuft unsichtbar weiter und haelt das Bildsignal, bis die Reinigung durch ist
                    Util.KeepAwake(true);
                    Thread.Sleep(Ddc.PixelCleaningDuration);
                    Util.KeepAwake(false);
                    return 0;
                }
                Error(res == Ddc.CleanResult.NotSupported
                    ? "Dieser Monitor unterstützt das Starten der Pixelreinigung per DDC/CI nicht.\nMonitor: " + c.Monitor
                    : "Pixelreinigung konnte nicht gestartet werden.\nMonitor: " + (c.Monitor.Length > 0 ? c.Monitor : "(keiner gefunden)"));
                return 1;
            }

            int value;
            if (a == "--target")
            {
                if (c.Monitor.Length == 0 || c.Target < 0)
                {
                    Util.AutoDetect(c);
                    if (c.Target < 0) { Error("Kein Ziel eingestellt. Bitte MonitorSwitch.exe ohne Parameter starten und das Ziel im Tray-Menü wählen."); return 1; }
                    c.Save();
                }
                value = c.Target;
            }
            else if (a == "--set" && args.Length > 1) value = Config.ParseInt(args[1]);
            else
            {
                MessageBox.Show("Verwendung:\n" +
                    "  MonitorSwitch.exe                 Tray-Modus\n" +
                    "  MonitorSwitch.exe --target        zum eingestellten Ziel wechseln\n" +
                    "  MonitorSwitch.exe --set 0x11      zu einem bestimmten Eingang wechseln\n" +
                    "  MonitorSwitch.exe --list          Monitore und Eingänge anzeigen\n" +
                    "  MonitorSwitch.exe --clean         Pixelreinigung starten (ASUS ROG OLED)",
                    "MonitorSwitch", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 2;
            }

            if (c.Monitor.Length == 0) Util.AutoDetect(c);
            if (Ddc.SetInput(c.Monitor, value)) return 0;
            Error("Wechsel zu " + c.InputName(value) + " fehlgeschlagen.\nMonitor: " + (c.Monitor.Length > 0 ? c.Monitor : "(keiner gefunden)") +
                  "\nIst DDC/CI im Monitor-Menü (OSD) aktiviert?");
            return 1;
        }

        static void Error(string text)
        {
            MessageBox.Show(text, "MonitorSwitch", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
