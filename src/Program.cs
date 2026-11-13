// Capturo - ferramenta de captura de tela no estilo Lightshot.
// Compila com o csc.exe do .NET Framework 4.x (C# 5), sem dependências externas.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Drawing.Text;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Capturo
{
    // ------------------------------------------------------------------ Win32
    static class Native
    {
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc fn, IntPtr hMod, uint threadId);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType);
        [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

        // detecção de janelas sob o cursor
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc fn, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder sb, int max);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT r, int size);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int v, int size);
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

        // Retângulos das janelas visíveis, da frente para trás, em coordenadas de tela.
        public static List<Rectangle> VisibleWindowRects(IntPtr skip)
        {
            List<IntPtr> handles;
            return VisibleWindowRects(skip, out handles);
        }

        // handles: a janela de cada retângulo (IntPtr.Zero para os monitores, no fim da lista)
        public static List<Rectangle> VisibleWindowRects(IntPtr skip, out List<IntPtr> handles)
        {
            var list = new List<Rectangle>();
            var hs = new List<IntPtr>();
            handles = hs;
            var cls = new System.Text.StringBuilder(64);
            EnumWindows((h, l) =>
            {
                if (h == skip || !IsWindowVisible(h) || IsIconic(h)) return true;
                if ((GetWindowLong(h, -20) & 0x20) != 0) return true; // WS_EX_TRANSPARENT
                int cloaked;
                if (DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) return true; // DWMWA_CLOAKED
                cls.Length = 0; GetClassName(h, cls, cls.Capacity);
                string c = cls.ToString();
                if (c == "Progman" || c == "WorkerW") return true; // área de trabalho: cai no monitor
                RECT r;
                if (DwmGetWindowAttribute(h, 9, out r, Marshal.SizeOf(typeof(RECT))) != 0) // DWMWA_EXTENDED_FRAME_BOUNDS
                    if (!GetWindowRect(h, out r)) return true;
                var rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                if (rect.Width >= 20 && rect.Height >= 20) { list.Add(rect); hs.Add(h); }
                return true;
            }, IntPtr.Zero);
            foreach (var s in Screen.AllScreens) { list.Add(s.Bounds); hs.Add(IntPtr.Zero); }
            return list;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder sb, int max);
        [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")] static extern uint GetWindowPid(IntPtr hWnd, out uint pid);

        // "Aplicativo — Título" da janela (como no seletor de fontes do Recordly)
        public static string WindowLabel(IntPtr h)
        {
            var sb = new System.Text.StringBuilder(256);
            GetWindowText(h, sb, sb.Capacity);
            string title = sb.ToString().Trim(), app = null;
            try
            {
                uint pid;
                GetWindowPid(h, out pid);
                using (var p = System.Diagnostics.Process.GetProcessById((int)pid))
                {
                    try { app = p.MainModule.FileVersionInfo.FileDescription; } catch { }
                    if (string.IsNullOrEmpty(app)) app = p.ProcessName;
                }
            }
            catch { }
            app = app == null ? "" : app.Trim();
            string label = app.Length == 0 ? title
                : title.Length == 0 || title.IndexOf(app, StringComparison.OrdinalIgnoreCase) >= 0 ? (title.Length == 0 ? app : title)
                : app + " — " + title;
            if (label.Length == 0) label = "Janela";
            return label.Length > 70 ? label.Substring(0, 67) + "…" : label;
        }

        public static bool Down(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }

        public static void ForceForeground(IntPtr hwnd)
        {
            IntPtr fg = GetForegroundWindow();
            uint fgThread = GetWindowThreadProcessId(fg, IntPtr.Zero);
            uint me = GetCurrentThreadId();
            if (fgThread != me) AttachThreadInput(me, fgThread, true);
            SetForegroundWindow(hwnd);
            if (fgThread != me) AttachThreadInput(me, fgThread, false);
        }
    }

    // ----------------------------------------------------------------- Hotkey
    struct Hotkey
    {
        public const int Ctrl = 1, Shift = 2, Alt = 4, Win = 8;
        public int Mods, Vk;
        public Hotkey(int mods, int vk) { Mods = mods; Vk = vk; }
        public bool IsEmpty { get { return Vk == 0; } }
        public bool Same(Hotkey o) { return Vk != 0 && Vk == o.Vk && Mods == o.Mods; }

        public static int CurrentMods()
        {
            int m = 0;
            if (Native.Down(0x11)) m |= Ctrl;
            if (Native.Down(0x10)) m |= Shift;
            if (Native.Down(0x12)) m |= Alt;
            if (Native.Down(0x5B) || Native.Down(0x5C)) m |= Win;
            return m;
        }

        public static bool IsModifierKey(int vk)
        {
            return vk == 0x10 || vk == 0x11 || vk == 0x12 || (vk >= 0xA0 && vk <= 0xA5) || vk == 0x5B || vk == 0x5C;
        }

        // Teclas que podem ser usadas sozinhas, sem modificador, sem atrapalhar a digitação.
        public static bool AllowedAlone(int vk)
        {
            return vk == 0x2C || vk == 0x13 || vk == 0x91 || vk == 0x2D || (vk >= 0x70 && vk <= 0x87);
        }

        public static string KeyName(int vk)
        {
            switch (vk)
            {
                case 0x2C: return "Print Screen";
                case 0x13: return "Pause";
                case 0x91: return "Scroll Lock";
                case 0x2D: return "Insert";
                case 0x2E: return "Delete";
                case 0x24: return "Home";
                case 0x23: return "End";
                case 0x21: return "Page Up";
                case 0x22: return "Page Down";
                case 0x20: return "Espaço";
                case 0x0D: return "Enter";
                case 0x09: return "Tab";
                case 0x08: return "Backspace";
                case 0x1B: return "Esc";
                case 0x25: return "←";
                case 0x26: return "↑";
                case 0x27: return "→";
                case 0x28: return "↓";
            }
            if ((vk >= 0x30 && vk <= 0x39) || (vk >= 0x41 && vk <= 0x5A)) return ((char)vk).ToString();
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F);
            if (vk >= 0x60 && vk <= 0x69) return "Num " + (vk - 0x60);
            uint c = Native.MapVirtualKey((uint)vk, 2);
            if (c != 0) return ((char)(c & 0x7FFF)).ToString().ToUpperInvariant();
            return ((Keys)vk).ToString();
        }

        public override string ToString()
        {
            if (IsEmpty) return "Nenhum";
            var parts = new List<string>();
            if ((Mods & Ctrl) != 0) parts.Add("Ctrl");
            if ((Mods & Shift) != 0) parts.Add("Shift");
            if ((Mods & Alt) != 0) parts.Add("Alt");
            if ((Mods & Win) != 0) parts.Add("Win");
            parts.Add(KeyName(Vk));
            return string.Join(" + ", parts.ToArray());
        }

        public string Serialize() { return Mods + ":" + Vk; }

        public static Hotkey Parse(string s, Hotkey fallback)
        {
            var p = s.Split(':');
            int m, v;
            if (p.Length == 2 && int.TryParse(p[0], out m) && int.TryParse(p[1], out v)) return new Hotkey(m, v);
            return fallback;
        }
    }

    // --------------------------------------------------------------- Settings
    class Settings
    {
        public string SaveFolder;
        public string Format = "png";
        public bool QuickSave = false;
        public bool OpenAfterUpload = true;
        public int ColorArgb = Color.FromArgb(255, 235, 30, 50).ToArgb();
        public int Thickness = 3;
        public string ToolSizes = "";                                // tamanho de cada ferramenta: "1:3,3:5,10:6"
        public int IconScale = 100;                                  // % do tamanho das barras de ferramentas
        public Hotkey CaptureKey = new Hotkey(0, 0x2C);              // Print Screen
        public Hotkey FullKey = new Hotkey(Hotkey.Shift, 0x2C);      // Shift + Print Screen
        public Hotkey PinKey = new Hotkey(Hotkey.Ctrl, 0x2C);        // Ctrl + Print Screen
        public Hotkey RepeatKey = new Hotkey();                      // repetir a última área (sem atalho por padrão)
        public bool KeepHistory = true;                              // guarda as capturas na galeria
        public bool Frame = false;                                   // moldura com sombra na imagem final
        public int FrameBg = 0;                                      // fundo da moldura (Art.FrameBgName)
        public int FramePadding = 28, FrameRadius = 10, FrameShadow = 50;   // margem e cantos (px), sombra (0..100)
        public int CaptureDelay = 0;                                 // atraso (s) do atalho "Capturar área" e do clique no ícone
        public Hotkey ScrollKey = new Hotkey();                      // print com rolagem (sem atalho por padrão)
        public Rectangle LastArea = Rectangle.Empty;                 // última área capturada (coordenadas de tela)
        // gravação de vídeo: áudio e as últimas escolhas do editor
        public bool RecMic = false, RecSys = true;
        public Hotkey VideoKey = new Hotkey();                       // gravar vídeo (sem atalho por padrão)
        public int VidFps = 30, VidCountdown = 3, VidMaxMin = 10;    // gravação: fps, contagem antes (s), limite (min)
        public bool VidOpenEditor = true;                            // ao parar, abre o editor (senão exporta direto)
        public bool VidCam = false;                                  // gravar a webcam junto (dá para ligar na barra também)
        public string VidCamName = "";                               // qual câmera (vazio: a primeira)
        public string VidStyle = "";                                 // últimas escolhas do editor (zoom, cursor, fundo...)

        public const int MinIcon = 60, MaxIcon = 160;

        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Capturo");
        static string FilePath { get { return Path.Combine(Dir, "settings.ini"); } }
        static string HistoryPath { get { return Path.Combine(Dir, "uploads.txt"); } }

        public static Settings Load()
        {
            var s = new Settings();
            s.SaveFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Capturo");
            try
            {
                if (File.Exists(FilePath))
                {
                    foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                    {
                        int i = line.IndexOf('=');
                        if (i <= 0) continue;
                        string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                        switch (k)
                        {
                            case "SaveFolder": if (v.Length > 0) s.SaveFolder = v; break;
                            case "Format": s.Format = v == "jpg" ? "jpg" : "png"; break;
                            case "QuickSave": s.QuickSave = v == "1"; break;
                            case "OpenAfterUpload": s.OpenAfterUpload = v == "1"; break;
                            case "Color": int c; if (int.TryParse(v, out c)) s.ColorArgb = c; break;
                            case "Thickness": int t; if (int.TryParse(v, out t)) s.Thickness = Math.Max(1, Math.Min(20, t)); break;
                            case "ToolSizes": s.ToolSizes = v; break;
                            case "IconScale": int z; if (int.TryParse(v, out z)) s.IconScale = Math.Max(MinIcon, Math.Min(MaxIcon, z)); break;
                            case "CaptureKey": s.CaptureKey = Hotkey.Parse(v, s.CaptureKey); break;
                            case "FullKey": s.FullKey = Hotkey.Parse(v, s.FullKey); break;
                            case "PinKey": s.PinKey = Hotkey.Parse(v, s.PinKey); break;
                            case "RepeatKey": s.RepeatKey = Hotkey.Parse(v, s.RepeatKey); break;
                            case "KeepHistory": s.KeepHistory = v != "0"; break;
                            case "Frame": s.Frame = v == "1"; break;
                            case "FrameBg": int fb; if (int.TryParse(v, out fb)) s.FrameBg = Math.Max(0, fb); break;
                            case "FramePadding": int fp; if (int.TryParse(v, out fp)) s.FramePadding = Math.Max(0, Math.Min(160, fp)); break;
                            case "FrameRadius": int fr; if (int.TryParse(v, out fr)) s.FrameRadius = Math.Max(0, Math.Min(60, fr)); break;
                            case "FrameShadow": int fs; if (int.TryParse(v, out fs)) s.FrameShadow = Math.Max(0, Math.Min(100, fs)); break;
                            case "CaptureDelay": int cd; if (int.TryParse(v, out cd)) s.CaptureDelay = Math.Max(0, Math.Min(10, cd)); break;
                            case "ScrollKey": s.ScrollKey = Hotkey.Parse(v, s.ScrollKey); break;
                            case "RecMic": s.RecMic = v == "1"; break;
                            case "RecSys": s.RecSys = v != "0"; break;
                            case "VideoKey": s.VideoKey = Hotkey.Parse(v, s.VideoKey); break;
                            case "VidFps": int vf; if (int.TryParse(v, out vf)) s.VidFps = vf >= 60 ? 60 : 30; break;
                            case "VidCountdown": int vc; if (int.TryParse(v, out vc)) s.VidCountdown = Math.Max(0, Math.Min(10, vc)); break;
                            case "VidMaxMin": int vm; if (int.TryParse(v, out vm)) s.VidMaxMin = Math.Max(1, Math.Min(15, vm)); break;
                            case "VidOpenEditor": s.VidOpenEditor = v != "0"; break;
                            case "VidCam": s.VidCam = v == "1"; break;
                            case "VidCamName": s.VidCamName = v; break;
                            case "VidStyle": s.VidStyle = v; break;
                            case "LastArea":
                                {
                                    var a = v.Split(',');
                                    int x, y, w, h;
                                    if (a.Length == 4 && int.TryParse(a[0], out x) && int.TryParse(a[1], out y) && int.TryParse(a[2], out w) && int.TryParse(a[3], out h))
                                        s.LastArea = new Rectangle(x, y, w, h);
                                    break;
                                }
                        }
                    }
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var sb = new StringBuilder();
                sb.AppendLine("SaveFolder=" + SaveFolder);
                sb.AppendLine("Format=" + Format);
                sb.AppendLine("QuickSave=" + (QuickSave ? "1" : "0"));
                sb.AppendLine("OpenAfterUpload=" + (OpenAfterUpload ? "1" : "0"));
                sb.AppendLine("Color=" + ColorArgb);
                sb.AppendLine("Thickness=" + Thickness);
                sb.AppendLine("ToolSizes=" + ToolSizes);
                sb.AppendLine("IconScale=" + IconScale);
                sb.AppendLine("CaptureKey=" + CaptureKey.Serialize());
                sb.AppendLine("FullKey=" + FullKey.Serialize());
                sb.AppendLine("PinKey=" + PinKey.Serialize());
                sb.AppendLine("RepeatKey=" + RepeatKey.Serialize());
                sb.AppendLine("KeepHistory=" + (KeepHistory ? "1" : "0"));
                sb.AppendLine("Frame=" + (Frame ? "1" : "0"));
                sb.AppendLine("FrameBg=" + FrameBg);
                sb.AppendLine("FramePadding=" + FramePadding);
                sb.AppendLine("FrameRadius=" + FrameRadius);
                sb.AppendLine("FrameShadow=" + FrameShadow);
                sb.AppendLine("CaptureDelay=" + CaptureDelay);
                sb.AppendLine("ScrollKey=" + ScrollKey.Serialize());
                sb.AppendLine("LastArea=" + LastArea.X + "," + LastArea.Y + "," + LastArea.Width + "," + LastArea.Height);
                sb.AppendLine("RecMic=" + (RecMic ? "1" : "0"));
                sb.AppendLine("RecSys=" + (RecSys ? "1" : "0"));
                sb.AppendLine("VideoKey=" + VideoKey.Serialize());
                sb.AppendLine("VidFps=" + VidFps);
                sb.AppendLine("VidCountdown=" + VidCountdown);
                sb.AppendLine("VidMaxMin=" + VidMaxMin);
                sb.AppendLine("VidOpenEditor=" + (VidOpenEditor ? "1" : "0"));
                sb.AppendLine("VidCam=" + (VidCam ? "1" : "0"));
                sb.AppendLine("VidCamName=" + VidCamName);
                sb.AppendLine("VidStyle=" + VidStyle);
                File.WriteAllText(FilePath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        public static void AddHistory(string url)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(HistoryPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "\t" + url + Environment.NewLine);
            }
            catch { }
        }

        public static List<string[]> History(int max)
        {
            var list = new List<string[]>();
            try
            {
                if (!File.Exists(HistoryPath)) return list;
                var lines = File.ReadAllLines(HistoryPath);
                for (int i = lines.Length - 1; i >= 0 && list.Count < max; i--)
                {
                    var p = lines[i].Split('\t');
                    if (p.Length == 2) list.Add(p);
                }
            }
            catch { }
            return list;
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public static bool StartWithWindows
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue("Capturo") != null;
            }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) k.SetValue("Capturo", "\"" + Application.ExecutablePath + "\"");
                    else if (k.GetValue("Capturo") != null) k.DeleteValue("Capturo");
                }
            }
        }
    }

    // ------------------------------------------------------------- Services
    static class Services
    {
        public static Bitmap CaptureVirtualScreen(out Rectangle vs)
        {
            vs = SystemInformation.VirtualScreen;
            var bmp = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(vs.Left, vs.Top, 0, 0, vs.Size, CopyPixelOperation.SourceCopy);
            return bmp;
        }

        public static string UniquePath(string folder, string ext)
        {
            Directory.CreateDirectory(folder);
            string baseName = "Captura_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string path = Path.Combine(folder, baseName + "." + ext);
            int n = 2;
            while (File.Exists(path)) path = Path.Combine(folder, baseName + " (" + (n++) + ")." + ext);
            return path;
        }

        public static void SaveImage(Bitmap bmp, string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".jpg" || ext == ".jpeg")
            {
                using (var rgb = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format24bppRgb))
                {
                    // JPEG não tem transparência: o que for transparente (moldura) vira branco, não preto
                    using (var g = Graphics.FromImage(rgb)) { g.Clear(Color.White); g.DrawImage(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height)); }
                    ImageCodecInfo codec = null;
                    foreach (var c in ImageCodecInfo.GetImageEncoders()) if (c.MimeType == "image/jpeg") codec = c;
                    var ep = new EncoderParameters(1);
                    ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
                    rgb.Save(path, codec, ep);
                }
            }
            else if (ext == ".bmp") bmp.Save(path, ImageFormat.Bmp);
            else bmp.Save(path, ImageFormat.Png);
        }

        // Copia a imagem como bitmap e também como PNG: navegadores, Word, Discord etc. leem o PNG
        // e mantêm a transparência (a moldura com fundo transparente).
        public static void SetClipboardImage(Bitmap bmp)
        {
            var data = new DataObject();
            data.SetImage(bmp);
            data.SetData("PNG", false, new MemoryStream(ToPng(bmp)));
            Clipboard.SetDataObject(data, true);
        }

        public static byte[] ToPng(Bitmap bmp)
        {
            using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); return ms.ToArray(); }
        }

        // Upload anônimo para catbox.moe (sem conta / chave de API). Retorna a URL pública.
        public static string Upload(byte[] png)
        {
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)(3072 | 12288); }
            catch { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; }

            string boundary = "----Capturo" + DateTime.Now.Ticks.ToString("x");
            var req = (HttpWebRequest)WebRequest.Create("https://catbox.moe/user/api.php");
            req.Method = "POST";
            req.UserAgent = "Capturo/1.0";
            req.ContentType = "multipart/form-data; boundary=" + boundary;
            req.Timeout = 60000;

            using (var body = new MemoryStream())
            {
                Action<string> w = s => { var b = Encoding.UTF8.GetBytes(s); body.Write(b, 0, b.Length); };
                w("--" + boundary + "\r\nContent-Disposition: form-data; name=\"reqtype\"\r\n\r\nfileupload\r\n");
                w("--" + boundary + "\r\nContent-Disposition: form-data; name=\"fileToUpload\"; filename=\"captura.png\"\r\nContent-Type: image/png\r\n\r\n");
                body.Write(png, 0, png.Length);
                w("\r\n--" + boundary + "--\r\n");
                req.ContentLength = body.Length;
                using (var rs = req.GetRequestStream()) body.WriteTo(rs);
            }
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream()))
            {
                string url = sr.ReadToEnd().Trim();
                if (!url.StartsWith("http")) throw new Exception(url.Length > 0 ? url : "resposta vazia do servidor");
                return url;
            }
        }

        public static void Print(Bitmap bmp)
        {
            var pd = new PrintDocument();
            pd.DocumentName = "Captura de tela";
            pd.DefaultPageSettings.Landscape = bmp.Width > bmp.Height;
            pd.PrintPage += (s, e) =>
            {
                Rectangle m = e.MarginBounds;
                float scale = Math.Min(1f, Math.Min((float)m.Width / bmp.Width, (float)m.Height / bmp.Height));
                float w = bmp.Width * scale, h = bmp.Height * scale;
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                e.Graphics.DrawImage(bmp, m.Left + (m.Width - w) / 2, m.Top, w, h);
            };
            using (var dlg = new PrintDialog())
            {
                dlg.Document = pd;
                dlg.UseEXDialog = true;
                if (dlg.ShowDialog() == DialogResult.OK) pd.Print();
            }
        }

        public static void Open(string target)
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
        }
    }

    // ------------------------------------------------------------------ Art
    static class Art
    {
        public static readonly Color Accent = Color.FromArgb(124, 77, 255);
        public static readonly Color PickBlue = Color.FromArgb(10, 132, 255);   // seleção da fonte da gravação (como no Recordly)
        public static readonly Color Ink = Color.FromArgb(52, 52, 60);
        public static readonly Color Pill = Color.FromArgb(232, 24, 24, 30);

        public static readonly Color[] Palette = {
            Color.FromArgb(235, 30, 50), Color.FromArgb(255, 140, 0), Color.FromArgb(255, 210, 0), Color.FromArgb(40, 190, 90), Color.FromArgb(0, 180, 220),
            Color.FromArgb(40, 110, 255), Color.FromArgb(124, 77, 255), Color.FromArgb(240, 70, 160), Color.White, Color.FromArgb(20, 20, 24) };

        public static Bitmap AppBitmap(int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float s = size / 32f;
                using (var path = RoundRect(new RectangleF(1 * s, 1 * s, 30 * s, 30 * s), 7 * s))
                using (var br = new LinearGradientBrush(new PointF(0, 0), new PointF(size, size),
                    Color.FromArgb(150, 100, 255), Color.FromArgb(90, 50, 220)))
                    g.FillPath(br, path);
                using (var pen = new Pen(Color.White, 3f * s))
                {
                    pen.StartCap = pen.EndCap = LineCap.Round;
                    float a = 8 * s, b = 24 * s, l = 5 * s;
                    g.DrawLines(pen, new[] { new PointF(a, a + l), new PointF(a, a), new PointF(a + l, a) });
                    g.DrawLines(pen, new[] { new PointF(b - l, a), new PointF(b, a), new PointF(b, a + l) });
                    g.DrawLines(pen, new[] { new PointF(a, b - l), new PointF(a, b), new PointF(a + l, b) });
                    g.DrawLines(pen, new[] { new PointF(b - l, b), new PointF(b, b), new PointF(b, b - l) });
                }
                g.FillEllipse(Brushes.White, 13 * s, 13 * s, 6 * s, 6 * s);
            }
            return bmp;
        }

        public static Icon AppIcon()
        {
            int sz = SystemInformation.SmallIconSize.Width >= 24 ? 32 : 16;
            using (var bmp = AppBitmap(sz)) return Icon.FromHandle(bmp.GetHicon());
        }

        // Grava um .ico com PNG 256x256 embutido (usado pelo build para o ícone do .exe).
        public static void WriteIco(string path)
        {
            byte[] png;
            using (var bmp = AppBitmap(256)) png = Services.ToPng(bmp);
            using (var fs = File.Create(path))
            using (var w = new BinaryWriter(fs))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)1);
                w.Write((byte)0); w.Write((byte)0); w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32); w.Write(png.Length); w.Write(22);
                w.Write(png);
            }
        }

        public static GraphicsPath RoundRect(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Color c, RectangleF r, float rad)
        {
            using (var p = RoundRect(r, rad)) using (var b = new SolidBrush(c)) g.FillPath(b, p);
        }

        // Sombra suave em camadas
        public static void Shadow(Graphics g, RectangleF r, float rad, float s)
        {
            for (int i = 4; i >= 1; i--)
            {
                var sr = r; sr.Inflate(i * 1.5f * s, i * 1.5f * s); sr.Offset(0, 2 * s);
                FillRound(g, Color.FromArgb(14, 0, 0, 0), sr, rad + i * 1.5f * s);
            }
        }

        // ---- moldura das capturas (estilo nas configurações)
        // fundos: cores lisas, transparente, os degradês e os papéis de parede do editor de vídeo
        static readonly string[] FrameBgBase = { "Cinza claro", "Branco", "Escuro", "Transparente",
            "Degradê violeta", "Degradê aurora", "Degradê oceano", "Degradê pôr do sol", "Degradê floresta", "Degradê pêssego", "Degradê meia-noite" };
        const int FrameGradStart = 4, FrameWallStart = 11;
        public static int FrameBgCount { get { return FrameWallStart + VideoProject.BackgroundCount - VideoProject.WallStart; } }
        public static string FrameBgName(int i)
        {
            if (i < FrameWallStart) return FrameBgBase[i];
            return VideoProject.BackgroundName(VideoProject.WallStart + i - FrameWallStart);
        }

        // Margem em uso (a imagem fixada na tela fica deslocada por ela para o conteúdo não sair do lugar).
        public static int FramePadNow { get { return TrayApp.S.FramePadding; } }

        public static Bitmap Framed(Bitmap src)
        {
            var S = TrayApp.S;
            return Framed(src, S.FrameBg, S.FramePadding, S.FrameRadius, S.FrameShadow);
        }

        // Moldura "de documentação": fundo, cantos arredondados e sombra suave.
        public static Bitmap Framed(Bitmap src, int bgIndex, int pad, int radius, int shadow)
        {
            if (bgIndex < 0 || bgIndex >= FrameBgCount) bgIndex = 0;
            float rad = Math.Min(radius, Math.Min(src.Width, src.Height) / 2f), s = shadow / 100f;
            var b = new Bitmap(src.Width + pad * 2, src.Height + pad * 2, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var full = new Rectangle(0, 0, b.Width, b.Height);
                switch (bgIndex)
                {
                    case 0: g.Clear(Color.FromArgb(235, 237, 242)); break;
                    case 1: g.Clear(Color.White); break;
                    case 2: g.Clear(Color.FromArgb(30, 31, 36)); break;
                    case 3: g.Clear(Color.Transparent); break;
                    default:
                        if (bgIndex < FrameWallStart)
                        {
                            var pair = VideoProject.BackgroundPair(bgIndex - FrameGradStart + 1);
                            using (var br = new LinearGradientBrush(full, pair[0], pair[1], 35f)) g.FillRectangle(br, full);
                        }
                        else VideoProject.DrawWallpaperCover(g, VideoProject.WallStart + bgIndex - FrameWallStart, full);
                        break;
                }
                var r = new RectangleF(pad, pad, src.Width, src.Height);
                if (s > 0 && pad > 0)
                {
                    // sombra em camadas: mais forte = mais escura, maior e mais deslocada para baixo
                    float blur = Math.Min(pad, 4 + 16 * s), off = Math.Min(pad / 2f, 1 + 5 * s);
                    int steps = 8, a = (int)Math.Round(3 + 16 * s);
                    for (int i = steps; i >= 1; i--)
                    {
                        var sr = r; float e = blur * i / steps;
                        sr.Inflate(e, e); sr.Offset(0, off);
                        FillRound(g, Color.FromArgb(a, 0, 0, 0), sr, rad + e);
                    }
                }
                if (rad >= 0.5f)
                {
                    using (var path = RoundRect(r, rad))
                    using (var tb = new TextureBrush(src))
                    {
                        tb.TranslateTransform(pad, pad);
                        g.FillPath(tb, path);
                    }
                }
                else
                {
                    g.DrawImage(src, new Rectangle(pad, pad, src.Width, src.Height), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel);
                }
                if (pad > 0)
                    using (var path = RoundRect(r, Math.Max(rad, 0.01f)))
                    using (var pen = new Pen(Color.FromArgb(bgIndex == 2 ? 40 : 35, bgIndex == 2 ? 255 : 0, bgIndex == 2 ? 255 : 0, bgIndex == 2 ? 255 : 0), 1)) g.DrawPath(pen, path);
            }
            return b;
        }

        public static void DrawArrow(Graphics g, Color c, float w, PointF a, PointF b)
        {
            float dx = b.X - a.X, dy = b.Y - a.Y;
            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 1) return;
            float head = Math.Min(len, 10 + w * 3.2f), hw = head * 0.5f;
            float ux = dx / len, uy = dy / len;
            var basePt = new PointF(b.X - ux * head, b.Y - uy * head);
            var p1 = new PointF(basePt.X - uy * hw, basePt.Y + ux * hw);
            var p2 = new PointF(basePt.X + uy * hw, basePt.Y - ux * hw);
            using (var pen = new Pen(c, w))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Flat;
                g.DrawLine(pen, a, new PointF(b.X - ux * head * 0.7f, b.Y - uy * head * 0.7f));
            }
            using (var br = new SolidBrush(c)) g.FillPolygon(br, new[] { b, p1, p2 });
        }

        // Ícones dos botões, desenhados vetorialmente.
        public static void Glyph(Graphics g, int id, Rectangle r, Color fg, Color current)
        {
            float s = r.Width / 38f;
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            Func<float, float, PointF> P = (x, y) => new PointF(cx + x * s, cy + y * s);
            using (var pen = new Pen(fg, 1.8f * s))
            using (var br = new SolidBrush(fg))
            {
                pen.StartCap = pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                switch (id)
                {
                    case 1: // lápis
                        g.DrawPolygon(pen, new[] { P(-7, 7), P(-6, 3), P(4, -7), P(7, -4), P(-3, 6) });
                        g.DrawLine(pen, P(2, -5), P(5, -2));
                        break;
                    case 2: g.DrawLine(pen, P(-7, 7), P(7, -7)); break; // linha
                    case 3: DrawArrow(g, fg, 1.8f * s, P(-7, 7), P(7, -7)); break; // seta
                    case 4: // retângulo
                        using (var rp = RoundRect(new RectangleF(cx - 8 * s, cy - 6 * s, 16 * s, 12 * s), 1.5f * s)) g.DrawPath(pen, rp);
                        break;
                    case 5: // marcador
                        using (var hp = new Pen(Color.FromArgb(120, current), 6 * s)) { hp.StartCap = hp.EndCap = LineCap.Round; g.DrawLine(hp, P(-7, 3), P(7, -3)); }
                        g.DrawLine(pen, P(-7, 8), P(7, 8));
                        break;
                    case 6: // texto
                        g.DrawLine(pen, P(-6, -7), P(6, -7));
                        g.DrawLine(pen, P(0, -7), P(0, 7));
                        g.DrawLine(pen, P(-3, 7), P(3, 7));
                        break;
                    case 7: // cor
                        using (var cb = new SolidBrush(current))
                        using (var bp = new Pen(Color.FromArgb(70, 0, 0, 0), 1f * s))
                        {
                            g.FillEllipse(Brushes.White, cx - 9 * s, cy - 9 * s, 18 * s, 18 * s);
                            g.DrawEllipse(bp, cx - 9 * s, cy - 9 * s, 18 * s, 18 * s);
                            g.FillEllipse(cb, cx - 6.5f * s, cy - 6.5f * s, 13 * s, 13 * s);
                        }
                        break;
                    case 8: // desfazer
                        g.DrawArc(pen, cx - 6 * s, cy - 5 * s, 13 * s, 12 * s, 200, 250);
                        g.FillPolygon(br, new[] { P(-9, -3), P(-2, -3), P(-6, 3) });
                        break;
                    case 9: // pixelar
                        using (var rp = RoundRect(new RectangleF(cx - 8 * s, cy - 8 * s, 16 * s, 16 * s), 2f * s)) g.DrawPath(pen, rp);
                        for (int i = 0; i < 3; i++)
                            for (int j = 0; j < 3; j++)
                                if ((i + j) % 2 == 0) g.FillRectangle(br, cx + (-6 + i * 4) * s, cy + (-6 + j * 4) * s, 4 * s, 4 * s);
                        break;
                    case 10: // numeração
                        g.DrawEllipse(pen, cx - 8.5f * s, cy - 8.5f * s, 17 * s, 17 * s);
                        g.DrawLines(pen, new[] { P(-2.5f, -2.5f), P(0.5f, -4.5f), P(0.5f, 4.5f) });
                        break;
                    case 11: // elipse
                        g.DrawEllipse(pen, cx - 8.5f * s, cy - 6.5f * s, 17 * s, 13 * s);
                        break;
                    case 12: // destaque
                        using (var db = new SolidBrush(Color.FromArgb(90, fg)))
                        using (var rp = RoundRect(new RectangleF(cx - 9 * s, cy - 7.5f * s, 18 * s, 15 * s), 2f * s)) g.FillPath(db, rp);
                        g.FillRectangle(Brushes.White, cx - 4 * s, cy - 3 * s, 8 * s, 6 * s);
                        g.DrawRectangle(pen, cx - 4 * s, cy - 3 * s, 8 * s, 6 * s);
                        break;
                    case 13: // mover / editar (cursor)
                        g.DrawPolygon(pen, new[] { P(-4.5f, -8), P(-4.5f, 6), P(-1, 2.8f), P(1.5f, 8), P(3.7f, 7), P(1.3f, 1.8f), P(6, 1.8f) });
                        break;
                    case 28: // gravar GIF
                        g.DrawEllipse(pen, cx - 8.5f * s, cy - 8.5f * s, 17 * s, 17 * s);
                        using (var rb = new SolidBrush(Color.FromArgb(235, 30, 50))) g.FillEllipse(rb, cx - 4 * s, cy - 4 * s, 8 * s, 8 * s);
                        break;
                    case 31: // gravar vídeo (câmera)
                        using (var rp = RoundRect(new RectangleF(cx - 9 * s, cy - 6 * s, 13 * s, 12 * s), 2.5f * s)) g.DrawPath(pen, rp);
                        g.DrawPolygon(pen, new[] { P(4, -2), P(9, -5), P(9, 5), P(4, 2) });
                        using (var rb = new SolidBrush(Color.FromArgb(235, 30, 50))) g.FillEllipse(rb, cx - 5 * s, cy - 2.5f * s, 5 * s, 5 * s);
                        break;
                    case 32: // print com rolagem: página com seta para baixo
                        using (var rp = RoundRect(new RectangleF(cx - 7 * s, cy - 9 * s, 14 * s, 18 * s), 2f * s)) g.DrawPath(pen, rp);
                        g.DrawLine(pen, P(0, -5), P(0, 4));
                        g.DrawLines(pen, new[] { P(-3.5f, 1), P(0, 4.5f), P(3.5f, 1) });
                        break;
                    case 29: // copiar texto (OCR)
                        g.DrawLines(pen, new[] { P(-8, -4), P(-8, -8), P(-4, -8) });
                        g.DrawLines(pen, new[] { P(4, -8), P(8, -8), P(8, -4) });
                        g.DrawLines(pen, new[] { P(8, 4), P(8, 8), P(4, 8) });
                        g.DrawLines(pen, new[] { P(-4, 8), P(-8, 8), P(-8, 4) });
                        g.DrawLine(pen, P(-3.5f, -3.5f), P(3.5f, -3.5f));
                        g.DrawLine(pen, P(0, -3.5f), P(0, 4));
                        break;
                    case 30: // moldura com sombra
                        using (var sb2 = new SolidBrush(Color.FromArgb(70, fg))) g.FillRectangle(sb2, cx - 4.5f * s, cy - 1.5f * s, 11 * s, 8 * s);
                        using (var rp = RoundRect(new RectangleF(cx - 7.5f * s, cy - 6.5f * s, 12 * s, 10 * s), 1.5f * s))
                        {
                            g.FillPath(Brushes.White, rp);
                            g.DrawPath(pen, rp);
                        }
                        break;
                    case 20: // upload
                        g.DrawLine(pen, P(0, 4), P(0, -7));
                        g.DrawLines(pen, new[] { P(-5, -2), P(0, -7), P(5, -2) });
                        g.DrawLines(pen, new[] { P(-8, 3), P(-8, 7), P(8, 7), P(8, 3) });
                        break;
                    case 21: // compartilhar
                        g.DrawLine(pen, P(-5, 0), P(5, -6)); g.DrawLine(pen, P(-5, 0), P(5, 6));
                        g.FillEllipse(br, cx - 8 * s, cy - 3 * s, 6 * s, 6 * s);
                        g.FillEllipse(br, cx + 2 * s, cy - 9 * s, 6 * s, 6 * s);
                        g.FillEllipse(br, cx + 2 * s, cy + 3 * s, 6 * s, 6 * s);
                        break;
                    case 22: // buscar semelhantes
                        g.DrawEllipse(pen, cx - 7 * s, cy - 7 * s, 11 * s, 11 * s);
                        using (var tp = new Pen(fg, 2.6f * s)) { tp.StartCap = tp.EndCap = LineCap.Round; g.DrawLine(tp, P(3, 3), P(7, 7)); }
                        break;
                    case 23: // imprimir
                        g.DrawRectangle(pen, cx - 8 * s, cy - 3 * s, 16 * s, 8 * s);
                        g.DrawLines(pen, new[] { P(-5, -3), P(-5, -8), P(5, -8), P(5, -3) });
                        g.DrawRectangle(pen, cx - 5 * s, cy + 2 * s, 10 * s, 6 * s);
                        break;
                    case 24: // copiar
                        using (var rp = RoundRect(new RectangleF(cx - 7 * s, cy - 7 * s, 10 * s, 11 * s), 1.5f * s)) g.DrawPath(pen, rp);
                        g.DrawLines(pen, new[] { P(-1, 7), P(6, 7), P(6, -3) });
                        break;
                    case 25: // salvar
                        g.DrawPolygon(pen, new[] { P(-7, -7), P(4, -7), P(7, -4), P(7, 7), P(-7, 7) });
                        g.DrawRectangle(pen, cx - 4 * s, cy - 7 * s, 7 * s, 4 * s);
                        g.DrawRectangle(pen, cx - 4 * s, cy + 1 * s, 8 * s, 6 * s);
                        break;
                    case 26: // fechar
                        g.DrawLine(pen, P(-6, -6), P(6, 6)); g.DrawLine(pen, P(6, -6), P(-6, 6));
                        break;
                    case 27: // fixar (alfinete)
                        g.DrawPolygon(pen, new[] { P(-4, -8), P(4, -8), P(3, -6), P(3, -1), P(6, 2), P(-6, 2), P(-3, -1), P(-3, -6) });
                        g.DrawLine(pen, P(0, 2), P(0, 9));
                        break;
                }
            }
        }
    }

    // ------------------------------------------------------------ Annotations
    enum Tool { None = 0, Pen = 1, Line = 2, Arrow = 3, Rect = 4, Marker = 5, Text = 6, Blur = 9, Step = 10, Ellipse = 11, Spot = 12 }

    class Annot
    {
        public Tool Kind;
        public Color Color;
        public float Width;
        public List<PointF> Pts = new List<PointF>();
        public PointF A, B;
        public string Text = "";
        public Bitmap Src;              // pixelar: imagem original de onde sai o mosaico
        Bitmap mosaic; Rectangle mosaicRect; int mosaicBlock;

        public Font MakeFont() { return new Font("Segoe UI", 14 + Width * 3, FontStyle.Bold, GraphicsUnit.Pixel); }

        public static bool IsTool(int id) { return (id >= 1 && id <= 6) || (id >= 9 && id <= 12); }

        public Annot Clone()
        {
            var c = (Annot)MemberwiseClone();
            c.Pts = new List<PointF>(Pts);
            c.mosaic = null;
            return c;
        }

        // Posiciona a anotação no lugar de "orig" deslocado por (dx, dy).
        public void MoveFrom(Annot orig, float dx, float dy)
        {
            A = new PointF(orig.A.X + dx, orig.A.Y + dy);
            B = new PointF(orig.B.X + dx, orig.B.Y + dy);
            for (int i = 0; i < Pts.Count && i < orig.Pts.Count; i++) Pts[i] = new PointF(orig.Pts[i].X + dx, orig.Pts[i].Y + dy);
        }

        RectangleF Box { get { return RectangleF.FromLTRB(Math.Min(A.X, B.X), Math.Min(A.Y, B.Y), Math.Max(A.X, B.X), Math.Max(A.Y, B.Y)); } }
        float StrokeHalf { get { return (Kind == Tool.Marker ? Math.Max(10, Width * 5) : Width) / 2; } }

        static readonly Graphics measure = Graphics.FromImage(new Bitmap(1, 1));

        public RectangleF Bounds()
        {
            switch (Kind)
            {
                case Tool.Pen:
                case Tool.Marker:
                    {
                        if (Pts.Count == 0) return RectangleF.Empty;
                        float l = Pts[0].X, t = Pts[0].Y, r = l, b = t;
                        foreach (var q in Pts) { l = Math.Min(l, q.X); t = Math.Min(t, q.Y); r = Math.Max(r, q.X); b = Math.Max(b, q.Y); }
                        float w = StrokeHalf;
                        return RectangleF.FromLTRB(l - w, t - w, r + w, b + w);
                    }
                case Tool.Step:
                    {
                        float d = StepDiameter;
                        return new RectangleF(A.X - d / 2, A.Y - d / 2, d, d);
                    }
                case Tool.Text:
                    using (var f = MakeFont())
                    {
                        SizeF sz = measure.MeasureString(Text.Length == 0 ? "W" : Text, f);
                        return new RectangleF(A.X, A.Y, sz.Width, sz.Height);
                    }
                case Tool.Blur:
                case Tool.Spot:
                    return Box;
                default:
                    {
                        var r = Box;
                        float e = Width / 2 + (Kind == Tool.Arrow ? Width * 2 + 5 : 0);
                        r.Inflate(e, e);
                        return r;
                    }
            }
        }

        static float SegDist(PointF p, PointF a, PointF b)
        {
            float dx = b.X - a.X, dy = b.Y - a.Y, len = dx * dx + dy * dy;
            float t = len < 0.01f ? 0 : Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len));
            float x = a.X + t * dx - p.X, y = a.Y + t * dy - p.Y;
            return (float)Math.Sqrt(x * x + y * y);
        }

        public bool HitTest(PointF p, float tol)
        {
            switch (Kind)
            {
                case Tool.Pen:
                case Tool.Marker:
                    {
                        float w = StrokeHalf + tol;
                        if (Pts.Count == 1) return SegDist(p, Pts[0], Pts[0]) <= w;
                        for (int i = 1; i < Pts.Count; i++) if (SegDist(p, Pts[i - 1], Pts[i]) <= w) return true;
                        return false;
                    }
                case Tool.Line:
                case Tool.Arrow:
                    return SegDist(p, A, B) <= Width / 2 + tol + (Kind == Tool.Arrow ? Width : 0);
                case Tool.Rect:
                case Tool.Ellipse:
                    {
                        var r = Box;
                        float w = Width / 2 + tol;
                        var outer = r; outer.Inflate(w, w);
                        if (!outer.Contains(p)) return false;
                        var inner = r; inner.Inflate(-w, -w);
                        if (inner.Width <= 0 || inner.Height <= 0) return true;
                        if (Kind == Tool.Rect) return !inner.Contains(p);
                        // elipse: distância aproximada ao contorno
                        float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, rx = Math.Max(1, r.Width / 2), ry = Math.Max(1, r.Height / 2);
                        double nx = (p.X - cx) / rx, ny = (p.Y - cy) / ry, d = Math.Sqrt(nx * nx + ny * ny);
                        return Math.Abs(d - 1) * Math.Min(rx, ry) <= w;
                    }
                default:
                    {
                        var b = Bounds(); b.Inflate(tol / 2, tol / 2);
                        return b.Contains(p);
                    }
            }
        }

        public float StepDiameter { get { return 24 + Width * 3; } }

        public void DisposeCache() { if (mosaic != null) { mosaic.Dispose(); mosaic = null; } }

        void DrawMosaic(Graphics g)
        {
            if (Src == null) return;
            var r = Rectangle.FromLTRB((int)Math.Min(A.X, B.X), (int)Math.Min(A.Y, B.Y), (int)Math.Max(A.X, B.X), (int)Math.Max(A.Y, B.Y));
            r.Intersect(new Rectangle(0, 0, Src.Width, Src.Height));
            if (r.Width < 2 || r.Height < 2) return;
            int bs = Math.Max(6, (int)(Width * 2.5f) + 6);
            int sw = (r.Width + bs - 1) / bs, sh = (r.Height + bs - 1) / bs;
            if (mosaic == null || mosaicRect != r || mosaicBlock != bs)
            {
                DisposeCache();
                mosaic = new Bitmap(sw, sh, PixelFormat.Format32bppArgb);
                using (var mg = Graphics.FromImage(mosaic))
                {
                    // reduz com média dos pixels: cada bloco vira uma cor
                    mg.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    mg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    mg.CompositingMode = CompositingMode.SourceCopy;
                    using (var ia = new ImageAttributes())
                    {
                        ia.SetWrapMode(WrapMode.TileFlipXY);
                        mg.DrawImage(Src, new Rectangle(0, 0, sw, sh), r.X, r.Y, sw * bs, sh * bs, GraphicsUnit.Pixel, ia);
                    }
                }
                mosaicRect = r; mosaicBlock = bs;
            }
            var st = g.Save();
            g.SetClip(r, CombineMode.Intersect);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImage(mosaic, new Rectangle(r.X, r.Y, sw * bs, sh * bs), 0, 0, sw, sh, GraphicsUnit.Pixel);
            g.Restore(st);
        }

        void DrawStep(Graphics g)
        {
            float d = StepDiameter;
            var rc = new RectangleF(A.X - d / 2, A.Y - d / 2, d, d);
            using (var sh = new SolidBrush(Color.FromArgb(60, 0, 0, 0))) g.FillEllipse(sh, rc.X, rc.Y + 1.5f, d, d);
            using (var fill = new SolidBrush(Color)) g.FillEllipse(fill, rc);
            using (var ring = new Pen(Color.White, Math.Max(2f, d / 14))) g.DrawEllipse(ring, rc);
            bool light = Color.R * 0.299 + Color.G * 0.587 + Color.B * 0.114 > 170;
            using (var f = new Font("Segoe UI", d * (Text.Length > 1 ? 0.42f : 0.52f), FontStyle.Bold, GraphicsUnit.Pixel))
            using (var tb = new SolidBrush(light ? Color.FromArgb(22, 22, 28) : Color.White))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                rc.Offset(0, d * 0.02f);
                g.DrawString(Text, f, tb, rc, sf);
            }
        }

        public void Draw(Graphics g, bool editing, bool caretOn)
        {
            switch (Kind)
            {
                case Tool.Blur: DrawMosaic(g); break;
                case Tool.Step: DrawStep(g); break;
                case Tool.Spot: break; // desenhado em conjunto pela sobreposição (escurece o resto)
                case Tool.Ellipse:
                    using (var pen = new Pen(Color, Width)) g.DrawEllipse(pen, Box);
                    break;
                case Tool.Pen:
                case Tool.Marker:
                    {
                        bool marker = Kind == Tool.Marker;
                        using (var pen = new Pen(marker ? Color.FromArgb(110, Color) : Color, marker ? Math.Max(10, Width * 5) : Width))
                        {
                            pen.StartCap = pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                            if (Pts.Count == 1)
                                using (var br = new SolidBrush(pen.Color)) g.FillEllipse(br, Pts[0].X - pen.Width / 2, Pts[0].Y - pen.Width / 2, pen.Width, pen.Width);
                            else if (Pts.Count > 1) g.DrawLines(pen, Pts.ToArray());
                        }
                        break;
                    }
                case Tool.Line:
                    using (var pen = new Pen(Color, Width)) { pen.StartCap = pen.EndCap = LineCap.Round; g.DrawLine(pen, A, B); }
                    break;
                case Tool.Arrow:
                    Art.DrawArrow(g, Color, Width, A, B);
                    break;
                case Tool.Rect:
                    using (var pen = new Pen(Color, Width))
                    {
                        pen.LineJoin = LineJoin.Miter;
                        g.DrawRectangle(pen, Math.Min(A.X, B.X), Math.Min(A.Y, B.Y), Math.Abs(B.X - A.X), Math.Abs(B.Y - A.Y));
                    }
                    break;
                case Tool.Text:
                    using (var f = MakeFont())
                    using (var br = new SolidBrush(Color))
                    {
                        if (editing)
                        {
                            SizeF sz = g.MeasureString(Text.Length == 0 ? "W" : Text, f);
                            using (var dp = new Pen(Color.FromArgb(200, 255, 255, 255), 1)) { dp.DashStyle = DashStyle.Dash; g.DrawRectangle(dp, A.X - 4, A.Y - 3, sz.Width + 10, sz.Height + 6); }
                            if (caretOn)
                            {
                                // caret no fim da última linha
                                string[] lines = Text.Split('\n');
                                string last = lines[lines.Length - 1];
                                float lh = f.GetHeight(g);
                                float cx = A.X + (last.Length == 0 ? 2 : g.MeasureString(last, f).Width - 4);
                                float cy = A.Y + lh * (lines.Length - 1) + 2;
                                using (var cp = new Pen(Color, Math.Max(1.5f, f.Size / 14))) g.DrawLine(cp, cx, cy, cx, cy + lh - 4);
                            }
                        }
                        g.DrawString(Text, f, br, A);
                    }
                    break;
            }
        }
    }

    // ---------------------------------------------------------------- Overlay
    class Btn { public int Id; public Rectangle R; }

    class OverlayForm : Form
    {
        enum Drag { None, NewSel, Move, Resize, Draw, MoveAnnot }

        static readonly int[] ToolIds = { 13, 1, 2, 3, 4, 11, 5, 6, 9, 10, 12, -1, 7, 8 };
        static readonly int[] ActIds = { 20, 21, 22, 23, -1, 29, 31, 28, 32, 30, -1, 27, 24, 25, -1, 26 };

        readonly TrayApp app;
        readonly Bitmap shot, dim;   // dim: null na edição de imagem (print com rolagem)
        readonly Rectangle vs;
        readonly bool pinMode;
        Rectangle sel; bool hasSel;
        Drag drag = Drag.None; int handle = -1;
        Point dragStart; Rectangle selStart;
        Tool tool = Tool.None;
        Color color; int thick;
        readonly List<Annot> annots = new List<Annot>();
        // desfazer/refazer guardam cópias completas da lista de anotações
        readonly List<List<Annot>> undoStack = new List<List<Annot>>(), redoStack = new List<List<Annot>>();
        Annot cur, editingText;
        bool textIsNew;
        Annot picked, pickedOrig;          // anotação selecionada com a ferramenta Mover
        List<Annot> pendingSnap;           // estado antes de arrastar a anotação
        public bool GifMode;               // a captura foi aberta para gravar GIF (Enter grava)
        public bool VideoMode;             // a captura foi aberta para gravar vídeo (Enter grava)
        public bool ScrollMode;            // a captura foi aberta para o print com rolagem (Enter começa)
        bool caretOn = true;
        readonly List<Btn> btns = new List<Btn>();
        Rectangle toolPanel, actPanel;
        Btn hover;
        Point mouse = new Point(-1000, -1000);
        readonly float scale;
        string flash; DateTime flashUntil;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        ContextMenuStrip shareMenu;
        // paleta rápida
        bool paletteOpen; Rectangle paletteRect, moreRect; readonly Rectangle[] swatchRects = new Rectangle[Art.Palette.Length];
        int hoverSwatch = -1; bool hoverMore;
        // tamanho (espessura) no painel de cores: controle deslizante de 1 a 20
        const int MinThick = 1, MaxThick = 20;
        Rectangle sizeTrack, sizeLabel; bool sizeDrag, hoverSize;
        // cada ferramenta lembra o próprio tamanho (números, seta, lápis...)
        readonly Dictionary<Tool, int> toolThick = new Dictionary<Tool, int>();
        readonly Font fontUi, fontUiB, fontSmall;
        // janelas visíveis no momento da captura (coordenadas do cliente, da frente para trás)
        readonly List<Rectangle> windows = new List<Rectangle>();
        readonly List<IntPtr> windowHandles = new List<IntPtr>();
        Rectangle hoverWin;
        // seleção feita clicando numa janela: no vídeo, grava a própria janela (acompanha se mover)
        IntPtr selWindow; Rectangle selWindowRect;

        // Edição de uma imagem maior que a tela (print com rolagem): a imagem rola dentro da janela,
        // as barras ficam presas na tela. Coordenadas: janela = imagem + view.
        readonly bool docMode;
        Point view;
        bool panning; Point panStart, panView;
        DateTime docHintUntil;
        static readonly int[] DocActIds = { 20, 21, 22, 23, -1, 30, -1, 24, 25, -1, 26 };

        Point ToDoc(Point c) { return new Point(c.X - view.X, c.Y - view.Y); }
        int DocTop { get { return S(12); } }
        int DocBottom { get { return S(76); } }   // espaço para a barra de ações não cobrir o fim da imagem

        void ClampView()
        {
            if (!docMode) return;
            int W = ClientSize.Width, H = ClientSize.Height, w = shot.Width, h = shot.Height;
            view.X = w <= W ? (W - w) / 2 : Math.Max(W - w, Math.Min(0, view.X));
            view.Y = h + DocTop + DocBottom <= H ? Math.Max(DocTop, (H - h) / 2) : Math.Max(H - DocBottom - h, Math.Min(DocTop, view.Y));
        }

        void ScrollBy(int dy)
        {
            view.Y += dy;
            ClampView(); LayoutBars(); Invalidate();
        }

        void StartPan(Point c) { panning = true; panStart = c; panView = view; Cursor = Cursors.SizeAll; }

        // A seleção (a imagem toda, na edição com rolagem) na parte visível da janela: as barras se apoiam nela.
        Rectangle SelOnScreen
        {
            get
            {
                if (!docMode) return sel;
                var r = new Rectangle(sel.X + view.X, sel.Y + view.Y, sel.Width, sel.Height);
                var vis = Rectangle.Intersect(r, new Rectangle(0, DocTop, ClientSize.Width, ClientSize.Height - DocTop - DocBottom + S(8)));
                return vis.Width > 0 && vis.Height > 0 ? vis : r;
            }
        }

        public OverlayForm(TrayApp app, Bitmap shot, Rectangle vs, bool pinMode) : this(app, () => shot, vs, pinMode, false) { }

        // doc: shot é uma imagem pronta para editar (print com rolagem) e vs o monitor onde a janela abre.
        public OverlayForm(TrayApp app, Bitmap shot, Rectangle vs, bool pinMode, bool doc) : this(app, () => shot, vs, pinMode, doc) { }

        // takeShot: entrega o print. Ele é pedido só depois de a janela (ainda invisível) ser criada,
        // então a captura da tela pode correr numa thread enquanto isso.
        public OverlayForm(TrayApp app, Func<Bitmap> takeShot, Rectangle vs, bool pinMode, bool doc)
        {
            this.app = app; this.vs = vs; this.pinMode = pinMode; docMode = doc;
            color = Color.FromArgb(TrayApp.S.ColorArgb);
            thick = TrayApp.S.Thickness;
            foreach (var part in (TrayApp.S.ToolSizes ?? "").Split(','))
            {
                var kv = part.Split(':');
                int k, v;
                if (kv.Length == 2 && int.TryParse(kv[0], out k) && int.TryParse(kv[1], out v) && Enum.IsDefined(typeof(Tool), k))
                    toolThick[(Tool)k] = Math.Max(MinThick, Math.Min(MaxThick, v));
            }

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Bounds = vs;
            TopMost = true;
            ShowInTaskbar = false;
            Text = "Capturo";
            Cursor = Cursors.Cross;
            KeyPreview = true;
            // buffer duplo próprio (abaixo): o do WinForms recria um buffer de tela inteira a cada redesenho
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);

            using (var g = CreateGraphics()) scale = Math.Max(1f, g.DpiX / 96f);
            fontUi = new Font("Segoe UI", 12 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            fontUiB = new Font("Segoe UI Semibold", 12 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            fontSmall = new Font("Consolas", 11 * scale, FontStyle.Regular, GraphicsUnit.Pixel);

            if (!docMode) try
            {
                List<IntPtr> hs;
                var rects = Native.VisibleWindowRects(Handle, out hs);
                for (int i = 0; i < rects.Count; i++)
                {
                    var c = rects[i]; c.Offset(-vs.X, -vs.Y);
                    c.Intersect(new Rectangle(0, 0, vs.Width, vs.Height));
                    if (c.Width >= 20 && c.Height >= 20) { windows.Add(c); windowHandles.Add(hs[i]); }
                }
            }
            catch { }

            try { shot = takeShot(); }
            catch { Dispose(); throw; }

            if (docMode)
            {
                sel = new Rectangle(0, 0, shot.Width, shot.Height); hasSel = true;
                Cursor = Cursors.Default;
                docHintUntil = DateTime.Now.AddSeconds(6);
            }
            else dim = Dimmed(shot);

            timer.Interval = 500;
            timer.Tick += (s, e) =>
            {
                if (flash != null && DateTime.Now > flashUntil) { flash = null; Invalidate(); }
                if (editingText != null) { caretOn = !caretOn; Invalidate(); }
                if (docMode && docHintUntil != DateTime.MinValue && DateTime.Now > docHintUntil) { docHintUntil = DateTime.MinValue; Invalidate(); }
            };
            timer.Start();
        }

        int S(float v) { return (int)Math.Round(v * scale); }

        // O print escurecido do fundo: o mesmo que pintar Color.FromArgb(120, 8, 8, 16) por cima,
        // feito em paralelo (o GDI+ levava ~20 ms na tela virtual de dois monitores).
        static unsafe Bitmap Dimmed(Bitmap shot)
        {
            int w = shot.Width, h = shot.Height;
            var dim = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            var rect = new Rectangle(0, 0, w, h);
            var s = shot.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                var d = dim.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    IntPtr s0 = s.Scan0, d0 = d.Scan0;
                    int ss = s.Stride, ds = d.Stride;
                    const uint A = 120, K = 255 - A;
                    System.Threading.Tasks.Parallel.For(0, h, y =>
                    {
                        uint* ps = (uint*)((byte*)s0 + (long)y * ss), pd = (uint*)((byte*)d0 + (long)y * ds);
                        for (int x = 0; x < w; x++)
                        {
                            uint c = ps[x];
                            uint b = ((c & 0xFF) * K + 16 * A + 127) / 255;
                            uint g = (((c >> 8) & 0xFF) * K + 8 * A + 127) / 255;
                            uint r = (((c >> 16) & 0xFF) * K + 8 * A + 127) / 255;
                            pd[x] = 0xFF000000u | (r << 16) | (g << 8) | b;
                        }
                    });
                }
                finally { dim.UnlockBits(d); }
            }
            finally { shot.UnlockBits(s); }
            return dim;
        }

        [DllImport("user32.dll")] static extern int GetUpdateRgn(IntPtr hWnd, IntPtr hRgn, bool erase);
        [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
        [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);

        // Região que o WM_PAINT atual precisa redesenhar. O ClipRectangle do WinForms é só o retângulo
        // que a envolve: as duas faixas da mira já cobrem a tela virtual inteira (3840x1080 a cada movimento).
        Region paintRegion;
        Rectangle[] paintRects;

        // Copia a imagem em r só nos retângulos que o WM_PAINT atual redesenha: com uma região de
        // várias faixas, o GDI+ percorreria o retângulo envolvente inteiro (a tela toda).
        void BlitImage(Graphics g, Bitmap img, Rectangle r)
        {
            if (paintRects == null) { g.DrawImage(img, r, r, GraphicsUnit.Pixel); return; }
            foreach (var pr in paintRects)
            {
                var x = Rectangle.Intersect(pr, r);
                if (x.Width > 0 && x.Height > 0) g.DrawImage(img, x, x, GraphicsUnit.Pixel);
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x02E0) return; // WM_DPICHANGED: a janela cobre todos os monitores, não redimensionar
            if (m.Msg == 0x000F)
            {
                IntPtr rgn = CreateRectRgn(0, 0, 0, 0);
                try
                {
                    if (GetUpdateRgn(Handle, rgn, false) == 3 /*COMPLEXREGION*/)
                    {
                        paintRegion = Region.FromHrgn(rgn);
                        using (var id = new Matrix())
                        {
                            var scans = paintRegion.GetRegionScans(id);
                            paintRects = new Rectangle[scans.Length];
                            for (int i = 0; i < scans.Length; i++) paintRects[i] = Rectangle.Round(scans[i]);
                        }
                    }
                }
                finally { DeleteObject(rgn); }
                try { base.WndProc(ref m); }
                finally { if (paintRegion != null) { paintRegion.Dispose(); paintRegion = null; } paintRects = null; }
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Bounds = vs;
            Native.ForceForeground(Handle);
            Activate();
            mouse = PointToClient(Cursor.Position);
            if (docMode) { view = new Point(0, int.MaxValue / 2); ClampView(); }   // começa no topo da imagem
            if (hasSel) LayoutBars();
            Invalidate();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Dispose();
            TrayApp.S.ColorArgb = color.ToArgb();
            TrayApp.S.Thickness = thick;
            var sizes = new List<string>();
            foreach (var kv in toolThick) sizes.Add((int)kv.Key + ":" + kv.Value);
            TrayApp.S.ToolSizes = string.Join(",", sizes.ToArray());
            TrayApp.S.Save();
            foreach (var a in annots) a.DisposeCache();
            shot.Dispose(); if (dim != null) dim.Dispose(); bufCtx.Dispose();
            fontUi.Dispose(); fontUiB.Dispose(); fontSmall.Dispose();
            if (shareMenu != null) shareMenu.Dispose();
            base.OnFormClosed(e);
        }

        Rectangle MonitorAt(Point clientPt)
        {
            var b = Screen.FromPoint(new Point(clientPt.X + vs.X, clientPt.Y + vs.Y)).Bounds;
            b.Offset(-vs.X, -vs.Y);
            return b;
        }

        static string TipName(int id)
        {
            switch (id)
            {
                case 1: return "Lápis"; case 2: return "Linha"; case 3: return "Seta"; case 4: return "Retângulo";
                case 5: return "Marcador"; case 6: return "Texto"; case 7: return "Cor e tamanho"; case 8: return "Desfazer";
                case 9: return "Pixelar"; case 10: return "Numeração";
                case 11: return "Elipse"; case 12: return "Destaque (escurece o resto)"; case 13: return "Mover e editar anotações";
                case 28: return "Gravar GIF da área"; case 29: return "Selecionar texto na imagem (OCR)"; case 30: return "Moldura com sombra";
                case 31: return "Gravar vídeo da área (com zoom e áudio)";
                case 32: return "Print com rolagem (rola e emenda a página)";
                case 20: return "Enviar para a nuvem"; case 21: return "Compartilhar"; case 22: return "Buscar semelhantes";
                case 23: return "Imprimir"; case 24: return "Copiar"; case 25: return "Salvar"; case 26: return "Fechar";
                case 27: return "Fixar na tela";
            }
            return "";
        }

        static string TipKey(int id)
        {
            switch (id)
            {
                case 1: return "P"; case 2: return "L"; case 3: return "A"; case 4: return "R"; case 5: return "M"; case 6: return "T";
                case 9: return "B"; case 10: return "N";
                case 11: return "E"; case 12: return "H"; case 13: return "V";
                case 28: return "Ctrl+R"; case 29: return "Ctrl+O"; case 30: return "F"; case 31: return "Ctrl+Shift+R"; case 32: return "Ctrl+L";
                case 7: return "C"; case 8: return "Ctrl+Z"; case 20: return "Ctrl+D"; case 22: return "Ctrl+G"; case 23: return "Ctrl+P";
                case 24: return "Ctrl+C"; case 25: return "Ctrl+S"; case 26: return "Esc"; case 27: return "Ctrl+T";
            }
            return null;
        }

        // ---------- layout das barras
        void LayoutBars()
        {
            btns.Clear();
            paletteOpen = paletteOpen && hasSel;
            if (!hasSel) return;
            float f = TrayApp.S.IconScale / 100f;
            int b = S(34 * f), pad = S(5 * f), gap = S(8), sep = S(9 * f);
            int W = ClientSize.Width, H = ClientSize.Height;

            // gravando (GIF ou vídeo): as ferramentas de desenho não entram na gravação, então somem
            bool rec = GifMode || VideoMode || ScrollMode;
            int[] toolIds = rec ? new int[0] : ToolIds;
            int[] actIds = rec ? new[] { ScrollMode ? 32 : VideoMode ? 31 : 28, -1, 26 } : docMode ? DocActIds : ActIds;
            var sel = SelOnScreen;   // na imagem com rolagem, a parte visível
            int vlen = 0; foreach (int id in toolIds) vlen += id < 0 ? sep : b;
            int vh = vlen + pad * 2, vw = b + pad * 2;
            int vx = sel.Right + gap;
            if (vx + vw > W) vx = sel.Left - gap - vw;
            if (vx < 0) vx = sel.Right - vw - gap;
            int vy = Math.Max(0, Math.Min(sel.Bottom - vh, H - vh));
            toolPanel = rec ? Rectangle.Empty : new Rectangle(vx, vy, vw, vh);
            int y = vy + pad;
            foreach (int id in toolIds)
            {
                int len = id < 0 ? sep : b;
                btns.Add(new Btn { Id = id, R = new Rectangle(vx + pad, y, b, len) });
                y += len;
            }

            int hlen = 0; foreach (int id in actIds) hlen += id < 0 ? sep : b;
            int hw = hlen + pad * 2, hh = b + pad * 2;
            int hy = sel.Bottom + gap;
            if (hy + hh > H) hy = sel.Top - gap - hh;
            if (hy < 0) hy = sel.Bottom - hh - gap;
            int hx = Math.Max(0, Math.Min(sel.Right - hw, W - hw));
            if (toolPanel.IntersectsWith(new Rectangle(hx, hy, hw, hh))) hx = Math.Max(0, vx - hw - gap);
            actPanel = new Rectangle(hx, hy, hw, hh);
            int x = hx + pad;
            foreach (int id in actIds)
            {
                int len = id < 0 ? sep : b;
                btns.Add(new Btn { Id = id, R = new Rectangle(x, hy + pad, len, b) });
                x += len;
            }
            if (paletteOpen) LayoutPalette();
        }

        void LayoutPalette()
        {
            var cb = btns.Find(btn => btn.Id == 7);
            if (cb == null) { paletteOpen = false; return; }
            int sw = S(24), g = S(8), cols = 5;
            int moreH = S(26), sizeH = S(50);
            int w = cols * sw + (cols + 1) * g, h = 2 * sw + 3 * g + moreH + sizeH;
            int x = toolPanel.Right + S(8);
            if (x + w > ClientSize.Width) x = toolPanel.Left - S(8) - w;
            int y = Math.Max(0, Math.Min(cb.R.Top - g, ClientSize.Height - h));
            paletteRect = new Rectangle(x, y, w, h);
            for (int i = 0; i < Art.Palette.Length; i++)
                swatchRects[i] = new Rectangle(x + g + (i % cols) * (sw + g), y + g + (i / cols) * (sw + g), sw, sw);
            moreRect = new Rectangle(x + g, y + 2 * sw + 3 * g - S(2), w - 2 * g, moreH - S(4));
            int sy = moreRect.Bottom + S(8);
            sizeLabel = new Rectangle(x + g, sy, w - 2 * g, S(18));
            sizeTrack = new Rectangle(x + g + S(4), sizeLabel.Bottom + S(2), w - 2 * g - S(8), S(18));
        }

        // ---------- tamanho (espessura) por ferramenta
        int ThickFor(Tool t)
        {
            int v;
            return toolThick.TryGetValue(t, out v) ? v : TrayApp.S.Thickness;
        }

        // tamanho mostrado no painel: o do objeto selecionado, senão o da ferramenta atual
        int ShownThick { get { return picked != null ? Math.Max(MinThick, Math.Min(MaxThick, (int)Math.Round(picked.Width))) : editingText != null ? (int)editingText.Width : thick; } }

        void SetThick(int v, bool snapshot)
        {
            v = Math.Max(MinThick, Math.Min(MaxThick, v));
            thick = v;
            Tool t = editingText != null ? Tool.Text : picked != null ? picked.Kind : tool;
            if (t != Tool.None) toolThick[t] = v;
            else TrayApp.S.Thickness = v;
            if (editingText != null) editingText.Width = v;
            else if (picked != null && picked.Width != v)
            {
                if (snapshot) Snapshot();
                picked.Width = v; picked.DisposeCache();
            }
            Invalidate();
        }

        int ThickAt(int x)
        {
            float f = (x - sizeTrack.Left) / (float)Math.Max(1, sizeTrack.Width);
            return MinThick + (int)Math.Round(Math.Max(0, Math.Min(1, f)) * (MaxThick - MinThick));
        }

        Btn HitBtn(Point p)
        {
            if (drag == Drag.NewSel) return null;
            foreach (var bt in btns) if (bt.Id > 0 && bt.R.Contains(p)) return bt;
            return null;
        }

        Point[] HandlePoints()
        {
            int cx = sel.X + sel.Width / 2, cy = sel.Y + sel.Height / 2;
            return new[] {
                new Point(sel.Left, sel.Top), new Point(cx, sel.Top), new Point(sel.Right, sel.Top), new Point(sel.Right, cy),
                new Point(sel.Right, sel.Bottom), new Point(cx, sel.Bottom), new Point(sel.Left, sel.Bottom), new Point(sel.Left, cy) };
        }

        bool ShowEdgeHandles { get { return sel.Width > S(40) && sel.Height > S(40); } }

        int HitHandle(Point p)
        {
            if (docMode) return -1;   // a imagem com rolagem não é recortada
            if (!hasSel) return -1;
            var pts = HandlePoints();
            int tol = S(7);
            for (int i = 0; i < 8; i++)
            {
                if (i % 2 == 1 && !ShowEdgeHandles) continue;
                if (Math.Abs(p.X - pts[i].X) <= tol && Math.Abs(p.Y - pts[i].Y) <= tol) return i;
            }
            // bordas inteiras também redimensionam
            if (tool == Tool.None || !sel.Contains(p))
            {
                int t = S(4);
                bool inX = p.X >= sel.Left - t && p.X <= sel.Right + t, inY = p.Y >= sel.Top - t && p.Y <= sel.Bottom + t;
                if (inY && Math.Abs(p.X - sel.Left) <= t) return 7;
                if (inY && Math.Abs(p.X - sel.Right) <= t) return 3;
                if (inX && Math.Abs(p.Y - sel.Top) <= t) return 1;
                if (inX && Math.Abs(p.Y - sel.Bottom) <= t) return 5;
            }
            return -1;
        }

        static readonly Cursor[] HandleCursors = {
            Cursors.SizeNWSE, Cursors.SizeNS, Cursors.SizeNESW, Cursors.SizeWE,
            Cursors.SizeNWSE, Cursors.SizeNS, Cursors.SizeNESW, Cursors.SizeWE };

        Rectangle ClampRect(Rectangle r)
        {
            r.Intersect(new Rectangle(Point.Empty, ClientSize));
            return r;
        }

        static Rectangle FromPoints(Point a, Point b)
        {
            return Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        }

        Rectangle HitWindow(Point p)
        {
            foreach (var r in windows) if (r.Contains(p)) return r;
            return Rectangle.Empty;
        }

        void Flash(string text) { flash = text; flashUntil = DateTime.Now.AddSeconds(1.2); Invalidate(); }

        // ---------- lupa
        bool ShowLoupe { get { return !RecMode && !docMode && (!hasSel || drag == Drag.NewSel || drag == Drag.Resize); } }

        void LoupeLayout(Point p, out Rectangle box, out Rectangle info)
        {
            int cell = S(8), n = 15, size = cell * n, infoH = S(42), off = S(22);
            var mon = MonitorAt(p);
            int x = p.X + off, y = p.Y + off;
            if (x + size > mon.Right) x = p.X - off - size;
            if (y + size + infoH > mon.Bottom) y = p.Y - off - size - infoH;
            box = new Rectangle(x, y, size, size);
            info = new Rectangle(x, y + size, size, infoH);
        }

        Rectangle LoupeBounds(Point p)
        {
            Rectangle box, info;
            LoupeLayout(p, out box, out info);
            var r = Rectangle.Union(box, info);
            r.Inflate(S(10), S(10));
            return r;
        }

        void DrawLoupe(Graphics g, Point p)
        {
            Rectangle box, info;
            LoupeLayout(p, out box, out info);
            int cell = S(8), n = 15, half = n / 2;
            var all = Rectangle.Union(box, info);
            float rad = S(8);

            Art.Shadow(g, all, rad, scale);
            using (var clip = Art.RoundRect(all, rad))
            {
                var st = g.Save();
                g.SetClip(clip, CombineMode.Intersect);
                g.FillRectangle(Brushes.Black, box);
                var src = new Rectangle(p.X - half, p.Y - half, n, n);
                var vis = Rectangle.Intersect(src, new Rectangle(0, 0, shot.Width, shot.Height));
                if (vis.Width > 0 && vis.Height > 0)
                {
                    var dst = new Rectangle(box.X + (vis.X - src.X) * cell, box.Y + (vis.Y - src.Y) * cell, vis.Width * cell, vis.Height * cell);
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(shot, dst, vis, GraphicsUnit.Pixel);
                    g.PixelOffsetMode = PixelOffsetMode.Default;
                }
                // linhas-guia
                using (var gb = new SolidBrush(Color.FromArgb(55, Art.Accent.R, Art.Accent.G, Art.Accent.B)))
                {
                    g.FillRectangle(gb, box.X, box.Y + half * cell, box.Width, cell);
                    g.FillRectangle(gb, box.X + half * cell, box.Y, cell, box.Height);
                }
                using (var cp = new Pen(Color.White, Math.Max(1, scale)))
                    g.DrawRectangle(cp, box.X + half * cell, box.Y + half * cell, cell - 1, cell - 1);

                using (var ib = new SolidBrush(Art.Pill)) g.FillRectangle(ib, info);
                Color px = Color.Black;
                if (p.X >= 0 && p.Y >= 0 && p.X < shot.Width && p.Y < shot.Height) px = shot.GetPixel(p.X, p.Y);
                string coords = (p.X + vs.X) + ", " + (p.Y + vs.Y);
                string hex = "#" + px.R.ToString("X2") + px.G.ToString("X2") + px.B.ToString("X2");
                int lh = (int)fontSmall.GetHeight(g);
                int ty = info.Y + (info.Height - lh * 2) / 2;
                g.DrawString(coords, fontSmall, Brushes.White, info.X + S(10), ty);
                int sw = lh - S(3);
                using (var pb = new SolidBrush(px)) g.FillRectangle(pb, info.X + S(10), ty + lh + S(2), sw, sw);
                g.DrawRectangle(Pens.Gray, info.X + S(10), ty + lh + S(2), sw, sw);
                using (var gray = new SolidBrush(Color.FromArgb(200, 200, 205)))
                    g.DrawString(hex, fontSmall, gray, info.X + S(14) + sw, ty + lh);
                g.Restore(st);
            }
            using (var bp = Art.RoundRect(all, rad)) using (var pen = new Pen(Color.FromArgb(90, 255, 255, 255), 1)) g.DrawPath(pen, bp);
        }

        // ---------- mouse
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            // c: posição na janela (botões, paleta); p: posição na imagem (seleção, anotações).
            // Só diferem na edição de uma imagem maior que a tela (print com rolagem), que rola.
            Point c = e.Location, p = ToDoc(c);

            if (paletteOpen)
            {
                if (paletteRect.Contains(c))
                {
                    for (int i = 0; i < swatchRects.Length; i++)
                        if (swatchRects[i].Contains(c)) { SetColor(Art.Palette[i]); paletteOpen = false; Invalidate(); return; }
                    if (moreRect.Contains(c)) { paletteOpen = false; PickColorDialog(); return; }
                    var hitTrack = sizeTrack; hitTrack.Inflate(S(6), S(6));
                    if (hitTrack.Contains(c)) { sizeDrag = true; SetThick(ThickAt(c.X), true); }
                    return;
                }
                paletteOpen = false; Invalidate();
                if (HitBtn(c) == null || HitBtn(c).Id == 7) return;
            }

            // modo texto: dentro da área, o mouse seleciona o texto da imagem
            if (textMode && hasSel && sel.Contains(p) && HitBtn(c) == null && HitHandle(p) < 0)
            {
                if (e.Button == MouseButtons.Right) { ShowTextMenu(c); return; }
                if (e.Button == MouseButtons.Left)
                {
                    liveDrag = Live.MouseDown(p, (ModifierKeys & Keys.Shift) != 0) && live.Dragging;
                    return;
                }
            }

            if (e.Button == MouseButtons.Right)
            {
                FinishText();
                if (docMode) { if (picked != null) { picked = null; Invalidate(); } return; }
                if (hasSel && annots.Count == 0) { hasSel = false; LayoutBars(); Invalidate(); }
                else if (!hasSel) Close();
                return;
            }
            if (e.Button == MouseButtons.Middle && docMode) { StartPan(c); return; }
            if (e.Button != MouseButtons.Left) return;

            var bt = HitBtn(c);
            if (bt != null) { FinishText(); OnButton(bt); return; }

            bool clickedOnEditing = editingText != null && tool == Tool.Text && sel.Contains(p);
            FinishText();
            if (clickedOnEditing) { Invalidate(); }

            if (!hasSel)
            {
                drag = Drag.NewSel; dragStart = p; sel = new Rectangle(p, Size.Empty); hasSel = true;
                return;
            }
            int h = HitHandle(p);
            if (h >= 0) { drag = Drag.Resize; handle = h; dragStart = p; selStart = sel; return; }

            if (tool != Tool.None && sel.Contains(p))
            {
                var a = new Annot { Kind = tool, Color = color, Width = thick, A = p, B = p };
                if (tool == Tool.Blur) a.Src = shot;
                if (tool == Tool.Step) a.Text = (annots.FindAll(x => x.Kind == Tool.Step).Count + 1).ToString();
                picked = null;
                if (tool == Tool.Text)
                {
                    Snapshot(); textIsNew = true;
                    editingText = a; caretOn = true; annots.Add(a);
                    Invalidate();
                    return;
                }
                a.Pts.Add(p);
                cur = a; drag = Drag.Draw;
                return;
            }
            if (tool == Tool.None && sel.Contains(p))
            {
                var hit = HitAnnot(p);
                if (hit != null)
                {
                    // pega a anotação para mover; o estado só vai para o desfazer se ela realmente andar
                    picked = hit; pickedOrig = hit.Clone(); pendingSnap = CloneList(annots);
                    drag = Drag.MoveAnnot; dragStart = p;
                    Invalidate();
                    return;
                }
                if (picked != null) { picked = null; Invalidate(); }
                if (docMode) { StartPan(c); return; }   // arrastar a imagem rola
                drag = Drag.Move; dragStart = p; selStart = sel;
                return;
            }
            if (docMode) { StartPan(c); return; }

            if (annots.Count == 0)
            {
                drag = Drag.NewSel; dragStart = p; sel = new Rectangle(p, Size.Empty);
                LayoutBars(); Invalidate();
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (textMode) return;   // no modo texto o duplo clique seleciona a palavra
            var p = ToDoc(e.Location);
            if (e.Button == MouseButtons.Left && hasSel && tool == Tool.None && sel.Contains(p) && HitBtn(e.Location) == null)
            {
                var hit = HitAnnot(p);
                if (hit != null && hit.Kind == Tool.Text)
                {
                    // duplo clique num texto: volta a editar
                    drag = Drag.None; pendingSnap = null; panning = false;
                    Snapshot(); textIsNew = false;
                    editingText = hit; caretOn = true; picked = null;
                    Invalidate();
                    return;
                }
                if (hit != null || docMode) return;   // na imagem com rolagem, duplo clique não copia (é fácil de esbarrar rolando)
                DoAction(DefaultAction);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Point c = e.Location, p = ToDoc(c);
            Point old = mouse;
            mouse = c;
            bool shift = (ModifierKeys & Keys.Shift) != 0;
            if (liveDrag) { live.MouseMove(p); return; }
            if (panning)
            {
                view = new Point(panView.X + c.X - panStart.X, panView.Y + c.Y - panStart.Y);
                ClampView(); LayoutBars(); Invalidate();
                return;
            }

            switch (drag)
            {
                case Drag.NewSel:
                    sel = ClampRect(FromPoints(dragStart, p));
                    break;
                case Drag.Move:
                    {
                        var r = selStart;
                        r.Offset(p.X - dragStart.X, p.Y - dragStart.Y);
                        r.X = Math.Max(0, Math.Min(r.X, ClientSize.Width - r.Width));
                        r.Y = Math.Max(0, Math.Min(r.Y, ClientSize.Height - r.Height));
                        sel = r; LayoutBars();
                        break;
                    }
                case Drag.Resize:
                    {
                        int dx = p.X - dragStart.X, dy = p.Y - dragStart.Y;
                        int l = selStart.Left, t = selStart.Top, r = selStart.Right, b = selStart.Bottom;
                        if (handle == 0 || handle == 6 || handle == 7) l += dx;
                        if (handle == 2 || handle == 3 || handle == 4) r += dx;
                        if (handle == 0 || handle == 1 || handle == 2) t += dy;
                        if (handle == 4 || handle == 5 || handle == 6) b += dy;
                        sel = ClampRect(FromPoints(new Point(l, t), new Point(r, b)));
                        LayoutBars();
                        break;
                    }
                case Drag.MoveAnnot:
                    if (p == dragStart) return;
                    if (pendingSnap != null) { PushUndo(pendingSnap); pendingSnap = null; }
                    picked.MoveFrom(pickedOrig, p.X - dragStart.X, p.Y - dragStart.Y);
                    break;
                case Drag.Draw:
                    if (cur.Kind == Tool.Pen || cur.Kind == Tool.Marker)
                    {
                        var last = cur.Pts[cur.Pts.Count - 1];
                        if (Math.Abs(last.X - p.X) + Math.Abs(last.Y - p.Y) >= 1) cur.Pts.Add(p);
                    }
                    else if (cur.Kind == Tool.Step) cur.A = cur.B = p; // o número acompanha o mouse até soltar
                    else
                    {
                        PointF q = p;
                        if (shift) q = Constrain(cur.A, p, cur.Kind == Tool.Rect || cur.Kind == Tool.Blur || cur.Kind == Tool.Ellipse || cur.Kind == Tool.Spot);
                        cur.B = q;
                    }
                    break;
                case Drag.None:
                    {
                        var bt = HitBtn(c);
                        if (sizeDrag) { if (ThickAt(c.X) != thick) SetThick(ThickAt(c.X), false); Cursor = Cursors.Hand; return; }
                        int sw = -1; bool more = false, onSize = false;
                        if (paletteOpen)
                        {
                            for (int i = 0; i < swatchRects.Length; i++) if (swatchRects[i].Contains(c)) sw = i;
                            more = moreRect.Contains(c);
                            var ht = sizeTrack; ht.Inflate(S(6), S(6)); onSize = ht.Contains(c);
                        }
                        bool changed = bt != hover || sw != hoverSwatch || more != hoverMore || onSize != hoverSize;
                        hover = bt; hoverSwatch = sw; hoverMore = more; hoverSize = onSize;
                        int h = HitHandle(p);
                        if (bt != null || sw >= 0 || more || onSize) Cursor = Cursors.Hand;
                        else if (paletteOpen && paletteRect.Contains(c)) Cursor = Cursors.Default;
                        else if (h >= 0) Cursor = HandleCursors[h];
                        else if (hasSel && sel.Contains(p) && textMode)
                            Cursor = Live.Over(p) ? Cursors.IBeam : Cursors.Default;
                        else if (hasSel && sel.Contains(p))
                            Cursor = tool == Tool.None ? (HitAnnot(p) != null ? Cursors.Hand : Cursors.SizeAll) : (tool == Tool.Text ? Cursors.IBeam : Cursors.Cross);
                        else Cursor = docMode ? Cursors.SizeAll : Cursors.Cross;

                        if (!hasSel)
                        {
                            // redesenha só o que muda: mira, lupa, a janela destacada e o aviso do monitor
                            var w = HitWindow(c);
                            if (w != hoverWin) { Invalidate(HoverArea(hoverWin)); hoverWin = w; Invalidate(HoverArea(w)); }
                            InvalidateCrosshair(old);
                            InvalidateCrosshair(c);
                            Rectangle mo = MonitorAt(old), mc = MonitorAt(c);
                            if (mo != mc) { InvalidateHint(mo); InvalidateHint(mc); }
                            return;
                        }
                        if (!changed) return;
                        break;
                    }
            }
            Invalidate();
        }

        void InvalidateCrosshair(Point p)
        {
            Invalidate(new Rectangle(0, p.Y - 2, ClientSize.Width, 5));
            Invalidate(new Rectangle(p.X - 2, 0, 5, ClientSize.Height));
            Invalidate(LoupeBounds(p));
        }

        // Faixas do aviso em pílula (no alto e embaixo do monitor, com a sombra).
        void InvalidateHint(Rectangle mon)
        {
            int h = S(36), m = S(12);
            Invalidate(new Rectangle(mon.X, mon.Y + S(24) - m, mon.Width, h + 2 * m));
            Invalidate(new Rectangle(mon.X, mon.Bottom - S(96) - h - m, mon.Width, h + 2 * m));
        }

        // Área da janela destacada sob o mouse, incluindo a pílula com o nome e as medidas.
        Rectangle HoverArea(Rectangle r)
        {
            if (r.Width <= 0 || r.Height <= 0) return Rectangle.Empty;
            using (var g = CreateGraphics())
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                var a = Rectangle.Union(r, PillRect(g, HoverText(r), HoverPillAt(r), fontUiB));
                a.Inflate(S(4), S(4));
                return a;
            }
        }

        string HoverText(Rectangle r)
        {
            return RecMode ? TargetLabel(r) + "  ·  " + r.Width + " × " + r.Height : r.Width + " × " + r.Height;
        }

        Point HoverPillAt(Rectangle r)
        {
            if (!RecMode) return new Point(r.X + S(8), r.Y + S(8));
            // gravação: acima da borda quando cabe; senão, por dentro
            int pillH = fontUiB.Height + S(8);
            return new Point(r.X + S(6), r.Y - pillH - S(6) >= MonitorAt(r.Location).Y ? r.Y - pillH - S(6) : r.Y + S(8));
        }

        static PointF Constrain(PointF a, PointF b, bool square)
        {
            float dx = b.X - a.X, dy = b.Y - a.Y;
            if (square)
            {
                float m = Math.Max(Math.Abs(dx), Math.Abs(dy));
                return new PointF(a.X + Math.Sign(dx) * m, a.Y + Math.Sign(dy) * m);
            }
            double ang = Math.Atan2(dy, dx), len = Math.Sqrt(dx * dx + dy * dy);
            double snap = Math.Round(ang / (Math.PI / 4)) * (Math.PI / 4);
            return new PointF(a.X + (float)(Math.Cos(snap) * len), a.Y + (float)(Math.Sin(snap) * len));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (panning) { panning = false; return; }
            if (sizeDrag) { sizeDrag = false; Invalidate(); return; }
            if (liveDrag) { liveDrag = false; live.MouseUp(); return; }
            if (drag == Drag.NewSel && (sel.Width < 3 || sel.Height < 3))
            {
                // clique sem arrastar: seleciona a janela (ou o monitor) sob o cursor
                var w = HitWindow(e.Location);
                if (w.Width >= 3 && w.Height >= 3)
                {
                    sel = w;
                    int wi = windows.IndexOf(w);
                    selWindow = wi >= 0 && wi < windowHandles.Count ? windowHandles[wi] : IntPtr.Zero;
                    selWindowRect = w;
                    if (RecMode)
                    {
                        // como no Recordly: clicar numa janela (ou na área de trabalho) já escolhe a fonte e grava
                        drag = Drag.None;
                        DoAction(DefaultAction);
                        if (!IsDisposed) { LayoutBars(); Invalidate(); }   // não gravou (área pequena demais)
                        return;
                    }
                }
                else hasSel = false;
            }
            if (drag == Drag.Draw && cur != null)
            {
                bool empty = (cur.Kind == Tool.Blur || cur.Kind == Tool.Spot) && (Math.Abs(cur.B.X - cur.A.X) < 3 || Math.Abs(cur.B.Y - cur.A.Y) < 3);
                if (!empty) { Snapshot(); annots.Add(cur); }
                cur = null;
            }
            drag = Drag.None;
            pendingSnap = null;
            if (textMode && hasSel && sel != liveSel && sel.Width >= 8 && sel.Height >= 8) StartLive();   // a área mudou: lê de novo
            LayoutBars();
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            // imagem com rolagem: a roda rola (Ctrl + roda muda o tamanho)
            if (docMode && (ModifierKeys & Keys.Control) == 0) { ScrollBy(e.Delta > 0 ? S(110) : -S(110)); return; }
            SetThick(ShownThick + (e.Delta > 0 ? 1 : -1), true);
            Flash("Tamanho  " + thick);
        }

        // ---------- teclado
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys k = keyData & Keys.KeyCode;
            if (docMode && (k == Keys.Up || k == Keys.Down) && editingText == null)
            {
                ScrollBy((k == Keys.Up ? 1 : -1) * S((keyData & Keys.Control) != 0 ? 300 : 60));
                return true;
            }
            if (k == Keys.Left || k == Keys.Right || k == Keys.Up || k == Keys.Down)
            {
                if (docMode) return true;
                int step = (keyData & Keys.Control) != 0 ? 10 : 1;
                int dx = k == Keys.Left ? -step : k == Keys.Right ? step : 0;
                int dy = k == Keys.Up ? -step : k == Keys.Down ? step : 0;
                if (!hasSel)
                {
                    // move o cursor pixel a pixel (útil com a lupa)
                    Cursor.Position = new Point(Cursor.Position.X + dx, Cursor.Position.Y + dy);
                    return true;
                }
                if (editingText != null) return true;
                if ((keyData & Keys.Shift) != 0) sel = ClampRect(new Rectangle(sel.X, sel.Y, Math.Max(1, sel.Width + dx), Math.Max(1, sel.Height + dy)));
                else if (annots.Count == 0)
                {
                    var r = sel; r.Offset(dx, dy);
                    r.X = Math.Max(0, Math.Min(r.X, ClientSize.Width - r.Width));
                    r.Y = Math.Max(0, Math.Min(r.Y, ClientSize.Height - r.Height));
                    sel = r;
                }
                LayoutBars(); Invalidate();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape)
            {
                if (paletteOpen) { paletteOpen = false; Invalidate(); }
                else if (editingText != null) { FinishText(); Invalidate(); }
                else if (picked != null) { picked = null; Invalidate(); }
                else if (textMode && live != null && live.HasSelection) live.ClearSelection();
                else Close();
                return;
            }
            if (e.Control)
            {
                e.SuppressKeyPress = true;
                switch (e.KeyCode)
                {
                    // no modo OCR o Ctrl+C só copia o texto selecionado (não copia a imagem nem fecha a captura)
                    case Keys.C: if (textMode) CopySelectedLiveText(); else if (hasSel) DoAction(24); else CopyPixelColor(); return;
                    case Keys.S: if (hasSel) DoAction(25); return;
                    case Keys.D: if (hasSel) DoAction(20); return;
                    case Keys.G: if (hasSel) DoAction(22); return;
                    case Keys.P: if (hasSel) DoAction(23); return;
                    case Keys.T: if (hasSel) DoAction(27); return;
                    case Keys.R: if (hasSel) DoAction(e.Shift ? 31 : 28); return;
                    case Keys.O: if (hasSel) DoAction(29); return;
                    case Keys.L: if (hasSel) DoAction(32); return;
                    case Keys.Z: Undo(); return;
                    case Keys.Y: Redo(); return;
                    case Keys.A:
                        if (docMode) return;
                        if (textMode) { Live.SelectAll(); return; }
                        FinishText(); sel = new Rectangle(Point.Empty, ClientSize); hasSel = true; LayoutBars(); Invalidate(); return;
                }
                e.SuppressKeyPress = false;
                return;
            }
            if (editingText != null) return; // digitação vai para OnKeyPress

            if (docMode)
            {
                int page = Math.Max(S(100), ClientSize.Height - DocTop - DocBottom - S(60));
                int dy = e.KeyCode == Keys.PageDown || e.KeyCode == Keys.Space ? -page : e.KeyCode == Keys.PageUp ? page
                    : e.KeyCode == Keys.Home ? int.MaxValue / 4 : e.KeyCode == Keys.End ? int.MinValue / 4 : 0;
                if (dy != 0) { ScrollBy(dy); e.SuppressKeyPress = true; return; }
            }

            switch (e.KeyCode)
            {
                case Keys.Enter: if (textMode) CopyLiveText(); else if (hasSel) DoAction(DefaultAction); break;
                case Keys.Delete:
                case Keys.Back:
                    if (picked == null) return;
                    Snapshot(); annots.Remove(picked); picked.DisposeCache(); picked = null; Invalidate();
                    break;
                case Keys.P: SetTool(Tool.Pen); break;
                case Keys.L: SetTool(Tool.Line); break;
                case Keys.A: SetTool(Tool.Arrow); break;
                case Keys.R: SetTool(Tool.Rect); break;
                case Keys.M: SetTool(Tool.Marker); break;
                case Keys.T: SetTool(Tool.Text); break;
                case Keys.B: SetTool(Tool.Blur); break;
                case Keys.N: SetTool(Tool.Step); break;
                case Keys.E: SetTool(Tool.Ellipse); break;
                case Keys.H: SetTool(Tool.Spot); break;
                case Keys.V: if (hasSel) { FinishText(); tool = Tool.None; Invalidate(); } break;
                case Keys.F: if (hasSel) ToggleFrame(); break;
                case Keys.C: if (hasSel) { paletteOpen = !paletteOpen; LayoutBars(); Invalidate(); } else CopyPixelColor(); break;
                default: return;
            }
            e.SuppressKeyPress = true;
        }

        void CopyPixelColor()
        {
            var p = mouse;
            if (p.X < 0 || p.Y < 0 || p.X >= shot.Width || p.Y >= shot.Height) return;
            var c = shot.GetPixel(p.X, p.Y);
            string hex = "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
            try { Clipboard.SetText(hex); } catch { }
            app.Notify("Cor copiada", hex, null);
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);
            if (editingText == null) return;
            if (e.KeyChar == '\b') { if (editingText.Text.Length > 0) editingText.Text = editingText.Text.Substring(0, editingText.Text.Length - 1); }
            else if (e.KeyChar == '\r') editingText.Text += "\n";
            else if (e.KeyChar >= ' ') editingText.Text += e.KeyChar;
            caretOn = true;
            e.Handled = true;
            Invalidate();
        }

        void FinishText()
        {
            if (editingText == null) return;
            if (editingText.Text.Trim().Length == 0)
            {
                annots.Remove(editingText);
                // texto novo que ficou vazio: descarta também o passo de desfazer criado para ele
                if (textIsNew && undoStack.Count > 0) undoStack.RemoveAt(undoStack.Count - 1);
            }
            editingText = null;
        }

        static List<Annot> CloneList(List<Annot> src)
        {
            var l = new List<Annot>(src.Count);
            foreach (var a in src) l.Add(a.Clone());
            return l;
        }

        void Snapshot() { PushUndo(CloneList(annots)); }

        void PushUndo(List<Annot> state)
        {
            undoStack.Add(state);
            if (undoStack.Count > 200) undoStack.RemoveAt(0);
            redoStack.Clear();
        }

        void Restore(List<Annot> state)
        {
            foreach (var a in annots) a.DisposeCache();
            annots.Clear();
            annots.AddRange(state);
            picked = null;
            Invalidate();
        }

        void Undo()
        {
            FinishText();
            if (undoStack.Count == 0) { Invalidate(); return; }
            redoStack.Add(CloneList(annots));
            var st = undoStack[undoStack.Count - 1];
            undoStack.RemoveAt(undoStack.Count - 1);
            Restore(st);
        }

        void Redo()
        {
            FinishText();
            if (redoStack.Count == 0) return;
            undoStack.Add(CloneList(annots));
            var st = redoStack[redoStack.Count - 1];
            redoStack.RemoveAt(redoStack.Count - 1);
            Restore(st);
        }

        Annot HitAnnot(Point p)
        {
            float tol = S(5);
            for (int i = annots.Count - 1; i >= 0; i--)
                if (annots[i] != editingText && annots[i].HitTest(p, tol)) return annots[i];
            return null;
        }

        int DefaultAction { get { return ScrollMode ? 32 : VideoMode ? 31 : GifMode ? 28 : (pinMode ? 27 : 24); } }

        // ---------- texto vivo: selecionar o texto direto sobre a captura
        LiveText live;
        bool textMode, liveDrag;
        Rectangle liveSel;
        string liveMsg; DateTime liveMsgUntil;

        LiveText Live
        {
            get
            {
                if (live == null) { live = new LiveText(this); live.Changed += () => Invalidate(); }
                return live;
            }
        }

        void ToggleTextMode()
        {
            if (textMode) { ExitTextMode(); return; }
            if (!hasSel || sel.Width < 8 || sel.Height < 8) { Flash("Selecione uma área com texto"); return; }
            FinishText();
            picked = null;
            tool = Tool.None;
            paletteOpen = false;
            textMode = true;
            StartLive();
            Invalidate();
        }

        void StartLive()
        {
            liveSel = sel;
            var crop = new Bitmap(sel.Width, sel.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(crop)) g.DrawImage(shot, new Rectangle(0, 0, sel.Width, sel.Height), sel, GraphicsUnit.Pixel);
            Live.Origin = sel.Location;
            Live.Scale = 1;
            Live.Start(crop);
        }

        void ExitTextMode()
        {
            textMode = false; liveDrag = false;
            if (live != null) live.Reset();
            Invalidate();
        }

        void LiveFlash(string msg) { liveMsg = msg; liveMsgUntil = DateTime.Now.AddSeconds(1.6); Invalidate(); }

        void CopyLiveText()
        {
            if (live == null || !live.Ready)
            {
                LiveFlash(live != null && live.Status == LiveText.State.Reading ? "Ainda lendo o texto..." : "Nenhum texto para copiar");
                return;
            }
            string t = live.HasSelection ? live.SelectedText : live.AllText;
            try { Clipboard.SetText(t); }
            catch { LiveFlash("Não foi possível copiar"); return; }
            TrayApp.S.LastArea = ScreenSel;
            Close();
            string preview = System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ").Trim();
            app.Notify("Texto copiado", preview.Length > 120 ? preview.Substring(0, 117) + "..." : preview, null);
        }

        void CopySelectedLiveText()
        {
            if (live == null || !live.Ready)
            {
                LiveFlash(live != null && live.Status == LiveText.State.Reading ? "Ainda lendo o texto..." : "Nenhum texto para copiar");
                return;
            }
            if (!live.HasSelection) { LiveFlash("Selecione um texto para copiar"); return; }
            try { Clipboard.SetText(live.SelectedText); }
            catch { LiveFlash("Não foi possível copiar"); return; }
            LiveFlash("Texto copiado");
        }

        void ShowTextMenu(Point p)
        {
            var m = new ContextMenuStrip();
            bool ready = Live.Ready, has = Live.HasSelection;
            Func<string> pick = () => System.Text.RegularExpressions.Regex.Replace(has ? live.SelectedText : live.AllText, @"\s+", " ").Trim();
            m.Items.Add(new ToolStripMenuItem("Copiar o texto selecionado", null, (s, e) => CopySelectedLiveText()) { Enabled = has, ShortcutKeyDisplayString = "Ctrl+C" });
            m.Items.Add(new ToolStripMenuItem("Copiar todo o texto", null, (s, e) => { live.ClearSelection(); CopyLiveText(); }) { Enabled = ready });
            m.Items.Add(new ToolStripMenuItem("Selecionar tudo", null, (s, e) => live.SelectAll()) { Enabled = ready, ShortcutKeyDisplayString = "Ctrl+A" });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem("Pesquisar no Google", null, (s, e) =>
            {
                string q = pick(); Close();
                try { Process.Start("https://www.google.com/search?q=" + Uri.EscapeDataString(q.Length > 300 ? q.Substring(0, 300) : q)); } catch { }
            }) { Enabled = ready });
            m.Items.Add(new ToolStripMenuItem("Traduzir", null, (s, e) =>
            {
                string q = pick(); Close();
                try { Process.Start("https://translate.google.com/?sl=auto&tl=pt&op=translate&text=" + Uri.EscapeDataString(q.Length > 4000 ? q.Substring(0, 4000) : q)); } catch { }
            }) { Enabled = ready });
            m.Items.Add(new ToolStripMenuItem("Abrir na janela de texto (editar, salvar...)", null, (s, e) => { var bmp = RenderFinal(); TrayApp.S.LastArea = ScreenSel; Close(); app.CopyText(bmp); }));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem("Voltar às ferramentas de desenho", null, (s, e) => ExitTextMode()) { ShortcutKeyDisplayString = "Ctrl+O" });
            m.Show(this, p);
        }

        string LiveStatus()
        {
            if (liveMsg != null && DateTime.Now < liveMsgUntil) return liveMsg;
            switch (Live.Status)
            {
                case LiveText.State.Reading: return "Lendo o texto...";
                case LiveText.State.Empty: return "Nenhum texto encontrado nesta área";
                case LiveText.State.Error: return "Não foi possível ler: " + live.Error;
                case LiveText.State.Ready:
                    return live.HasSelection ? "Ctrl+C copia a seleção · botão direito: mais opções"
                        : "Arraste sobre o texto para selecionar · Ctrl+A tudo · Ctrl+C copia";
            }
            return "";
        }

        void ToggleFrame()
        {
            TrayApp.S.Frame = !TrayApp.S.Frame;
            Flash(TrayApp.S.Frame ? "Moldura com sombra: ligada" : "Moldura com sombra: desligada");
        }

        // Abre a captura com uma área já selecionada (repetir a última área).
        public void Preselect(Rectangle screenRect)
        {
            var r = screenRect; r.Offset(-vs.X, -vs.Y);
            r.Intersect(new Rectangle(0, 0, shot.Width, shot.Height));
            if (r.Width < 3 || r.Height < 3) return;
            sel = r; hasSel = true;
            LayoutBars();
        }

        void SetTool(Tool t)
        {
            if (!hasSel) return;
            if (textMode) ExitTextMode();
            FinishText();
            picked = null;
            tool = tool == t ? Tool.None : t;
            if (tool != Tool.None) thick = ThickFor(tool);   // cada ferramenta volta com o próprio tamanho
            Invalidate();
        }

        void SetColor(Color c)
        {
            color = c;
            if (editingText != null) editingText.Color = c;
            else if (picked != null) { Snapshot(); picked.Color = c; }
            Invalidate();
        }

        void PickColorDialog()
        {
            using (var dlg = new ColorDialog())
            {
                dlg.Color = color; dlg.FullOpen = true; dlg.AnyColor = true;
                if (dlg.ShowDialog(this) == DialogResult.OK) SetColor(dlg.Color);
            }
            Native.ForceForeground(Handle);
            Invalidate();
        }

        void OnButton(Btn bt)
        {
            if (Annot.IsTool(bt.Id)) SetTool((Tool)bt.Id);
            else if (bt.Id == 13) { FinishText(); tool = Tool.None; Invalidate(); }
            else if (bt.Id == 30) ToggleFrame();
            else if (bt.Id == 7) { paletteOpen = !paletteOpen; LayoutBars(); Invalidate(); }
            else if (bt.Id == 8) Undo();
            else DoAction(bt.Id);
        }

        Bitmap RenderFinal()
        {
            FinishText();
            var b = new Bitmap(sel.Width, sel.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(b))
            {
                g.DrawImage(shot, new Rectangle(0, 0, sel.Width, sel.Height), sel, GraphicsUnit.Pixel);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.TranslateTransform(-sel.X, -sel.Y);
                DrawAnnots(g, sel, false);
            }
            return b;
        }

        // Imagem final das ações: aplica a moldura (se ligada), guarda no histórico e lembra a área.
        Bitmap Final()
        {
            var b = RenderFinal();
            if (TrayApp.S.Frame) { var f = Art.Framed(b); b.Dispose(); b = f; }
            CaptureHistory.Add(b);
            if (!docMode) TrayApp.S.LastArea = ScreenSel;
            return b;
        }

        Rectangle ScreenSel { get { return new Rectangle(sel.X + vs.X, sel.Y + vs.Y, sel.Width, sel.Height); } }

        // Pixelados primeiro, depois o escurecimento dos destaques, depois o resto (setas ficam por cima).
        void DrawAnnots(Graphics g, Rectangle area, bool live)
        {
            var all = new List<Annot>(annots);
            if (cur != null) all.Add(cur);
            foreach (var a in all) if (a.Kind == Tool.Blur) a.Draw(g, false, false);
            bool any = false;
            using (var rg = new Region(area))
            {
                foreach (var a in all)
                    if (a.Kind == Tool.Spot)
                    {
                        var r = a.Bounds();
                        if (r.Width > 1 && r.Height > 1) { rg.Exclude(r); any = true; }
                    }
                if (any) using (var br = new SolidBrush(Color.FromArgb(150, 0, 0, 0))) g.FillRegion(br, rg);
            }
            foreach (var a in all)
                if (a.Kind != Tool.Blur && a.Kind != Tool.Spot) a.Draw(g, live && a == editingText, caretOn);
            if (live && picked != null && annots.Contains(picked))
            {
                var r = picked.Bounds(); r.Inflate(S(4), S(4));
                var sm = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.None;
                using (var p1 = new Pen(Art.Accent, Math.Max(1, scale))) g.DrawRectangle(p1, r.X, r.Y, r.Width, r.Height);
                using (var p2 = new Pen(Color.White, Math.Max(1, scale)) { DashStyle = DashStyle.Dash }) g.DrawRectangle(p2, r.X, r.Y, r.Width, r.Height);
                g.SmoothingMode = sm;
            }
        }

        void DoAction(int id)
        {
            if (!hasSel || sel.Width < 1 || sel.Height < 1) return;
            if (docMode && (id == 27 || id == 28 || id == 29 || id == 31 || id == 32)) return;   // não valem para a imagem com rolagem
            switch (id)
            {
                case 20: { var bmp = Final(); Close(); app.UploadAsync(bmp, TrayApp.AfterUpload.Link, null); break; }
                case 21: ShowShareMenu(); break;
                case 22: { var bmp = Final(); Close(); app.UploadAsync(bmp, TrayApp.AfterUpload.GoogleLens, null); break; }
                case 23: { var bmp = Final(); Close(); app.PrintLater(bmp); break; }
                case 28:
                    {
                        if (sel.Width < 16 || sel.Height < 16) { Flash("Área pequena demais para gravar"); return; }
                        var area = ScreenSel;
                        TrayApp.S.LastArea = area;
                        Close();
                        app.StartRecording(area);
                        break;
                    }
                case 31:
                    {
                        if (sel.Width < 32 || sel.Height < 32) { Flash("Área pequena demais para gravar"); return; }
                        var area = ScreenSel;
                        TrayApp.S.LastArea = area;
                        // a seleção ainda é a janela clicada: grava a janela (senão, o retângulo)
                        var win = selWindow != IntPtr.Zero && sel == selWindowRect ? selWindow : IntPtr.Zero;
                        Close();
                        app.StartVideo(area, win);
                        break;
                    }
                case 32:
                    {
                        if (sel.Width < 40 || sel.Height < 40) { Flash("Área pequena demais para o print com rolagem"); return; }
                        var area = ScreenSel;
                        TrayApp.S.LastArea = area;
                        Close();
                        app.StartScroll(area);
                        break;
                    }
                case 29:
                    {
                        // selecionar o texto direto na captura (a janela de texto fica no menu do botão direito)
                        ToggleTextMode();
                        break;
                    }
                case 24:
                    using (var bmp = Final()) { app.CopyImage(bmp); }
                    Close();
                    break;
                case 25:
                    {
                        var bmp = Final();
                        bool saved = app.SaveImage(bmp, this);
                        bmp.Dispose();
                        if (saved) Close(); else Native.ForceForeground(Handle);
                        break;
                    }
                case 26: Close(); break;
                case 27:
                    {
                        var bmp = Final();
                        var loc = new Point(sel.X + vs.X, sel.Y + vs.Y);
                        if (TrayApp.S.Frame) loc.Offset(-Art.FramePadNow, -Art.FramePadNow);
                        Close();
                        app.Pin(bmp, loc);
                        break;
                    }
            }
        }

        void ShowShareMenu()
        {
            if (shareMenu == null)
            {
                shareMenu = new ContextMenuStrip();
                string[][] nets = {
                    new[] { "X (Twitter)", "https://twitter.com/intent/tweet?url={0}" },
                    new[] { "Facebook", "https://www.facebook.com/sharer/sharer.php?u={0}" },
                    new[] { "WhatsApp", "https://api.whatsapp.com/send?text={0}" },
                    new[] { "Telegram", "https://t.me/share/url?url={0}" },
                    new[] { "Reddit", "https://www.reddit.com/submit?url={0}" },
                    new[] { "Pinterest", "https://www.pinterest.com/pin/create/button/?url={0}&media={0}" },
                    new[] { "LinkedIn", "https://www.linkedin.com/sharing/share-offsite/?url={0}" } };
                foreach (var n in nets)
                {
                    string tpl = n[1];
                    shareMenu.Items.Add(n[0], null, (s, e) =>
                    {
                        var bmp = Final(); Close();
                        app.UploadAsync(bmp, TrayApp.AfterUpload.Share, tpl);
                    });
                }
            }
            var b = btns.Find(x => x.Id == 21);
            shareMenu.Show(this, b != null ? new Point(b.R.Left, b.R.Bottom + S(6)) : mouse);
        }

        // ---------- pintura
        protected override void OnPaintBackground(PaintEventArgs e) { }

        // O buffer do WinForms só é reaproveitado até 225x96 px; acima disso ele cria e descarta um de
        // tela inteira (16 MB num par de monitores) a cada redesenho. Este fica guardado: ~40% mais rápido.
        readonly BufferedGraphicsContext bufCtx = new BufferedGraphicsContext();

        protected override void OnPaint(PaintEventArgs e)
        {
            bufCtx.MaximumBuffer = new Size(ClientSize.Width + 1, ClientSize.Height + 1);
            using (var bg = bufCtx.Allocate(e.Graphics, ClientRectangle))
            {
                if (paintRegion != null) bg.Graphics.Clip = paintRegion;
                else bg.Graphics.SetClip(e.ClipRectangle);
                PaintCore(new PaintEventArgs(bg.Graphics, e.ClipRectangle));
                if (paintRects == null) { bg.Render(e.Graphics); return; }
                // Render copiaria o buffer inteiro; aqui vão só as faixas que mudaram
                IntPtr dst = e.Graphics.GetHdc();
                try
                {
                    IntPtr src = bg.Graphics.GetHdc();
                    try { foreach (var r in paintRects) BitBlt(dst, r.X, r.Y, r.Width, r.Height, src, r.X, r.Y, 0x00CC0020 /*SRCCOPY*/); }
                    finally { bg.Graphics.ReleaseHdc(src); }
                }
                finally { e.Graphics.ReleaseHdc(dst); }
            }
        }

        // Imagem com rolagem: a parte visível da imagem sobre fundo escuro, anotações, barras e a posição.
        void PaintDoc(Graphics g)
        {
            g.Clear(Color.FromArgb(28, 29, 34));
            var docR = new Rectangle(view.X, view.Y, shot.Width, shot.Height);
            var vis = Rectangle.Intersect(docR, ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Art.Shadow(g, docR, 0, scale);
            if (vis.Width > 0 && vis.Height > 0)
            {
                var src = vis; src.Offset(-view.X, -view.Y);
                g.CompositingMode = CompositingMode.SourceCopy;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(shot, vis, src, GraphicsUnit.Pixel);
                g.CompositingMode = CompositingMode.SourceOver;
                g.PixelOffsetMode = PixelOffsetMode.Default;
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var st = g.Save();
            g.TranslateTransform(view.X, view.Y);
            g.SetClip(sel, CombineMode.Intersect);
            DrawAnnots(g, sel, true);
            g.Restore(st);

            // tamanho da imagem e o aviso de espessura
            var dimRect = DrawPill(g, shot.Width + " × " + shot.Height, new Point(Math.Max(0, vis.Left) + S(10), S(10)), fontUiB, Art.Pill, Color.White);
            if (flash != null)
            {
                var fr = DrawPill(g, flash + "      ", new Point(dimRect.Right + S(6), dimRect.Y), fontUiB, Art.Pill, Color.White);
                float d = Math.Min(fr.Height - S(8), thick * 1.2f + 2);
                using (var cb = new SolidBrush(color)) g.FillEllipse(cb, fr.Right - S(16) - d / 2, fr.Y + fr.Height / 2f - d / 2, d, d);
            }

            // barra de rolagem (só indica a posição)
            int total = shot.Height + DocTop + DocBottom, H = ClientSize.Height;
            if (total > H)
            {
                int trackH = H - S(24), thumbH = Math.Max(S(36), trackH * H / total);
                float pos = (DocTop - view.Y) / (float)(total - H);
                int ty = S(12) + (int)((trackH - thumbH) * Math.Max(0, Math.Min(1, pos)));
                Art.FillRound(g, Color.FromArgb(70, 255, 255, 255), new Rectangle(ClientSize.Width - S(10), S(12), S(5), trackH), S(2.5f));
                Art.FillRound(g, Color.FromArgb(200, 255, 255, 255), new Rectangle(ClientSize.Width - S(10), ty, S(5), thumbH), S(2.5f));
            }

            DrawBars(g);
            if (paletteOpen) DrawPalette(g);
            if (hover != null && !panning) DrawTooltip(g, hover);
            if (docHintUntil != DateTime.MinValue)
                DrawHintSegs(g, new[] {
                    new object[] { "Edite o print com rolagem", false },
                    new object[] { "Roda", true }, new object[] { "rola", false },
                    new object[] { "Arrastar", true }, new object[] { "move a imagem", false },
                    new object[] { "Ctrl+roda", true }, new object[] { "tamanho", false },
                    new object[] { "Esc", true }, new object[] { "fechar", false } }, false);
        }

        void PaintCore(PaintEventArgs e)
        {
            var g = e.Graphics;
            if (docMode) { PaintDoc(g); return; }
            var full = new Rectangle(0, 0, shot.Width, shot.Height);
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            BlitImage(g, dim, full);
            if (hasSel && sel.Width > 0 && sel.Height > 0) BlitImage(g, shot, sel);
            g.CompositingMode = CompositingMode.SourceOver;
            g.PixelOffsetMode = PixelOffsetMode.Default;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            if (RecMode && (!hasSel || sel.Width == 0))
            {
                if (drag != Drag.NewSel) DrawRecTarget(g, HitWindow(mouse));
                DrawRecHint(g);
                return;
            }
            if (!hasSel || sel.Width == 0)
            {
                DrawWindowHover(g, HitWindow(mouse));
                DrawCrosshair(g, mouse);
                DrawHint(g);
                DrawLoupe(g, mouse);
                return;
            }

            // anotações (recortadas à seleção)
            var st = g.Save();
            g.SetClip(sel, CombineMode.Intersect);
            DrawAnnots(g, sel, true);
            if (textMode && sel == liveSel) live.Paint(g, !live.HasSelection);
            g.Restore(st);

            // borda + alças
            g.SmoothingMode = SmoothingMode.None;
            using (var bp = new Pen(Color.FromArgb(230, 150, 115, 255), 1))
                g.DrawRectangle(bp, sel.X, sel.Y, sel.Width - 1, sel.Height - 1);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (drag != Drag.Draw && drag != Drag.Move)
            {
                var pts = HandlePoints();
                float hr = 4.5f * scale;
                using (var hp = new Pen(Art.Accent, 1.6f * scale))
                    for (int i = 0; i < 8; i++)
                    {
                        if (i % 2 == 1 && !ShowEdgeHandles) continue;
                        g.FillEllipse(Brushes.White, pts[i].X - hr, pts[i].Y - hr, hr * 2, hr * 2);
                        g.DrawEllipse(hp, pts[i].X - hr, pts[i].Y - hr, hr * 2, hr * 2);
                    }
            }

            // dimensões
            string dims = sel.Width + " × " + sel.Height;
            SizeF ds = g.MeasureString(dims, fontUiB);
            int pillH = (int)ds.Height + S(8);
            int ly = sel.Top - pillH - S(8);
            if (ly < 0) ly = sel.Top + S(8);
            var dimRect = DrawPill(g, dims, new Point(sel.Left, ly), fontUiB, Art.Pill, Color.White);
            if (textMode)
            {
                // estado do texto ao lado das dimensões
                var tr = DrawPill(g, LiveStatus(), new Point(dimRect.Right + S(6), dimRect.Y), fontUi, Color.FromArgb(235, 40, 110, 245), Color.White);
                dimRect = tr;
            }

            if (flash != null)
            {
                // indicador de espessura com prévia
                var fr = DrawPill(g, flash + "      ", new Point(dimRect.Right + S(6), dimRect.Y), fontUiB, Art.Pill, Color.White);
                float d = Math.Min(fr.Height - S(8), thick * 1.2f + 2);
                using (var cb = new SolidBrush(color)) g.FillEllipse(cb, fr.Right - S(16) - d / 2, fr.Y + fr.Height / 2f - d / 2, d, d);
            }

            if (drag != Drag.NewSel)
            {
                DrawBars(g);
                if (paletteOpen) DrawPalette(g);
                if (hover != null && drag == Drag.None) DrawTooltip(g, hover);
            }

            if (RecMode && drag == Drag.None) DrawRecHint(g);
            if (ShowLoupe && drag != Drag.None) DrawLoupe(g, mouse);
        }

        // Destaca a janela que um clique selecionaria.
        void DrawWindowHover(Graphics g, Rectangle r)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            BlitImage(g, shot, r);
            g.CompositingMode = CompositingMode.SourceOver;
            g.PixelOffsetMode = PixelOffsetMode.Default;
            g.SmoothingMode = SmoothingMode.None;
            float w = Math.Max(2, 2 * scale);
            using (var pen = new Pen(Art.Accent, w)) { pen.Alignment = PenAlignment.Inset; g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height); }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            DrawPill(g, HoverText(r), HoverPillAt(r), fontUiB, Art.Accent, Color.White);
        }

        void DrawCrosshair(Graphics g, Point p)
        {
            g.SmoothingMode = SmoothingMode.None;
            using (var pen = new Pen(Color.FromArgb(150, 255, 255, 255), 1))
            {
                pen.DashPattern = new float[] { 4, 4 };
                g.DrawLine(pen, 0, p.Y, ClientSize.Width, p.Y);
                g.DrawLine(pen, p.X, 0, p.X, ClientSize.Height);
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
        }

        void DrawHint(Graphics g)
        {
            // segmentos: texto normal e "teclas"
            object[][] segs = {
                new object[] { "Arraste para selecionar ou clique numa janela", false },
                new object[] { "Ctrl+A", true }, new object[] { "tela inteira", false },
                new object[] { "Ctrl+C", true }, new object[] { "copiar cor", false },
                new object[] { "Esc", true }, new object[] { "sair", false } };
            DrawHintSegs(g, segs, false);
        }

        // ---------- gravação (vídeo/GIF): seleção da fonte no estilo do Recordly
        bool RecMode { get { return VideoMode || GifMode || ScrollMode; } }
        readonly Dictionary<IntPtr, string> winLabels = new Dictionary<IntPtr, string>();

        string TargetLabel(Rectangle r)
        {
            int i = windows.IndexOf(r);
            IntPtr h = i >= 0 && i < windowHandles.Count ? windowHandles[i] : IntPtr.Zero;
            if (h == IntPtr.Zero) return Screen.AllScreens.Length > 1 ? "Tela inteira (este monitor)" : "Tela inteira";
            string s;
            if (!winLabels.TryGetValue(h, out s)) { s = Native.WindowLabel(h); winLabels[h] = s; }
            return s;
        }

        // Destaca a janela (ou o monitor) sob o cursor: realce azul, contorno e o nome com as medidas.
        void DrawRecTarget(Graphics g, Rectangle r)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            BlitImage(g, shot, r);
            g.CompositingMode = CompositingMode.SourceOver;
            g.PixelOffsetMode = PixelOffsetMode.Default;
            using (var tint = new SolidBrush(Color.FromArgb(40, Art.PickBlue))) g.FillRectangle(tint, r);
            g.SmoothingMode = SmoothingMode.None;
            float w = Math.Max(2, 2 * scale);
            using (var pen = new Pen(Art.PickBlue, w)) { pen.Alignment = PenAlignment.Inset; g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height); }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            DrawPill(g, HoverText(r), HoverPillAt(r), fontUiB, Art.PickBlue, Color.White);
        }

        void DrawRecHint(Graphics g)
        {
            object[][] segs;
            if (!hasSel || sel.Width == 0)
                segs = new[] {
                    new object[] { "Clique numa janela ou na área de trabalho, ou arraste uma área", false },
                    new object[] { "Esc", true }, new object[] { "cancelar", false } };
            else if (sel.Width < 16 || sel.Height < 16)
                segs = new[] {
                    new object[] { "Área pequena demais · arraste para aumentar", false },
                    new object[] { "Esc", true }, new object[] { "cancelar", false } };
            else
                segs = new[] {
                    new object[] { "Mova ou redimensione a área", false },
                    new object[] { "Enter", true }, new object[] { ScrollMode ? "capturar rolando" : "gravar", false },
                    new object[] { "Botão direito", true }, new object[] { "escolher outra", false },
                    new object[] { "Esc", true }, new object[] { "cancelar", false } };
            DrawHintSegs(g, segs, true);
        }

        // Aviso em pílula, centralizado no monitor do cursor (no topo ou embaixo, como no Recordly).
        void DrawHintSegs(Graphics g, object[][] segs, bool bottom)
        {
            var mon = docMode ? ClientRectangle : MonitorAt(mouse);
            int padX = S(14), gap = S(8), kpad = S(6);
            float total = 0;
            foreach (var s in segs)
            {
                SizeF sz = g.MeasureString((string)s[0], fontUi);
                total += sz.Width + ((bool)s[1] ? kpad * 2 : 0) + gap;
            }
            int h = S(36);
            var r = new RectangleF(mon.X + (mon.Width - total - padX * 2) / 2f, bottom ? mon.Bottom - S(96) - h : mon.Y + S(24), total + padX * 2 - gap, h);
            Art.Shadow(g, r, h / 2f, scale);
            Art.FillRound(g, Art.Pill, r, h / 2f);
            float x = r.X + padX;
            using (var gray = new SolidBrush(Color.FromArgb(205, 205, 215)))
                foreach (var s in segs)
                {
                    string t = (string)s[0];
                    SizeF sz = g.MeasureString(t, fontUi);
                    float ty = r.Y + (h - sz.Height) / 2f;
                    if ((bool)s[1])
                    {
                        var kr = new RectangleF(x, r.Y + S(7), sz.Width + kpad * 2, h - S(14));
                        Art.FillRound(g, Color.FromArgb(70, 255, 255, 255), kr, S(4));
                        g.DrawString(t, fontUi, Brushes.White, x + kpad, ty);
                        x += kr.Width + gap;
                    }
                    else
                    {
                        g.DrawString(t, fontUi, gray, x, ty);
                        x += sz.Width + gap;
                    }
                }
        }

        Rectangle PillRect(Graphics g, string text, Point at, Font f)
        {
            SizeF s = g.MeasureString(text, f);
            int px = S(9), py = S(4);
            var r = new Rectangle(at.X, at.Y, (int)s.Width + px * 2, (int)s.Height + py * 2);
            if (r.Right > ClientSize.Width) r.X = ClientSize.Width - r.Width - 2;
            if (r.Bottom > ClientSize.Height) r.Y = ClientSize.Height - r.Height - 2;
            if (r.X < 0) r.X = 0;
            return r;
        }

        Rectangle DrawPill(Graphics g, string text, Point at, Font f, Color bg, Color fg)
        {
            var r = PillRect(g, text, at, f);
            Art.FillRound(g, bg, r, S(6));
            using (var b = new SolidBrush(fg)) g.DrawString(text, f, b, r.X + S(9), r.Y + S(4));
            return r;
        }

        void DrawTooltip(Graphics g, Btn b)
        {
            string name = TipName(b.Id), key = TipKey(b.Id);
            if ((b.Id == 27 && pinMode) || (b.Id == 28 && GifMode) || (b.Id == 31 && VideoMode)) name += " (Enter)";
            SizeF ns = g.MeasureString(name, fontUiB);
            SizeF ks = key != null ? g.MeasureString(key, fontUi) : SizeF.Empty;
            int px = S(10), py = S(5), kg = S(8), kp = S(5);
            int w = (int)ns.Width + px * 2 + (key != null ? kg + (int)ks.Width + kp * 2 : 0);
            int h = (int)ns.Height + py * 2;
            int x, y;
            if (b.Id >= 20)
            {
                x = b.R.Left + b.R.Width / 2 - w / 2;
                y = actPanel.Bottom + S(8);
                if (y + h > ClientSize.Height) y = actPanel.Top - h - S(8);
            }
            else
            {
                x = toolPanel.Right + S(8);
                y = b.R.Top + (b.R.Height - h) / 2;
                if (x + w > ClientSize.Width) x = toolPanel.Left - w - S(8);
            }
            x = Math.Max(2, Math.Min(x, ClientSize.Width - w - 2));
            var r = new Rectangle(x, y, w, h);
            Art.Shadow(g, r, S(6), scale);
            Art.FillRound(g, Art.Pill, r, S(6));
            g.DrawString(name, fontUiB, Brushes.White, r.X + px, r.Y + py);
            if (key != null)
            {
                var kr = new RectangleF(r.X + px + ns.Width + kg, r.Y + S(4), ks.Width + kp * 2, h - S(8));
                Art.FillRound(g, Color.FromArgb(60, 255, 255, 255), kr, S(4));
                using (var kb = new SolidBrush(Color.FromArgb(225, 225, 235)))
                    g.DrawString(key, fontUi, kb, kr.X + kp, r.Y + (h - ks.Height) / 2f);
            }
        }

        void DrawBars(Graphics g)
        {
            if (btns.Count == 0) return;
            if (!toolPanel.IsEmpty) DrawPanel(g, toolPanel);
            DrawPanel(g, actPanel);
            foreach (var b in btns)
            {
                if (b.Id < 0)
                {
                    using (var sp = new Pen(Color.FromArgb(225, 225, 232), 1))
                    {
                        if (b.R.Width > b.R.Height) { int yy = b.R.Y + b.R.Height / 2; g.DrawLine(sp, b.R.X + S(7), yy, b.R.Right - S(7), yy); }
                        else { int xx = b.R.X + b.R.Width / 2; g.DrawLine(sp, xx, b.R.Y + S(7), xx, b.R.Bottom - S(7)); }
                    }
                    continue;
                }
                bool active = (b.Id == 13 && tool == Tool.None && !textMode) || (Annot.IsTool(b.Id) && (int)tool == b.Id) || (b.Id == 7 && paletteOpen) || (b.Id == 30 && TrayApp.S.Frame) || (b.Id == 29 && textMode);
                var r = b.R; r.Inflate(-S(2), -S(2));
                if (active) Art.FillRound(g, Color.FromArgb(40, Art.Accent), r, S(6));
                else if (b == hover) Art.FillRound(g, b.Id == 26 ? Color.FromArgb(255, 236, 236) : Color.FromArgb(240, 240, 245), r, S(6));
                Color fg = active ? Art.Accent : (b.Id == 26 ? Color.FromArgb(215, 50, 60) : ((b.Id == 27 && pinMode) || (b.Id == 28 && GifMode) || (b.Id == 31 && VideoMode) ? Art.Accent : Art.Ink));
                Art.Glyph(g, b.Id, b.R, fg, color);
            }
        }

        void DrawPanel(Graphics g, Rectangle r)
        {
            Art.Shadow(g, r, S(9), scale);
            Art.FillRound(g, Color.White, r, S(9));
        }

        void DrawPalette(Graphics g)
        {
            Art.Shadow(g, paletteRect, S(9), scale);
            Art.FillRound(g, Color.White, paletteRect, S(9));
            for (int i = 0; i < swatchRects.Length; i++)
            {
                var r = swatchRects[i];
                bool selected = Art.Palette[i].ToArgb() == color.ToArgb();
                if (selected || i == hoverSwatch)
                {
                    var o = r; o.Inflate(S(3), S(3));
                    using (var op = new Pen(selected ? Art.Accent : Color.FromArgb(200, 200, 210), 2 * scale)) g.DrawEllipse(op, o);
                }
                using (var b = new SolidBrush(Art.Palette[i])) g.FillEllipse(b, r);
                using (var bp = new Pen(Color.FromArgb(50, 0, 0, 0), 1)) g.DrawEllipse(bp, r);
            }
            if (hoverMore) Art.FillRound(g, Color.FromArgb(240, 240, 245), moreRect, S(6));
            using (var ink = new SolidBrush(Art.Ink))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString("Mais cores…", fontUi, ink, moreRect, sf);
            DrawSizeSlider(g);
        }

        void DrawSizeSlider(Graphics g)
        {
            var oldSmooth = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var line = new Pen(Color.FromArgb(230, 230, 236), 1))
                g.DrawLine(line, paletteRect.Left + S(8), sizeLabel.Top - S(5), paletteRect.Right - S(8), sizeLabel.Top - S(5));

            int v = ShownThick;
            using (var ink = new SolidBrush(Art.Ink))
            using (var grey = new SolidBrush(Color.FromArgb(120, 120, 130)))
            using (var left = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center })
            using (var right = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center })
            {
                g.DrawString("Tamanho", fontUi, ink, sizeLabel, left);
                g.DrawString(v.ToString(), fontUiB, grey, sizeLabel, right);
            }

            float cy = sizeTrack.Top + sizeTrack.Height / 2f;
            float f = (v - MinThick) / (float)(MaxThick - MinThick);
            float tx = sizeTrack.Left + f * sizeTrack.Width;
            // trilho afinando para a esquerda: dá a ideia de fino → grosso
            using (var track = new SolidBrush(Color.FromArgb(225, 225, 232)))
                g.FillPolygon(track, new[] { new PointF(sizeTrack.Left, cy - 1), new PointF(sizeTrack.Right, cy - S(4)), new PointF(sizeTrack.Right, cy + S(4)), new PointF(sizeTrack.Left, cy + 1) });
            using (var fill = new SolidBrush(color))
            {
                float hw = 1 + f * (S(4) - 1);
                g.FillPolygon(fill, new[] { new PointF(sizeTrack.Left, cy - 1), new PointF(tx, cy - hw), new PointF(tx, cy + hw), new PointF(sizeTrack.Left, cy + 1) });
            }
            float r = (sizeDrag || hoverSize) ? S(8) : S(7);
            using (var sh = new SolidBrush(Color.FromArgb(50, 0, 0, 0))) g.FillEllipse(sh, tx - r, cy - r + 1, 2 * r, 2 * r);
            using (var w = new SolidBrush(Color.White)) g.FillEllipse(w, tx - r, cy - r, 2 * r, 2 * r);
            using (var bp = new Pen(Art.Accent, 2 * scale)) g.DrawEllipse(bp, tx - r, cy - r, 2 * r, 2 * r);
            g.SmoothingMode = oldSmooth;
        }
    }

    // ---------------------------------------------------------------- Pin
    // Janela flutuante com a captura, sempre visível por cima das outras.
    class PinForm : Form
    {
        readonly TrayApp app;
        readonly Bitmap img;
        float zoom = 1f;
        bool dragging; Point dragOff;
        string flash; DateTime flashUntil;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly ToolStripMenuItem topItem;
        readonly LiveText live;
        bool textDrag;

        public void StartReading()
        {
            if (live.Status == LiveText.State.Idle && IsHandleCreated && img.Width * img.Height <= 4096 * 2160) live.Start(new Bitmap(img));
        }

        void CopySelectedText()
        {
            if (!live.HasSelection) return;
            try { Clipboard.SetText(live.SelectedText); Flash("Texto copiado"); } catch { Flash("Não foi possível copiar"); }
        }

        public PinForm(TrayApp app, Bitmap img, Point screenLoc)
        {
            this.app = app; this.img = img;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            Text = "Capturo - imagem fixada";
            Icon = Art.AppIcon();
            Cursor = Cursors.SizeAll;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Bounds = new Rectangle(screenLoc, img.Size);

            var m = new ContextMenuStrip();
            m.Items.Add(new ToolStripMenuItem("Copiar", null, (s, e) => app.CopyImage(img)) { ShortcutKeyDisplayString = "Ctrl+C" });
            m.Items.Add(new ToolStripMenuItem("Salvar como...", null, (s, e) => app.SaveImage(img, this)) { ShortcutKeyDisplayString = "Ctrl+S" });
            m.Items.Add(new ToolStripMenuItem("Abrir o texto numa janela (OCR)", null, (s, e) => app.CopyText(new Bitmap(img))) { ShortcutKeyDisplayString = "Ctrl+O" });
            m.Items.Add(new ToolStripSeparator());
            topItem = new ToolStripMenuItem("Sempre visível (por cima)") { Checked = true, CheckOnClick = true, ShortcutKeyDisplayString = "Ctrl+T" };
            topItem.CheckedChanged += (s, e) => SetTop(topItem.Checked);
            m.Items.Add(topItem);
            var op = new ToolStripMenuItem("Opacidade");
            foreach (int pct in new[] { 100, 80, 60, 40 })
            {
                int v = pct;
                op.DropDownItems.Add(pct + "%", null, (s, e) => { Opacity = v / 100.0; Flash("Opacidade " + v + "%"); });
            }
            m.Items.Add(op);
            m.Items.Add(new ToolStripMenuItem("Tamanho original", null, (s, e) => SetZoom(1f, new Point(0, 0))) { ShortcutKeyDisplayString = "0" });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem("Fechar", null, (s, e) => Close()) { ShortcutKeyDisplayString = "Esc" });
            var copySel = new ToolStripMenuItem("Copiar o texto selecionado", null, (s, e) => CopySelectedText()) { ShortcutKeyDisplayString = "Ctrl+C" };
            m.Items.Insert(0, copySel);
            m.Opening += (s, e) => copySel.Visible = live.HasSelection;
            ContextMenuStrip = m;

            // texto vivo: o texto da imagem fica selecionável (lido em segundo plano)
            live = new LiveText(this);
            live.Changed += () => Invalidate();
            // lê o texto só quando o mouse chega na imagem (fixar e só olhar não gasta nada)
            MouseEnter += (s, e) => StartReading();
            KeyDown += (s, e) => { if (e.Control && e.KeyCode == Keys.A) StartReading(); };

            timer.Interval = 300;
            timer.Tick += (s, e) => { if (flash != null && DateTime.Now > flashUntil) { flash = null; Invalidate(); } };
            timer.Start();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x02E0) return; // mantém o tamanho em pixels ao mudar de monitor
            base.WndProc(ref m);
        }

        void SetTop(bool top)
        {
            TopMost = top;
            ShowInTaskbar = !top; // fora do "sempre visível", fica acessível pela barra de tarefas
            if (topItem.Checked != top) topItem.Checked = top;
            Flash(top ? "Sempre visível: ligado" : "Sempre visível: desligado");
        }

        void Flash(string t) { flash = t; flashUntil = DateTime.Now.AddSeconds(1.3); Invalidate(); }

        void SetZoom(float z, Point anchor)
        {
            z = Math.Max(0.1f, Math.Min(8f, z));
            float rx = ClientSize.Width > 0 ? anchor.X / (float)ClientSize.Width : 0;
            float ry = ClientSize.Height > 0 ? anchor.Y / (float)ClientSize.Height : 0;
            int nw = Math.Max(8, (int)Math.Round(img.Width * z)), nh = Math.Max(8, (int)Math.Round(img.Height * z));
            Point sa = PointToScreen(anchor);
            zoom = z;
            Bounds = new Rectangle((int)(sa.X - rx * nw), (int)(sa.Y - ry * nh), nw, nh);
            Flash("Zoom " + Math.Round(z * 100) + "%");
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.InterpolationMode = Math.Abs(zoom - 1f) < 0.001f ? InterpolationMode.NearestNeighbor : (zoom > 1 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic);
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(img, new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), 0, 0, img.Width, img.Height, GraphicsUnit.Pixel);
            g.PixelOffsetMode = PixelOffsetMode.Default;
            live.Origin = PointF.Empty;
            live.Scale = ClientSize.Width / (float)img.Width;
            live.Paint(g, false);
            using (var p = new Pen(ContainsFocus ? Art.Accent : Color.FromArgb(150, 120, 120, 130), 1))
                g.DrawRectangle(p, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            if (flash != null)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                using (var f = new Font("Segoe UI Semibold", 12, FontStyle.Regular, GraphicsUnit.Pixel))
                {
                    SizeF s = g.MeasureString(flash, f);
                    var r = new RectangleF(6, ClientSize.Height - s.Height - 14, s.Width + 14, s.Height + 6);
                    Art.FillRound(g, Art.Pill, r, 5);
                    g.DrawString(flash, f, Brushes.White, r.X + 7, r.Y + 3);
                }
            }
        }

        protected override void OnActivated(EventArgs e) { base.OnActivated(e); Invalidate(); }
        protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            // sobre uma palavra: seleciona texto; fora: arrasta a janela
            live.Scale = ClientSize.Width / (float)img.Width;
            if (live.Over(e.Location) || (ModifierKeys & Keys.Shift) != 0 && live.HasSelection)
            {
                live.MouseDown(e.Location, (ModifierKeys & Keys.Shift) != 0);
                textDrag = live.Dragging;
                return;
            }
            if (live.HasSelection) live.ClearSelection();
            dragging = true; dragOff = e.Location;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (textDrag) { live.MouseMove(e.Location); return; }
            if (dragging) { var c = Cursor.Position; Location = new Point(c.X - dragOff.X, c.Y - dragOff.Y); return; }
            Cursor = live.Over(e.Location) ? Cursors.IBeam : Cursors.SizeAll;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (textDrag) { textDrag = false; live.MouseUp(); }
            dragging = false;
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (live.Over(e.Location)) return;   // duplo clique numa palavra seleciona a palavra
            if (e.Button == MouseButtons.Left) Close();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if ((ModifierKeys & Keys.Control) != 0)
            {
                Opacity = Math.Max(0.2, Math.Min(1.0, Opacity + (e.Delta > 0 ? 0.1 : -0.1)));
                Flash("Opacidade " + Math.Round(Opacity * 100) + "%");
            }
            else SetZoom(e.Delta > 0 ? zoom * 1.1f : zoom / 1.1f, e.Location);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) { if (live.HasSelection) live.ClearSelection(); else Close(); }
            else if (e.Control && e.KeyCode == Keys.C) { if (live.HasSelection) CopySelectedText(); else app.CopyImage(img); }
            else if (e.Control && e.KeyCode == Keys.A) { live.SelectAll(); if (!live.Ready) Flash("Ainda lendo o texto..."); }
            else if (e.Control && e.KeyCode == Keys.O) app.CopyText(new Bitmap(img));
            else if (e.Control && e.KeyCode == Keys.S) app.SaveImage(img, this);
            else if (e.Control && e.KeyCode == Keys.T) SetTop(!TopMost);
            else if (e.KeyCode == Keys.D0 || e.KeyCode == Keys.NumPad0) SetZoom(1f, new Point(0, 0));
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Dispose();
            img.Dispose();
            base.OnFormClosed(e);
        }
    }

    // --------------------------------------------------------------- Settings UI
    class HotkeyBox : TextBox
    {
        Hotkey value;
        bool recording;
        public event Action<string> Problem;
        public Hotkey Value { get { return value; } set { this.value = value; StopRecording(); } }

        public HotkeyBox()
        {
            ReadOnly = true;
            BackColor = SystemColors.Window;
            Cursor = Cursors.Hand;
            TextAlign = HorizontalAlignment.Center;
            Enter += (s, e) => StartRecording();
            Click += (s, e) => { if (!recording) StartRecording(); };
            Leave += (s, e) => StopRecording();
        }

        void StartRecording()
        {
            recording = true;
            Text = "Pressione o atalho...";
            BackColor = Color.FromArgb(238, 232, 255);
            TrayApp.Recorder = OnKey;
        }

        public void StopRecording()
        {
            recording = false;
            if (TrayApp.Recorder != null && TrayApp.Recorder.Target == this) TrayApp.Recorder = null;
            Text = value.ToString();
            BackColor = SystemColors.Window;
        }

        void OnKey(Hotkey hk)
        {
            if (!recording) return;
            if (hk.Mods == 0 && hk.Vk == 0x1B) { StopRecording(); return; }
            if (hk.Mods == 0 && (hk.Vk == 0x08 || hk.Vk == 0x2E)) { value = new Hotkey(); StopRecording(); return; }
            if (hk.Mods == 0 && !Hotkey.AllowedAlone(hk.Vk))
            {
                if (Problem != null) Problem("Use um modificador (Ctrl, Alt, Shift ou Win) com a tecla " + Hotkey.KeyName(hk.Vk) + ".");
                return;
            }
            value = hk;
            if (Problem != null) Problem(null);
            StopRecording();
        }
    }

    // Prévia das barras da captura no tamanho escolhido nas configurações.
    class IconPreview : Control
    {
        public int Percent = 100;
        public float Dpi = 1f;
        static readonly int[] Ids = { 1, 3, 4, 6, -1, 7, 24, 25 };

        public IconPreview()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Color.FromArgb(58, 60, 66));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float f = Percent / 100f * Dpi;
            int b = (int)Math.Round(34 * f), pad = (int)Math.Round(5 * f), sep = (int)Math.Round(9 * f);
            int len = 0; foreach (int id in Ids) len += id < 0 ? sep : b;
            var bar = new Rectangle((Width - len - pad * 2) / 2, (Height - b - pad * 2) / 2, len + pad * 2, b + pad * 2);
            Art.Shadow(g, bar, 9 * Dpi, Dpi);
            Art.FillRound(g, Color.White, bar, 9 * Dpi);
            int x = bar.X + pad;
            var col = Color.FromArgb(TrayApp.S.ColorArgb);
            foreach (int id in Ids)
            {
                if (id < 0)
                {
                    using (var sp = new Pen(Color.FromArgb(225, 225, 232), 1))
                        g.DrawLine(sp, x + sep / 2, bar.Y + pad + 7 * Dpi, x + sep / 2, bar.Y + pad + b - 7 * Dpi);
                    x += sep; continue;
                }
                Art.Glyph(g, id, new Rectangle(x, bar.Y + pad, b, b), Art.Ink, col);
                x += b;
            }
        }
    }

    // Prévia da moldura nas configurações: uma "janela" de exemplo com o estilo escolhido.
    class FramePreview : Control
    {
        Bitmap sample, framed;
        int bg = -1, pad, rad, sh;

        public FramePreview() { DoubleBuffered = true; ResizeRedraw = true; }

        public void SetStyle(int bg, int pad, int rad, int sh)
        {
            this.bg = bg; this.pad = pad; this.rad = rad; this.sh = sh;
            if (framed != null) { framed.Dispose(); framed = null; }
            Invalidate();
        }

        static Bitmap Sample()
        {
            var b = new Bitmap(360, 200, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.White);
                using (var t = new SolidBrush(Color.FromArgb(243, 244, 248))) g.FillRectangle(t, 0, 0, 360, 34);
                Color[] dots = { Color.FromArgb(255, 95, 87), Color.FromArgb(255, 189, 46), Color.FromArgb(40, 200, 64) };
                for (int i = 0; i < 3; i++) using (var d = new SolidBrush(dots[i])) g.FillEllipse(d, 14 + i * 18, 11, 12, 12);
                using (var a = new SolidBrush(Art.Accent)) g.FillRectangle(a, 24, 58, 120, 14);
                using (var l = new SolidBrush(Color.FromArgb(214, 217, 225)))
                    for (int i = 0; i < 5; i++) g.FillRectangle(l, 24, 88 + i * 20, i == 4 ? 180 : 300, 9);
            }
            return b;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            // xadrez: mostra quando o fundo é transparente
            int c = Math.Max(6, Height / 24);
            using (var l = new SolidBrush(Color.FromArgb(250, 250, 252))) g.FillRectangle(l, ClientRectangle);
            using (var dk = new SolidBrush(Color.FromArgb(232, 233, 238)))
                for (int y = 0; y < Height; y += c) for (int x = (y / c % 2) * c; x < Width; x += c * 2) g.FillRectangle(dk, x, y, c, c);
            if (bg < 0) return;
            if (sample == null) sample = Sample();
            if (framed == null) framed = Art.Framed(sample, bg, pad, rad, sh);
            float k = Math.Min((Width - 8f) / framed.Width, (Height - 8f) / framed.Height);
            float w = framed.Width * k, h = framed.Height * k;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(framed, (Width - w) / 2, (Height - h) / 2, w, h);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { if (sample != null) sample.Dispose(); if (framed != null) framed.Dispose(); }
            base.Dispose(disposing);
        }
    }

    class SettingsForm : Form
    {
        readonly TextBox folder;
        readonly ComboBox format;
        readonly CheckBox quick, openAfter, startup, history;
        readonly TrackBar iconSize;
        readonly HotkeyBox hkCapture, hkFull, hkPin, hkRepeat, hkVideo, hkScroll;
        readonly Label problem;
        readonly float k;

        int D(int v) { return (int)Math.Round(v * k); }
        Point P(int x, int y) { return new Point(D(x), D(y)); }

        public SettingsForm()
        {
            using (var g = CreateGraphics()) k = g.DpiX / 96f;
            Text = "Capturo - Configurações";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Segoe UI", 12f * k, FontStyle.Regular, GraphicsUnit.Pixel);
            ClientSize = new Size(D(500), D(510));
            Icon = Art.AppIcon();
            TopMost = true;
            var S = TrayApp.S;

            var tabs = new TabControl { Location = P(10, 10), Size = new Size(D(480), D(446)) };
            var pgKeys = new TabPage("Atalhos e salvar") { UseVisualStyleBackColor = true };
            var pgGeneral = new TabPage("Geral") { UseVisualStyleBackColor = true };
            var pgVideo = new TabPage("Vídeo") { UseVisualStyleBackColor = true };
            var pgFrame = new TabPage("Moldura") { UseVisualStyleBackColor = true };
            tabs.TabPages.Add(pgKeys); tabs.TabPages.Add(pgGeneral); tabs.TabPages.Add(pgFrame); tabs.TabPages.Add(pgVideo);
            Controls.Add(tabs);

            // --- Atalhos
            var gk = new GroupBox { Text = "Atalhos de teclado", Location = P(8, 8), Size = new Size(D(456), D(264)) };
            pgKeys.Controls.Add(gk);
            problem = new Label { Location = P(14, 238), Size = new Size(D(430), D(20)), ForeColor = Color.FromArgb(200, 40, 40) };
            gk.Controls.Add(new Label { Text = "Clique no campo e pressione a combinação (Esc cancela, Backspace limpa).", Location = P(14, 22), AutoSize = true, ForeColor = SystemColors.GrayText });
            hkCapture = AddHotkeyRow(gk, "Capturar área", 46, S.CaptureKey);
            hkFull = AddHotkeyRow(gk, "Salvar tela inteira", 78, S.FullKey);
            hkPin = AddHotkeyRow(gk, "Capturar e fixar na tela", 110, S.PinKey);
            hkRepeat = AddHotkeyRow(gk, "Repetir a última área", 142, S.RepeatKey);
            hkVideo = AddHotkeyRow(gk, "Gravar vídeo", 174, S.VideoKey);
            hkScroll = AddHotkeyRow(gk, "Print com rolagem", 206, S.ScrollKey);
            gk.Controls.Add(problem);

            // --- Salvamento
            var gs = new GroupBox { Text = "Salvar", Location = P(8, 280), Size = new Size(D(456), D(132)) };
            pgKeys.Controls.Add(gs);
            gs.Controls.Add(new Label { Text = "Pasta:", Location = P(14, 30), AutoSize = true });
            folder = new TextBox { Text = S.SaveFolder, Location = P(90, 26), Width = D(262) };
            var browse = new Button { Text = "Procurar...", Location = P(358, 24), Size = new Size(D(86), D(28)) };
            browse.Click += (s, e) =>
            {
                using (var d = new FolderBrowserDialog { SelectedPath = folder.Text })
                    if (d.ShowDialog(this) == DialogResult.OK) folder.Text = d.SelectedPath;
            };
            gs.Controls.Add(folder); gs.Controls.Add(browse);
            gs.Controls.Add(new Label { Text = "Formato:", Location = P(14, 66), AutoSize = true });
            format = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = P(90, 62), Width = D(90) };
            format.Items.AddRange(new object[] { "png", "jpg" });
            format.SelectedItem = S.Format;
            gs.Controls.Add(format);
            quick = new CheckBox { Text = "Salvar direto na pasta, sem perguntar o nome", Checked = S.QuickSave, Location = P(16, 98), AutoSize = true };
            gs.Controls.Add(quick);

            // --- Geral
            var gg = new GroupBox { Text = "Geral", Location = P(8, 8), Size = new Size(D(456), D(250)) };
            pgGeneral.Controls.Add(gg);
            openAfter = new CheckBox { Text = "Abrir o link no navegador após enviar para a nuvem", Checked = S.OpenAfterUpload, Location = P(16, 28), AutoSize = true };
            startup = new CheckBox { Text = "Iniciar com o Windows", Checked = Settings.StartWithWindows, Location = P(16, 58), AutoSize = true };
            history = new CheckBox { Text = "Guardar as últimas " + CaptureHistory.Keep + " capturas na galeria", Checked = S.KeepHistory, Location = P(16, 88), AutoSize = true };
            gg.Controls.Add(openAfter); gg.Controls.Add(startup); gg.Controls.Add(history);

            gg.Controls.Add(new Label { Text = "Tamanho dos ícones:", Location = P(14, 128), AutoSize = true });
            iconSize = new TrackBar
            {
                Minimum = Settings.MinIcon / 10, Maximum = Settings.MaxIcon / 10, SmallChange = 1, LargeChange = 2,
                TickFrequency = 1, Value = S.IconScale / 10, Location = P(150, 120), Size = new Size(D(190), D(34)), AutoSize = false
            };
            var iconPct = new Label { Location = P(344, 128), Size = new Size(D(46), D(20)), TextAlign = ContentAlignment.TopRight };
            var iconReset = new Button { Text = "100%", Location = P(394, 124), Size = new Size(D(50), D(26)), Font = new Font(Font.FontFamily, 10.5f * k, GraphicsUnit.Pixel) };
            var preview = new IconPreview { Location = P(14, 162), Size = new Size(D(428), D(76)), Dpi = k };
            EventHandler sync = (s, e) => { iconPct.Text = iconSize.Value * 10 + "%"; preview.Percent = iconSize.Value * 10; preview.Invalidate(); };
            iconSize.ValueChanged += sync;
            iconReset.Click += (s, e) => iconSize.Value = 10;
            sync(null, null);
            gg.Controls.Add(iconSize); gg.Controls.Add(iconPct); gg.Controls.Add(iconReset); gg.Controls.Add(preview);

            var gd = new GroupBox { Text = "Atraso", Location = P(8, 266), Size = new Size(D(456), D(96)) };
            pgGeneral.Controls.Add(gd);
            gd.Controls.Add(new Label { Text = "Esperar antes de capturar:", Location = P(14, 30), AutoSize = true });
            var delay = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = P(230, 26), Width = D(200) };
            delay.Items.AddRange(new object[] { "Não esperar", "3 segundos", "5 segundos", "10 segundos" });
            delay.SelectedIndex = S.CaptureDelay >= 10 ? 3 : S.CaptureDelay >= 5 ? 2 : S.CaptureDelay >= 1 ? 1 : 0;
            gd.Controls.Add(delay);
            gd.Controls.Add(new Label { Text = "Vale para o atalho Capturar área e o clique no ícone. Dá tempo de abrir um menu antes do print; Esc cancela a contagem.", Location = P(14, 58), Size = new Size(D(430), D(34)), ForeColor = SystemColors.GrayText });

            // --- Moldura
            var gf = new GroupBox { Text = "Moldura das capturas", Location = P(8, 8), Size = new Size(D(456), D(404)) };
            pgFrame.Controls.Add(gf);
            var fOn = new CheckBox { Text = "Aplicar a moldura (também liga e desliga com F na captura)", Checked = S.Frame, Location = P(16, 26), AutoSize = true };
            gf.Controls.Add(fOn);
            gf.Controls.Add(new Label { Text = "Fundo:", Location = P(14, 62), AutoSize = true });
            var fBg = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = P(100, 58), Width = D(220), MaxDropDownItems = 14 };
            for (int i = 0; i < Art.FrameBgCount; i++) fBg.Items.Add(Art.FrameBgName(i));
            fBg.SelectedIndex = Math.Max(0, Math.Min(Art.FrameBgCount - 1, S.FrameBg));
            gf.Controls.Add(fBg);
            Func<string, int, int, int, int, int, TrackBar> slider = (label, y, min, max, val, step) =>
            {
                gf.Controls.Add(new Label { Text = label, Location = P(14, y + 6), AutoSize = true });
                var tb = new TrackBar { Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, val)), SmallChange = step, LargeChange = step * 4, TickStyle = TickStyle.None, Location = P(96, y), Size = new Size(D(270), D(30)), AutoSize = false };
                gf.Controls.Add(tb);
                return tb;
            };
            var fPad = slider("Margem:", 92, 0, 120, S.FramePadding, 2);
            var fRad = slider("Cantos:", 126, 0, 40, S.FrameRadius, 1);
            var fSh = slider("Sombra:", 160, 0, 100, S.FrameShadow, 5);
            var lPad = new Label { Location = P(372, 98), Size = new Size(D(70), D(20)) };
            var lRad = new Label { Location = P(372, 132), Size = new Size(D(70), D(20)) };
            var lSh = new Label { Location = P(372, 166), Size = new Size(D(70), D(20)) };
            gf.Controls.Add(lPad); gf.Controls.Add(lRad); gf.Controls.Add(lSh);
            var fPrev = new FramePreview { Location = P(14, 200), Size = new Size(D(428), D(190)) };
            gf.Controls.Add(fPrev);
            EventHandler fSync = (s2, e2) =>
            {
                lPad.Text = fPad.Value + " px"; lRad.Text = fRad.Value + " px"; lSh.Text = fSh.Value + "%";
                fPrev.SetStyle(fBg.SelectedIndex, fPad.Value, fRad.Value, fSh.Value);
            };
            fBg.SelectedIndexChanged += fSync; fPad.ValueChanged += fSync; fRad.ValueChanged += fSync; fSh.ValueChanged += fSync;
            fSync(null, null);

            // --- Vídeo
            var gv = new GroupBox { Text = "Gravação de vídeo", Location = P(8, 8), Size = new Size(D(456), D(248)) };
            pgVideo.Controls.Add(gv);
            gv.Controls.Add(new Label { Text = "Quadros por segundo:", Location = P(14, 32), AutoSize = true });
            var vfps = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = P(230, 28), Width = D(200) };
            vfps.Items.AddRange(new object[] { "30 (arquivos menores)", "60 (movimento mais fluido)" });
            vfps.SelectedIndex = S.VidFps >= 60 ? 1 : 0;
            gv.Controls.Add(new Label { Text = "Contagem antes de começar:", Location = P(14, 68), AutoSize = true });
            var vcount = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = P(230, 64), Width = D(200) };
            vcount.Items.AddRange(new object[] { "Sem contagem", "3 segundos", "5 segundos" });
            vcount.SelectedIndex = S.VidCountdown >= 5 ? 2 : S.VidCountdown >= 1 ? 1 : 0;
            gv.Controls.Add(new Label { Text = "Duração máxima:", Location = P(14, 104), AutoSize = true });
            var vmax = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = P(230, 100), Width = D(200) };
            vmax.Items.AddRange(new object[] { "5 minutos", "10 minutos", "15 minutos" });
            vmax.SelectedIndex = S.VidMaxMin <= 5 ? 0 : S.VidMaxMin <= 10 ? 1 : 2;
            var vsys = new CheckBox { Text = "Gravar o som do computador", Checked = S.RecSys, Location = P(16, 140), AutoSize = true };
            var vmic = new CheckBox { Text = "Gravar o microfone", Checked = S.RecMic, Location = P(16, 170), AutoSize = true };
            gv.Controls.Add(vfps); gv.Controls.Add(vcount); gv.Controls.Add(vmax); gv.Controls.Add(vsys); gv.Controls.Add(vmic);
            gv.Controls.Add(new Label { Text = "Webcam:", Location = P(14, 210), AutoSize = true });
            var vcam = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = P(230, 206), Width = D(200) };
            vcam.Items.Add("Não gravar");
            List<string> cams;
            try { cams = Webcam.Devices(); } catch { cams = new List<string>(); }
            foreach (var c in cams) vcam.Items.Add(c);
            int ci = S.VidCam ? Math.Max(0, cams.IndexOf(S.VidCamName)) + 1 : 0;
            vcam.SelectedIndex = cams.Count == 0 ? 0 : Math.Min(ci, cams.Count);
            if (cams.Count == 0) { vcam.Items[0] = "Nenhuma câmera encontrada"; vcam.Enabled = false; }
            gv.Controls.Add(vcam);
            var ga = new GroupBox { Text = "Ao parar a gravação", Location = P(8, 264), Size = new Size(D(456), D(96)) };
            pgVideo.Controls.Add(ga);
            var vEditor = new RadioButton { Text = "Abrir o editor (zoom, cursor, fundo, corte)", Checked = S.VidOpenEditor, Location = P(16, 28), AutoSize = true };
            var vDirect = new RadioButton { Text = "Exportar o MP4 direto, com o último estilo usado", Checked = !S.VidOpenEditor, Location = P(16, 58), AutoSize = true };
            ga.Controls.Add(vEditor); ga.Controls.Add(vDirect);
            pgVideo.Controls.Add(new Label { Text = "O microfone, o som do computador e a webcam também podem ser escolhidos no painel que aparece antes da contagem, e ligados ou desligados durante a gravação. Clique numa janela para gravar só ela (mesmo se for movida ou coberta), ou na área de trabalho para gravar a tela inteira.", Location = P(12, 368), Size = new Size(D(450), D(62)), ForeColor = SystemColors.GrayText });

            var ok = new Button { Text = "Salvar", Location = P(310, 470), Size = new Size(D(86), D(30)) };
            var cancel = new Button { Text = "Cancelar", Location = P(402, 470), Size = new Size(D(86), D(30)), DialogResult = DialogResult.Cancel };
            Controls.Add(ok); Controls.Add(cancel);
            CancelButton = cancel;
            ok.Click += (s, e) =>
            {
                var keys = new[] { hkCapture.Value, hkFull.Value, hkPin.Value, hkRepeat.Value, hkVideo.Value, hkScroll.Value };
                for (int i = 0; i < keys.Length; i++)
                    for (int j = i + 1; j < keys.Length; j++)
                        if (keys[i].Same(keys[j])) { tabs.SelectedTab = pgKeys; problem.Text = "Dois comandos estão com o mesmo atalho (" + keys[i] + ")."; return; }
                S.CaptureKey = keys[0]; S.FullKey = keys[1]; S.PinKey = keys[2]; S.RepeatKey = keys[3]; S.VideoKey = keys[4]; S.ScrollKey = keys[5];
                S.CaptureDelay = new[] { 0, 3, 5, 10 }[delay.SelectedIndex];
                S.Frame = fOn.Checked; S.FrameBg = fBg.SelectedIndex;
                S.FramePadding = fPad.Value; S.FrameRadius = fRad.Value; S.FrameShadow = fSh.Value;
                S.VidFps = vfps.SelectedIndex == 1 ? 60 : 30;
                S.VidCountdown = vcount.SelectedIndex == 2 ? 5 : vcount.SelectedIndex == 1 ? 3 : 0;
                S.VidMaxMin = vmax.SelectedIndex == 0 ? 5 : vmax.SelectedIndex == 1 ? 10 : 15;
                S.RecSys = vsys.Checked; S.RecMic = vmic.Checked;
                S.VidOpenEditor = vEditor.Checked;
                if (vcam.Enabled)
                {
                    S.VidCam = vcam.SelectedIndex > 0;
                    if (S.VidCam) S.VidCamName = (string)vcam.SelectedItem;
                }
                S.KeepHistory = history.Checked;
                S.SaveFolder = folder.Text.Trim().Length > 0 ? folder.Text.Trim() : S.SaveFolder;
                S.Format = (string)format.SelectedItem;
                S.QuickSave = quick.Checked;
                S.OpenAfterUpload = openAfter.Checked;
                S.IconScale = iconSize.Value * 10;
                try { Settings.StartWithWindows = startup.Checked; } catch { }
                S.Save();
                DialogResult = DialogResult.OK;
            };
            Deactivate += (s, e) => { hkCapture.StopRecording(); hkFull.StopRecording(); hkPin.StopRecording(); hkRepeat.StopRecording(); hkVideo.StopRecording(); hkScroll.StopRecording(); };
            Shown += (s, e) => { Native.ForceForeground(Handle); cancel.Focus(); };
        }

        HotkeyBox AddHotkeyRow(GroupBox parent, string label, int y, Hotkey value)
        {
            parent.Controls.Add(new Label { Text = label, Location = P(14, y + 4), AutoSize = true });
            var box = new HotkeyBox { Location = P(170, y), Width = D(200) };
            box.Value = value;
            box.Problem += msg => problem.Text = msg ?? "";
            var clear = new Button { Text = "Limpar", Location = P(376, y - 1), Size = new Size(D(68), D(28)) };
            clear.Click += (s, e) => { box.Value = new Hotkey(); problem.Text = ""; };
            parent.Controls.Add(box); parent.Controls.Add(clear);
            return box;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            TrayApp.Recorder = null;
            base.OnFormClosed(e);
        }
    }

    // ------------------------------------------------------------- Tray app
    class TrayApp : ApplicationContext
    {
        public static Settings S;
        public static Action<Hotkey> Recorder;   // quando definido, a próxima combinação vai para a tela de configurações
        static bool paused;
        public enum AfterUpload { Link, GoogleLens, Share }

        readonly NotifyIcon tray;
        readonly Control invoker;
        readonly Native.LowLevelKeyboardProc hookProc;
        IntPtr hook = IntPtr.Zero;
        int swallowUpVk = -1;
        OverlayForm overlay;
        string balloonTarget;
        readonly ToolStripMenuItem historyMenu, miCapture, miFull, miPin, miClosePins, miRepeat, miGif, miVideo, miSaved, miScroll;
        GalleryForm gallery;
        CountdownForm countdown;
        readonly List<PinForm> pins = new List<PinForm>();

        public TrayApp()
        {
            S = Settings.Load();
            invoker = new Control();
            invoker.CreateControl();
            var unused = invoker.Handle;

            var menu = new ContextMenuStrip();
            miCapture = new ToolStripMenuItem("Capturar área", null, (s, e) => StartCapture(false));
            miCapture.Font = new Font(menu.Font, FontStyle.Bold);
            miPin = new ToolStripMenuItem("Capturar e fixar na tela", null, (s, e) => StartCapture(true));
            miFull = new ToolStripMenuItem("Salvar tela inteira", null, (s, e) => QuickFullScreen());
            miClosePins = new ToolStripMenuItem("Fechar imagens fixadas", null, (s, e) => { foreach (var p in pins.ToArray()) p.Close(); });
            miRepeat = new ToolStripMenuItem("Repetir a última área", null, (s, e) => RepeatLastArea());
            miGif = new ToolStripMenuItem("Gravar GIF de uma área", null, (s, e) => StartCapture(false, Rectangle.Empty, true));
            miVideo = new ToolStripMenuItem("Gravar vídeo de uma área", null, (s, e) => StartCapture(false, Rectangle.Empty, false, true));
            miScroll = new ToolStripMenuItem("Print com rolagem", null, (s, e) => StartScrollPick());
            var miDelay = new ToolStripMenuItem("Capturar com atraso");
            foreach (int sec in new[] { 3, 5, 10 })
            {
                int v = sec;
                miDelay.DropDownItems.Add(v + " segundos", null, (s, e) => DelayedCapture(v));
            }
            historyMenu = new ToolStripMenuItem("Envios recentes");
            menu.Items.Add(miCapture);
            menu.Items.Add(miPin);
            menu.Items.Add(miRepeat);
            menu.Items.Add(miDelay);
            menu.Items.Add(miScroll);
            menu.Items.Add(miFull);
            menu.Items.Add(miVideo);
            miSaved = new ToolStripMenuItem("Gravações não exportadas");
            menu.Items.Add(miSaved);
            menu.Items.Add(miGif);
            menu.Items.Add(miClosePins);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Galeria de capturas...", null, (s, e) => ShowGallery());
            menu.Items.Add(historyMenu);
            menu.Items.Add("Abrir pasta de capturas", null, (s, e) => { Directory.CreateDirectory(S.SaveFolder); Services.Open(S.SaveFolder); });
            menu.Items.Add("Configurações...", null, (s, e) => ShowSettings());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Sair", null, (s, e) => ExitThread());
            menu.Opening += (s, e) => RefreshMenu();

            tray = new NotifyIcon { Icon = Art.AppIcon(), ContextMenuStrip = menu, Visible = true };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) CaptureArea(); };
            tray.BalloonTipClicked += (s, e) => { if (balloonTarget != null) Services.Open(balloonTarget); };
            UpdateTrayText();

            hookProc = HookCallback;
            hook = Native.SetWindowsHookEx(13 /*WH_KEYBOARD_LL*/, hookProc, Native.GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero) Notify("Atenção", "Não foi possível registrar os atalhos de teclado.", null);

            // Permite disparar uma captura de fora: "Capturo.exe --capture" (útil para atalhos personalizados)
            var t = new Thread(() =>
            {
                while (true)
                {
                    Program.CaptureSignal.WaitOne();
                    invoker.BeginInvoke((Action)(() => StartCapture(false)));
                }
            });
            t.IsBackground = true;
            t.Start();
            var t2 = new Thread(() =>
            {
                while (true)
                {
                    Program.SettingsSignal.WaitOne();
                    invoker.BeginInvoke((Action)ShowSettings);
                }
            });
            t2.IsBackground = true;
            t2.Start();

            VideoRecorder.CleanOld();
            idleTrim.Tick += (s2, e2) => TrimMemory();
            ScheduleTrim();
            Notify("Capturo está rodando", S.CaptureKey.IsEmpty
                ? "Clique no ícone da bandeja para capturar."
                : "Pressione " + S.CaptureKey + " para capturar uma área.", null);
        }

        void UpdateTrayText()
        {
            string t = "Capturo" + (S.CaptureKey.IsEmpty ? "" : " - " + S.CaptureKey);
            tray.Text = t.Length > 63 ? t.Substring(0, 63) : t;
        }

        void RefreshMenu()
        {
            miCapture.ShortcutKeyDisplayString = S.CaptureKey.IsEmpty ? "" : S.CaptureKey.ToString();
            miPin.ShortcutKeyDisplayString = S.PinKey.IsEmpty ? "" : S.PinKey.ToString();
            miFull.ShortcutKeyDisplayString = S.FullKey.IsEmpty ? "" : S.FullKey.ToString();
            miRepeat.ShortcutKeyDisplayString = S.RepeatKey.IsEmpty ? "" : S.RepeatKey.ToString();
            miRepeat.Enabled = !S.LastArea.IsEmpty;
            miGif.Text = GifRecorder.Current != null ? "Parar a gravação do GIF" : "Gravar GIF de uma área";
            miVideo.Text = VideoRecorder.Current != null ? "Parar a gravação do vídeo" : "Gravar vídeo de uma área";
            miScroll.Text = ScrollCapture.Current != null ? "Concluir o print com rolagem" : "Print com rolagem";
            miVideo.ShortcutKeyDisplayString = S.VideoKey.IsEmpty ? "" : S.VideoKey.ToString();
            miSaved.DropDownItems.Clear();
            foreach (var d in SavedRecordings())
            {
                string dir = d;
                DateTime when;
                string label = !DateTime.TryParseExact(Path.GetFileName(d), "yyyyMMdd_HHmmss", null, System.Globalization.DateTimeStyles.None, out when) ? Path.GetFileName(d)
                    : when.Date == DateTime.Today ? "hoje, " + when.ToString("HH:mm") : when.Date == DateTime.Today.AddDays(-1) ? "ontem, " + when.ToString("HH:mm") : when.ToString("dd/MM, HH:mm");
                miSaved.DropDownItems.Add("Gravação de " + label, null, (s, e) => OpenVideoEditor(dir));
            }
            miSaved.Visible = miSaved.DropDownItems.Count > 0;
            pins.RemoveAll(p => p.IsDisposed);
            miClosePins.Visible = pins.Count > 0;
            miClosePins.Text = "Fechar imagens fixadas (" + pins.Count + ")";
            RefreshHistory();
        }

        IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int vk = Marshal.ReadInt32(lParam);
                int msg = wParam.ToInt32();
                bool down = msg == 0x100 || msg == 0x104, up = msg == 0x101 || msg == 0x105;
                if (up && vk == swallowUpVk) { swallowUpVk = -1; return (IntPtr)1; }
                if (down && !Hotkey.IsModifierKey(vk) && vk != 0xE8)
                {
                    var hk = new Hotkey(Hotkey.CurrentMods(), vk);
                    var rec = Recorder;
                    if (rec != null)
                    {
                        invoker.BeginInvoke((Action)(() => rec(hk)));
                        return Swallow(hk);
                    }
                    if (!paused)
                    {
                        Action act = null;
                        if (hk.Same(S.CaptureKey)) act = CaptureArea;
                        else if (hk.Same(S.ScrollKey)) act = StartScrollPick;
                        else if (hk.Same(S.PinKey)) act = () => StartCapture(true);
                        else if (hk.Same(S.FullKey)) act = QuickFullScreen;
                        else if (hk.Same(S.RepeatKey)) act = RepeatLastArea;
                        else if (hk.Same(S.VideoKey)) act = () => StartCapture(false, Rectangle.Empty, false, true);
                        if (act != null) { invoker.BeginInvoke(act); return Swallow(hk); }
                    }
                }
            }
            return Native.CallNextHookEx(hook, nCode, wParam, lParam);
        }

        IntPtr Swallow(Hotkey hk)
        {
            swallowUpVk = hk.Vk;
            // Evita que soltar Win/Alt sozinho abra o menu Iniciar / a barra de menus.
            if ((hk.Mods & (Hotkey.Win | Hotkey.Alt)) != 0)
            {
                Native.keybd_event(0xE8, 0, 0, UIntPtr.Zero);
                Native.keybd_event(0xE8, 0, 2, UIntPtr.Zero);
            }
            return (IntPtr)1;
        }

        public void StartCapture(bool pinMode) { StartCapture(pinMode, Rectangle.Empty, false); }

        // preset: área já selecionada (coordenadas de tela). gif: Enter / duplo clique começam a gravar.
        public void StartCapture(bool pinMode, Rectangle preset, bool gif) { StartCapture(pinMode, preset, gif, false); }

        // Atalho "Capturar área" e clique no ícone: respeita o atraso das configurações
        // (durante uma gravação, o atalho encerra a gravação na hora).
        void CaptureArea()
        {
            if (S.CaptureDelay > 0 && GifRecorder.Current == null && VideoRecorder.Current == null && ScrollCapture.Current == null)
                DelayedCapture(S.CaptureDelay);
            else StartCapture(false);
        }

        // Escolher a área (ou clicar numa janela) para o print com rolagem.
        public void StartScrollPick()
        {
            if (ScrollCapture.Current != null) { ScrollCapture.Current.Stop(); return; }
            StartCapture(false, Rectangle.Empty, false, false, true);
        }

        // A sobreposição acabou de fechar: espera ela sumir e começa a rolar.
        public void StartScroll(Rectangle area)
        {
            var t = new System.Windows.Forms.Timer { Interval = 150 };
            t.Tick += (s, e) => { t.Dispose(); ScrollCapture.Start(this, area); };
            t.Start();
        }

        // Imagem longa pronta: abre na tela de edição (anotações, copiar, salvar, enviar...), no monitor
        // onde ela foi capturada. A imagem passa a ser da tela de edição.
        public void FinishScroll(Bitmap b, int parts, string why, Rectangle area)
        {
            if (why != null) Notify("Print com rolagem", why + " A imagem tem " + parts + (parts == 1 ? " parte." : " partes."), null);
            if (overlay != null && !overlay.IsDisposed) overlay.Close();
            try
            {
                overlay = new OverlayForm(this, b, Screen.FromRectangle(area).Bounds, false, true);
                overlay.FormClosed += (s, e) => { overlay = null; ScheduleTrim(); };
                overlay.Show();
            }
            catch (Exception ex) { Notify("Erro no print com rolagem", ex.Message, null); b.Dispose(); }
        }

        public void StartCapture(bool pinMode, Rectangle preset, bool gif, bool video) { StartCapture(pinMode, preset, gif, video, false); }

        public void StartCapture(bool pinMode, Rectangle preset, bool gif, bool video, bool scroll)
        {
            // durante a gravação, o atalho de captura (ou o ícone da bandeja) encerra o GIF ou o vídeo
            if (GifRecorder.Current != null) { GifRecorder.Current.Stop(false); return; }
            if (VideoRecorder.Current != null) { VideoRecorder.Current.Stop(false); return; }
            if (ScrollCapture.Current != null) { ScrollCapture.Current.Stop(); return; }
            if (overlay != null && !overlay.IsDisposed) return;
            // a tela é lida numa thread enquanto a janela da captura (ainda invisível) é criada
            Rectangle vs = SystemInformation.VirtualScreen;
            Bitmap shot = null; Exception grabError = null;
            var grab = new Thread(() =>
            {
                try { Rectangle v; shot = Services.CaptureVirtualScreen(out v); }
                catch (Exception ex) { grabError = ex; }
            }) { IsBackground = true, Name = "capturar-tela" };
            grab.Start();
            Log("captura " + vs + (pinMode ? " (fixar)" : ""));
            try
            {
                overlay = new OverlayForm(this, () =>
                {
                    grab.Join();
                    if (grabError != null) throw grabError;
                    return shot;
                }, vs, pinMode, false);
            }
            catch (Exception ex)
            {
                overlay = null;
                grab.Join();
                if (shot != null) shot.Dispose();
                Notify("Erro na captura", (grabError ?? ex).Message, null);
                return;
            }
            overlay.GifMode = gif;
            overlay.VideoMode = video;
            overlay.ScrollMode = scroll;
            if (!preset.IsEmpty) overlay.Preselect(preset);
            overlay.FormClosed += (s, e) => { overlay = null; ScheduleTrim(); };
            overlay.Show();
        }

        void RepeatLastArea()
        {
            if (S.LastArea.IsEmpty) { Notify("Nenhuma área ainda", "Faça uma captura primeiro; a área dela fica guardada.", null); return; }
            StartCapture(false, S.LastArea, false);
        }

        void DelayedCapture(int seconds)
        {
            if (countdown != null && !countdown.IsDisposed) return;
            countdown = new CountdownForm(seconds);
            countdown.Done += () => { countdown = null; StartCapture(false); };
            countdown.Show();
        }

        void ShowGallery()
        {
            if (gallery != null && !gallery.IsDisposed)
            {
                if (gallery.WindowState == FormWindowState.Minimized) gallery.WindowState = FormWindowState.Normal;
                gallery.Activate();
                return;
            }
            gallery = new GalleryForm(this);
            gallery.FormClosed += (s, e) => gallery = null;
            gallery.Show();
        }

        public void RunOnUi(Action a)
        {
            try { invoker.BeginInvoke(a); } catch { }
        }

        // A sobreposição acabou de fechar: espera um instante para ela sumir da tela antes de gravar.
        public void StartRecording(Rectangle area)
        {
            var t = new System.Windows.Forms.Timer { Interval = 150 };
            t.Tick += (s, e) => { t.Dispose(); GifRecorder.Start(this, area); };
            t.Start();
        }

        public void StartVideo(Rectangle area) { StartVideo(area, IntPtr.Zero); }

        // window: grava essa janela (Windows Graphics Capture) em vez do retângulo da tela
        public void StartVideo(Rectangle area, IntPtr window)
        {
            if (VideoRecorder.Current != null) return;
            if (preRec != null && !preRec.IsDisposed) return;
            // antes da contagem: painel com microfone, som do computador, webcam e contagem
            List<string> cams;
            try { cams = Webcam.Devices(); } catch { cams = new List<string>(); }
            var frame = new RecFrame(area) { Armed = true };
            var pre = new PreRecBar(area, cams)
            {
                MicOn = S.RecMic, SysOn = S.RecSys,
                CamOn = S.VidCam && cams.Count > 0,
                CamName = cams.Contains(S.VidCamName) ? S.VidCamName : (cams.Count > 0 ? cams[0] : null),
                Countdown = S.VidCountdown <= 0 ? 0 : S.VidCountdown <= 3 ? 3 : 5,
            };
            preRec = pre;
            pre.CancelClicked += () => { preRec = null; frame.Close(); };
            pre.StartClicked += () =>
            {
                preRec = null;
                frame.Close();
                S.RecMic = pre.MicOn; S.RecSys = pre.SysOn;
                if (cams.Count > 0) { S.VidCam = pre.CamOn; if (pre.CamName != null) S.VidCamName = pre.CamName; }
                S.VidCountdown = pre.Countdown;
                S.Save();
                BeginVideo(area, window);
            };
            frame.Show();
            pre.Show();
        }

        PreRecBar preRec;

        void BeginVideo(Rectangle area, IntPtr window)
        {
            // contagem regressiva (opcional) perto da área, depois começa
            if (S.VidCountdown > 0)
            {
                if (countdown != null && !countdown.IsDisposed) return;
                countdown = new CountdownForm(S.VidCountdown, "Gravando em", area);
                countdown.Done += () => { countdown = null; VideoRecorder.Start(this, area, window); };
                countdown.Show();
                return;
            }
            var t = new System.Windows.Forms.Timer { Interval = 150 };
            t.Tick += (s, e) => { t.Dispose(); VideoRecorder.Start(this, area, window); };
            t.Start();
        }

        readonly List<VideoEditorForm> editors = new List<VideoEditorForm>();

        // Gravações guardadas para abrir depois (fechou o editor sem exportar, ou o app fechou).
        static List<string> SavedRecordings()
        {
            var list = new List<string>();
            try
            {
                if (Directory.Exists(VideoRecorder.Root))
                    foreach (var d in Directory.GetDirectories(VideoRecorder.Root))
                        if (File.Exists(Path.Combine(d, "guardada.txt")) && File.Exists(Path.Combine(d, "sessao.txt"))) list.Add(d);
            }
            catch { }
            list.Sort((a, b) => string.CompareOrdinal(b, a));
            return list;
        }

        // Sem o editor: exporta direto em MP4 com o último estilo usado e copia o arquivo.
        public void QuickExportVideo(string dir)
        {
            Notify("Gerando o vídeo...", "Usando o último estilo do editor.", null);
            new Thread(() =>
            {
                string path = null, err = null;
                try
                {
                    var p = VideoProject.Load(dir);
                    p.Style.Format = 0;
                    path = Services.UniquePath(S.SaveFolder, "mp4");
                    try { VideoExport.Mp4(p, path, f => { }, () => false); }
                    finally { p.Dispose(); }
                }
                catch (Exception ex) { err = ex.Message; Log("exportar vídeo: " + ex); }
                RunOnUi(() =>
                {
                    if (err != null)
                    {
                        // não perde a gravação: fica guardada para abrir no editor
                        try { File.WriteAllText(Path.Combine(dir, "guardada.txt"), DateTime.Now.ToString("s")); } catch { }
                        Notify("Erro ao gerar o vídeo", err + " A gravação ficou guardada (bandeja → Gravações não exportadas).", null);
                        return;
                    }
                    try { Directory.Delete(dir, true); } catch { }
                    GifSaved(path, 0);
                });
            }) { IsBackground = true }.Start();
        }

        public void OpenVideoEditor(string dir)
        {
            try { File.Delete(Path.Combine(dir, "guardada.txt")); } catch { }
            VideoEditorForm f;
            try { f = new VideoEditorForm(this, dir); }
            catch (Exception ex) { Log("editor de vídeo: " + ex); Notify("Não foi possível abrir o editor", ex.Message, null); return; }
            editors.Add(f);
            f.FormClosed += (s, e) => { editors.Remove(f); ScheduleTrim(); };
            f.Show();
        }

        public void GifSaved(string path, long ms)
        {
            long kb = 0;
            try { kb = new FileInfo(path).Length / 1024; } catch { }
            try
            {
                var files = new System.Collections.Specialized.StringCollection();
                files.Add(path);
                Clipboard.SetFileDropList(files);   // cola como arquivo no chat, e-mail ou Explorer
            }
            catch { }
            string size = kb > 2048 ? (kb / 1024.0).ToString("0.0") + " MB" : kb + " KB";
            bool video = path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);
            Notify((video ? "Vídeo salvo · " : "GIF salvo · ") + (ms > 0 ? (ms / 1000.0).ToString("0.0") + " s · " : "") + size,
                "O arquivo foi copiado: é só colar no chat ou no e-mail. Clique para abrir.", path);
        }

        // Reconhece o texto da imagem: a janela abre na hora, com a imagem, e o texto chega em seguida.
        // A janela fica com a imagem (e a descarta ao fechar).
        // ---- libera buffers de desenho ociosos sem forçar pausas de GC ou paginação
        readonly System.Windows.Forms.Timer idleTrim = new System.Windows.Forms.Timer { Interval = 20000 };

        public void ScheduleTrim() { idleTrim.Stop(); idleTrim.Start(); }

        void TrimMemory()
        {
            idleTrim.Stop();
            // ocupado (capturando, gravando, editando): tenta de novo mais tarde
            if ((overlay != null && !overlay.IsDisposed) || VideoRecorder.Current != null || GifRecorder.Current != null || editors.Count > 0) { idleTrim.Start(); return; }
            BufferedGraphicsManager.Current.Invalidate();
            // O GC gerencia o heap. Coletas forçadas e esvaziar o working set
            // bloqueavam a interface e traziam páginas de volta no próximo uso.
        }

        public void CopyText(Bitmap bmp)
        {
            var form = new OcrTextForm(bmp);
            form.FormClosed += (s, e) => ScheduleTrim();
            form.LanguageRequested += lang => { form.SetReading(lang); ReadText(form, bmp, lang); };
            form.Show();
            Native.ForceForeground(form.Handle);
            ReadText(form, bmp, null);
        }

        void ReadText(OcrTextForm form, Bitmap bmp, string lang)
        {
            Bitmap copy;
            lock (bmp) copy = new Bitmap(bmp);   // a janela continua desenhando a original
            new Thread(() =>
            {
                OcrDoc doc = null; string err = null;
                try { doc = RunOcr(copy, lang); }
                catch (Exception ex) { err = ex is TypeLoadException || ex is FileNotFoundException ? "Este Windows não tem o reconhecimento de texto (requer Windows 10 ou 11)." : ex.Message; }
                finally { copy.Dispose(); }
                RunOnUi(() =>
                {
                    if (form.IsDisposed) return;
                    if (err != null) form.SetError(err); else form.SetResult(doc);
                });
            }) { IsBackground = true, Name = "ocr" }.Start();
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static OcrDoc RunOcr(Bitmap b, string lang) { return Ocr.Read(b, lang); }

        void QuickFullScreen()
        {
            if (overlay != null && !overlay.IsDisposed) return;
            try
            {
                Rectangle vs;
                using (var shot = Services.CaptureVirtualScreen(out vs))
                using (var b = new Bitmap(shot.Width, shot.Height, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(b)) g.DrawImage(shot, new Rectangle(0, 0, b.Width, b.Height));
                    string path = Services.UniquePath(S.SaveFolder, S.Format);
                    Services.SaveImage(b, path);
                    Notify("Tela inteira salva", path, path);
                }
            }
            catch (Exception ex) { Notify("Erro na captura", ex.Message, null); }
        }

        public void Pin(Bitmap bmp, Point screenLoc)
        {
            var f = new PinForm(this, bmp, screenLoc);
            pins.Add(f);
            f.FormClosed += (s, e) => { pins.Remove(f); ScheduleTrim(); };
            f.Show();
            Native.ForceForeground(f.Handle);
        }

        public void CopyImage(Bitmap bmp)
        {
            try { Services.SetClipboardImage(bmp); Notify("Copiado", "A captura está na área de transferência.", null); }
            catch (Exception ex) { Notify("Erro ao copiar", ex.Message, null); }
        }

        // Salva com o diálogo (ou direto na pasta, se configurado). Retorna false se o usuário cancelou.
        public bool SaveImage(Bitmap bmp, IWin32Window owner)
        {
            string path = null;
            if (S.QuickSave) path = Services.UniquePath(S.SaveFolder, S.Format);
            else
            {
                using (var dlg = new SaveFileDialog())
                {
                    Directory.CreateDirectory(S.SaveFolder);
                    dlg.InitialDirectory = S.SaveFolder;
                    dlg.FileName = Path.GetFileName(Services.UniquePath(S.SaveFolder, S.Format));
                    dlg.Filter = "PNG (*.png)|*.png|JPEG (*.jpg)|*.jpg|Bitmap (*.bmp)|*.bmp";
                    dlg.FilterIndex = S.Format == "jpg" ? 2 : 1;
                    dlg.AddExtension = true; dlg.OverwritePrompt = true;
                    if (dlg.ShowDialog(owner) != DialogResult.OK) return false;
                    path = dlg.FileName;
                    S.SaveFolder = Path.GetDirectoryName(path);
                    S.Save();
                }
            }
            try { Services.SaveImage(bmp, path); Notify("Captura salva", path, path); }
            catch (Exception ex) { Notify("Erro ao salvar", ex.Message, null); }
            return true;
        }

        public void PrintLater(Bitmap bmp)
        {
            invoker.BeginInvoke((Action)(() =>
            {
                try { Services.Print(bmp); }
                catch (Exception ex) { Notify("Erro ao imprimir", ex.Message, null); }
                finally { bmp.Dispose(); }
            }));
        }

        public void UploadAsync(Bitmap bmp, AfterUpload after, string shareTemplate)
        {
            byte[] png = Services.ToPng(bmp);
            bmp.Dispose();
            Notify("Enviando...", "Enviando a captura para a nuvem.", null);
            var t = new Thread(() =>
            {
                try
                {
                    string url = Services.Upload(png);
                    Settings.AddHistory(url);
                    invoker.BeginInvoke((Action)(() =>
                    {
                        try { Clipboard.SetText(url); } catch { }
                        string enc = Uri.EscapeDataString(url);
                        if (after == AfterUpload.GoogleLens)
                        {
                            Services.Open("https://lens.google.com/uploadbyurl?url=" + enc);
                            Notify("Buscando imagens semelhantes", "Link copiado: " + url, url);
                        }
                        else if (after == AfterUpload.Share)
                        {
                            Services.Open(string.Format(shareTemplate, enc));
                            Notify("Link copiado", url, url);
                        }
                        else
                        {
                            if (S.OpenAfterUpload) Services.Open(url);
                            Notify("Link copiado!", url, url);
                        }
                    }));
                }
                catch (Exception ex)
                {
                    invoker.BeginInvoke((Action)(() => Notify("Falha no envio", ex.Message, null)));
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        void RefreshHistory()
        {
            historyMenu.DropDownItems.Clear();
            var h = Settings.History(10);
            if (h.Count == 0) { historyMenu.DropDownItems.Add(new ToolStripMenuItem("(nenhum envio ainda)") { Enabled = false }); return; }
            foreach (var item in h)
            {
                string url = item[1];
                var mi = new ToolStripMenuItem(item[0] + "   " + url);
                mi.Click += (s, e) => { try { Clipboard.SetText(url); } catch { } Services.Open(url); };
                historyMenu.DropDownItems.Add(mi);
            }
        }

        bool settingsOpen;
        void ShowSettings()
        {
            if (settingsOpen) return;
            settingsOpen = true;
            paused = true;
            try
            {
                using (var f = new SettingsForm())
                    if (f.ShowDialog() == DialogResult.OK) UpdateTrayText();
            }
            finally { paused = false; Recorder = null; settingsOpen = false; }
        }

        public static void Log(string text)
        {
            try { Directory.CreateDirectory(Settings.Dir); File.AppendAllText(Path.Combine(Settings.Dir, "log.txt"), DateTime.Now.ToString("s") + " " + text + Environment.NewLine); } catch { }
        }

        public void Notify(string title, string text, string target)
        {
            Log(title + ": " + text);
            balloonTarget = target;
            tray.ShowBalloonTip(2500, title, text, ToolTipIcon.None);
        }

        protected override void ExitThreadCore()
        {
            if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook);
            if (GifRecorder.Current != null) GifRecorder.Current.Stop(true);
            if (VideoRecorder.Current != null) VideoRecorder.Current.Stop(true);
            if (ScrollCapture.Current != null) ScrollCapture.Current.Stop();
            foreach (var ed in editors.ToArray()) { ed.Quiet = true; ed.Close(); }
            S.Save();
            foreach (var p in pins.ToArray()) p.Close();
            tray.Visible = false;
            tray.Dispose();
            base.ExitThreadCore();
        }
    }

    static class Program
    {
        public static readonly EventWaitHandle CaptureSignal =
            new EventWaitHandle(false, EventResetMode.AutoReset, "Capturo_Capture_Signal");
        public static readonly EventWaitHandle SettingsSignal =
            new EventWaitHandle(false, EventResetMode.AutoReset, "Capturo_Settings_Signal");

        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--write-icon") { Art.WriteIco(args[1]); return; }

            try { if (!Native.SetProcessDpiAwarenessContext(new IntPtr(-4))) Native.SetProcessDPIAware(); }
            catch { try { Native.SetProcessDPIAware(); } catch { } }

            bool capture = args.Length == 1 && args[0] == "--capture";
            bool settings = args.Length == 1 && args[0] == "--settings";
            bool created;
            using (var mutex = new Mutex(true, "Capturo_SingleInstance_Mutex", out created))
            {
                if (!created && capture) { CaptureSignal.Set(); return; }
                if (!created && settings) { SettingsSignal.Set(); return; }
                if (!created)
                {
                    MessageBox.Show("O Capturo já está em execução (veja o ícone na bandeja).", "Capturo");
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                // janelas com buffer duplo (prévia e linha do tempo do editor, galeria...) reaproveitam o buffer
                // em vez de criar um novo a cada redesenho; ele é liberado quando o app fica ocioso
                BufferedGraphicsManager.Current.MaximumBuffer = new Size(2560, 1600);
                Application.ThreadException += (s, e) => { TrayApp.Log(e.Exception.ToString()); MessageBox.Show(e.Exception.ToString(), "Capturo - erro"); };
                Application.Run(new TrayApp());
            }
        }
    }
}
