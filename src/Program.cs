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
        public int IconScale = 100;                                  // % do tamanho das barras de ferramentas
        public Hotkey CaptureKey = new Hotkey(0, 0x2C);              // Print Screen
        public Hotkey FullKey = new Hotkey(Hotkey.Shift, 0x2C);      // Shift + Print Screen
        public Hotkey PinKey = new Hotkey(Hotkey.Ctrl, 0x2C);        // Ctrl + Print Screen

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
                            case "IconScale": int z; if (int.TryParse(v, out z)) s.IconScale = Math.Max(MinIcon, Math.Min(MaxIcon, z)); break;
                            case "CaptureKey": s.CaptureKey = Hotkey.Parse(v, s.CaptureKey); break;
                            case "FullKey": s.FullKey = Hotkey.Parse(v, s.FullKey); break;
                            case "PinKey": s.PinKey = Hotkey.Parse(v, s.PinKey); break;
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
                sb.AppendLine("IconScale=" + IconScale);
                sb.AppendLine("CaptureKey=" + CaptureKey.Serialize());
                sb.AppendLine("FullKey=" + FullKey.Serialize());
                sb.AppendLine("PinKey=" + PinKey.Serialize());
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
                    using (var g = Graphics.FromImage(rgb)) g.DrawImage(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
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
    enum Tool { None = 0, Pen = 1, Line = 2, Arrow = 3, Rect = 4, Marker = 5, Text = 6 }

    class Annot
    {
        public Tool Kind;
        public Color Color;
        public float Width;
        public List<PointF> Pts = new List<PointF>();
        public PointF A, B;
        public string Text = "";

        public Font MakeFont() { return new Font("Segoe UI", 14 + Width * 3, FontStyle.Bold, GraphicsUnit.Pixel); }

        public void Draw(Graphics g, bool editing, bool caretOn)
        {
            switch (Kind)
            {
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
        enum Drag { None, NewSel, Move, Resize, Draw }

        static readonly int[] ToolIds = { 1, 2, 3, 4, 5, 6, -1, 7, 8 };
        static readonly int[] ActIds = { 20, 21, 22, 23, -1, 27, 24, 25, -1, 26 };

        readonly TrayApp app;
        readonly Bitmap shot, dim;
        readonly Rectangle vs;
        readonly bool pinMode;
        Rectangle sel; bool hasSel;
        Drag drag = Drag.None; int handle = -1;
        Point dragStart; Rectangle selStart;
        Tool tool = Tool.None;
        Color color; int thick;
        readonly List<Annot> annots = new List<Annot>();
        readonly List<Annot> redo = new List<Annot>();
        Annot cur, editingText;
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
        readonly Font fontUi, fontUiB, fontSmall;

        public OverlayForm(TrayApp app, Bitmap shot, Rectangle vs, bool pinMode)
        {
            this.app = app; this.shot = shot; this.vs = vs; this.pinMode = pinMode;
            color = Color.FromArgb(TrayApp.S.ColorArgb);
            thick = TrayApp.S.Thickness;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Bounds = vs;
            TopMost = true;
            ShowInTaskbar = false;
            Text = "Capturo";
            Cursor = Cursors.Cross;
            KeyPreview = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);

            using (var g = CreateGraphics()) scale = Math.Max(1f, g.DpiX / 96f);
            fontUi = new Font("Segoe UI", 12 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            fontUiB = new Font("Segoe UI Semibold", 12 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            fontSmall = new Font("Consolas", 11 * scale, FontStyle.Regular, GraphicsUnit.Pixel);

            dim = new Bitmap(shot.Width, shot.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(dim))
            {
                g.DrawImage(shot, new Rectangle(0, 0, shot.Width, shot.Height), 0, 0, shot.Width, shot.Height, GraphicsUnit.Pixel);
                using (var br = new SolidBrush(Color.FromArgb(120, 8, 8, 16))) g.FillRectangle(br, 0, 0, shot.Width, shot.Height);
            }

            timer.Interval = 500;
            timer.Tick += (s, e) =>
            {
                if (flash != null && DateTime.Now > flashUntil) { flash = null; Invalidate(); }
                if (editingText != null) { caretOn = !caretOn; Invalidate(); }
            };
            timer.Start();
        }

        int S(float v) { return (int)Math.Round(v * scale); }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x02E0) return; // WM_DPICHANGED: a janela cobre todos os monitores, não redimensionar
            base.WndProc(ref m);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Bounds = vs;
            Native.ForceForeground(Handle);
            Activate();
            mouse = PointToClient(Cursor.Position);
            Invalidate();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Dispose();
            TrayApp.S.ColorArgb = color.ToArgb();
            TrayApp.S.Thickness = thick;
            TrayApp.S.Save();
            shot.Dispose(); dim.Dispose();
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
                case 5: return "Marcador"; case 6: return "Texto"; case 7: return "Cor"; case 8: return "Desfazer";
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

            int vlen = 0; foreach (int id in ToolIds) vlen += id < 0 ? sep : b;
            int vh = vlen + pad * 2, vw = b + pad * 2;
            int vx = sel.Right + gap;
            if (vx + vw > W) vx = sel.Left - gap - vw;
            if (vx < 0) vx = sel.Right - vw - gap;
            int vy = Math.Max(0, Math.Min(sel.Bottom - vh, H - vh));
            toolPanel = new Rectangle(vx, vy, vw, vh);
            int y = vy + pad;
            foreach (int id in ToolIds)
            {
                int len = id < 0 ? sep : b;
                btns.Add(new Btn { Id = id, R = new Rectangle(vx + pad, y, b, len) });
                y += len;
            }

            int hlen = 0; foreach (int id in ActIds) hlen += id < 0 ? sep : b;
            int hw = hlen + pad * 2, hh = b + pad * 2;
            int hy = sel.Bottom + gap;
            if (hy + hh > H) hy = sel.Top - gap - hh;
            if (hy < 0) hy = sel.Bottom - hh - gap;
            int hx = Math.Max(0, Math.Min(sel.Right - hw, W - hw));
            if (toolPanel.IntersectsWith(new Rectangle(hx, hy, hw, hh))) hx = Math.Max(0, vx - hw - gap);
            actPanel = new Rectangle(hx, hy, hw, hh);
            int x = hx + pad;
            foreach (int id in ActIds)
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
            int moreH = S(26);
            int w = cols * sw + (cols + 1) * g, h = 2 * sw + 3 * g + moreH;
            int x = toolPanel.Right + S(8);
            if (x + w > ClientSize.Width) x = toolPanel.Left - S(8) - w;
            int y = Math.Max(0, Math.Min(cb.R.Top - g, ClientSize.Height - h));
            paletteRect = new Rectangle(x, y, w, h);
            for (int i = 0; i < Art.Palette.Length; i++)
                swatchRects[i] = new Rectangle(x + g + (i % cols) * (sw + g), y + g + (i / cols) * (sw + g), sw, sw);
            moreRect = new Rectangle(x + g, y + 2 * sw + 3 * g - S(2), w - 2 * g, moreH - S(4));
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

        void Flash(string text) { flash = text; flashUntil = DateTime.Now.AddSeconds(1.2); Invalidate(); }

        // ---------- lupa
        bool ShowLoupe { get { return !hasSel || drag == Drag.NewSel || drag == Drag.Resize; } }

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
            Point p = e.Location;

            if (paletteOpen)
            {
                if (paletteRect.Contains(p))
                {
                    for (int i = 0; i < swatchRects.Length; i++)
                        if (swatchRects[i].Contains(p)) { SetColor(Art.Palette[i]); paletteOpen = false; Invalidate(); return; }
                    if (moreRect.Contains(p)) { paletteOpen = false; PickColorDialog(); }
                    return;
                }
                paletteOpen = false; Invalidate();
                if (HitBtn(p) == null || HitBtn(p).Id == 7) return;
            }

            if (e.Button == MouseButtons.Right)
            {
                FinishText();
                if (hasSel && annots.Count == 0) { hasSel = false; LayoutBars(); Invalidate(); }
                else if (!hasSel) Close();
                return;
            }
            if (e.Button != MouseButtons.Left) return;

            var bt = HitBtn(p);
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
                if (tool == Tool.Text)
                {
                    editingText = a; caretOn = true; annots.Add(a); redo.Clear();
                    Invalidate();
                    return;
                }
                a.Pts.Add(p);
                cur = a; drag = Drag.Draw;
                return;
            }
            if (tool == Tool.None && sel.Contains(p)) { drag = Drag.Move; dragStart = p; selStart = sel; return; }

            if (annots.Count == 0)
            {
                drag = Drag.NewSel; dragStart = p; sel = new Rectangle(p, Size.Empty);
                LayoutBars(); Invalidate();
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button == MouseButtons.Left && hasSel && tool == Tool.None && sel.Contains(e.Location) && HitBtn(e.Location) == null)
                DoAction(pinMode ? 27 : 24);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Point p = e.Location;
            Point old = mouse;
            mouse = p;
            bool shift = (ModifierKeys & Keys.Shift) != 0;

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
                case Drag.Draw:
                    if (cur.Kind == Tool.Pen || cur.Kind == Tool.Marker)
                    {
                        var last = cur.Pts[cur.Pts.Count - 1];
                        if (Math.Abs(last.X - p.X) + Math.Abs(last.Y - p.Y) >= 1) cur.Pts.Add(p);
                    }
                    else
                    {
                        PointF q = p;
                        if (shift) q = Constrain(cur.A, p, cur.Kind == Tool.Rect);
                        cur.B = q;
                    }
                    break;
                case Drag.None:
                    {
                        var bt = HitBtn(p);
                        int sw = -1; bool more = false;
                        if (paletteOpen)
                        {
                            for (int i = 0; i < swatchRects.Length; i++) if (swatchRects[i].Contains(p)) sw = i;
                            more = moreRect.Contains(p);
                        }
                        bool changed = bt != hover || sw != hoverSwatch || more != hoverMore;
                        hover = bt; hoverSwatch = sw; hoverMore = more;
                        int h = HitHandle(p);
                        if (bt != null || sw >= 0 || more) Cursor = Cursors.Hand;
                        else if (paletteOpen && paletteRect.Contains(p)) Cursor = Cursors.Default;
                        else if (h >= 0) Cursor = HandleCursors[h];
                        else if (hasSel && sel.Contains(p))
                            Cursor = tool == Tool.None ? Cursors.SizeAll : (tool == Tool.Text ? Cursors.IBeam : Cursors.Cross);
                        else Cursor = Cursors.Cross;

                        if (!hasSel)
                        {
                            // redesenha só a mira e a lupa (muito mais leve em telas grandes)
                            InvalidateCrosshair(old);
                            InvalidateCrosshair(p);
                            if (MonitorAt(old) != MonitorAt(p)) Invalidate();
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
            if (drag == Drag.NewSel && (sel.Width < 3 || sel.Height < 3)) hasSel = false;
            if (drag == Drag.Draw && cur != null) { annots.Add(cur); redo.Clear(); cur = null; }
            drag = Drag.None;
            LayoutBars();
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            thick = Math.Max(1, Math.Min(20, thick + (e.Delta > 0 ? 1 : -1)));
            if (editingText != null) editingText.Width = thick;
            Flash("Espessura  " + thick);
        }

        // ---------- teclado
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys k = keyData & Keys.KeyCode;
            if (k == Keys.Left || k == Keys.Right || k == Keys.Up || k == Keys.Down)
            {
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
                else Close();
                return;
            }
            if (e.Control)
            {
                e.SuppressKeyPress = true;
                switch (e.KeyCode)
                {
                    case Keys.C: if (hasSel) DoAction(24); else CopyPixelColor(); return;
                    case Keys.S: if (hasSel) DoAction(25); return;
                    case Keys.D: if (hasSel) DoAction(20); return;
                    case Keys.G: if (hasSel) DoAction(22); return;
                    case Keys.P: if (hasSel) DoAction(23); return;
                    case Keys.T: if (hasSel) DoAction(27); return;
                    case Keys.Z: Undo(); return;
                    case Keys.Y: if (redo.Count > 0) { annots.Add(redo[redo.Count - 1]); redo.RemoveAt(redo.Count - 1); Invalidate(); } return;
                    case Keys.A: FinishText(); sel = new Rectangle(Point.Empty, ClientSize); hasSel = true; LayoutBars(); Invalidate(); return;
                }
                e.SuppressKeyPress = false;
                return;
            }
            if (editingText != null) return; // digitação vai para OnKeyPress

            switch (e.KeyCode)
            {
                case Keys.Enter: if (hasSel) DoAction(pinMode ? 27 : 24); break;
                case Keys.P: SetTool(Tool.Pen); break;
                case Keys.L: SetTool(Tool.Line); break;
                case Keys.A: SetTool(Tool.Arrow); break;
                case Keys.R: SetTool(Tool.Rect); break;
                case Keys.M: SetTool(Tool.Marker); break;
                case Keys.T: SetTool(Tool.Text); break;
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
            if (editingText.Text.Trim().Length == 0) annots.Remove(editingText);
            editingText = null;
        }

        void Undo()
        {
            FinishText();
            if (annots.Count > 0) { redo.Add(annots[annots.Count - 1]); annots.RemoveAt(annots.Count - 1); }
            Invalidate();
        }

        void SetTool(Tool t)
        {
            if (!hasSel) return;
            FinishText();
            tool = tool == t ? Tool.None : t;
            Invalidate();
        }

        void SetColor(Color c)
        {
            color = c;
            if (editingText != null) editingText.Color = c;
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
            if (bt.Id >= 1 && bt.Id <= 6) SetTool((Tool)bt.Id);
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
                foreach (var a in annots) a.Draw(g, false, false);
            }
            return b;
        }

        void DoAction(int id)
        {
            if (!hasSel || sel.Width < 1 || sel.Height < 1) return;
            switch (id)
            {
                case 20: { var bmp = RenderFinal(); Close(); app.UploadAsync(bmp, TrayApp.AfterUpload.Link, null); break; }
                case 21: ShowShareMenu(); break;
                case 22: { var bmp = RenderFinal(); Close(); app.UploadAsync(bmp, TrayApp.AfterUpload.GoogleLens, null); break; }
                case 23: { var bmp = RenderFinal(); Close(); app.PrintLater(bmp); break; }
                case 24:
                    using (var bmp = RenderFinal()) { app.CopyImage(bmp); }
                    Close();
                    break;
                case 25:
                    {
                        var bmp = RenderFinal();
                        bool saved = app.SaveImage(bmp, this);
                        bmp.Dispose();
                        if (saved) Close(); else Native.ForceForeground(Handle);
                        break;
                    }
                case 26: Close(); break;
                case 27:
                    {
                        var bmp = RenderFinal();
                        var loc = new Point(sel.X + vs.X, sel.Y + vs.Y);
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
                        var bmp = RenderFinal(); Close();
                        app.UploadAsync(bmp, TrayApp.AfterUpload.Share, tpl);
                    });
                }
            }
            var b = btns.Find(x => x.Id == 21);
            shareMenu.Show(this, b != null ? new Point(b.R.Left, b.R.Bottom + S(6)) : mouse);
        }

        // ---------- pintura
        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            PaintCore(e);
        }

        void PaintCore(PaintEventArgs e)
        {
            var g = e.Graphics;
            var full = new Rectangle(0, 0, shot.Width, shot.Height);
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(dim, full, full, GraphicsUnit.Pixel);
            if (hasSel && sel.Width > 0 && sel.Height > 0) g.DrawImage(shot, sel, sel, GraphicsUnit.Pixel);
            g.CompositingMode = CompositingMode.SourceOver;
            g.PixelOffsetMode = PixelOffsetMode.Default;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            if (!hasSel || sel.Width == 0)
            {
                DrawCrosshair(g, mouse);
                DrawHint(g);
                DrawLoupe(g, mouse);
                return;
            }

            // anotações (recortadas à seleção)
            var st = g.Save();
            g.SetClip(sel);
            foreach (var a in annots) a.Draw(g, a == editingText, caretOn);
            if (cur != null) cur.Draw(g, false, false);
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

            if (ShowLoupe && drag != Drag.None) DrawLoupe(g, mouse);
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
            var mon = MonitorAt(mouse);
            // segmentos: texto normal e "teclas"
            object[][] segs = {
                new object[] { "Arraste para selecionar", false },
                new object[] { "Ctrl+A", true }, new object[] { "tela inteira", false },
                new object[] { "Ctrl+C", true }, new object[] { "copiar cor", false },
                new object[] { "Esc", true }, new object[] { "sair", false } };
            int padX = S(14), gap = S(8), kpad = S(6);
            float total = 0;
            foreach (var s in segs)
            {
                SizeF sz = g.MeasureString((string)s[0], fontUi);
                total += sz.Width + ((bool)s[1] ? kpad * 2 : 0) + gap;
            }
            int h = S(36);
            var r = new RectangleF(mon.X + (mon.Width - total - padX * 2) / 2f, mon.Y + S(24), total + padX * 2 - gap, h);
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

        Rectangle DrawPill(Graphics g, string text, Point at, Font f, Color bg, Color fg)
        {
            SizeF s = g.MeasureString(text, f);
            int px = S(9), py = S(4);
            var r = new Rectangle(at.X, at.Y, (int)s.Width + px * 2, (int)s.Height + py * 2);
            if (r.Right > ClientSize.Width) r.X = ClientSize.Width - r.Width - 2;
            if (r.Bottom > ClientSize.Height) r.Y = ClientSize.Height - r.Height - 2;
            if (r.X < 0) r.X = 0;
            Art.FillRound(g, bg, r, S(6));
            using (var b = new SolidBrush(fg)) g.DrawString(text, f, b, r.X + px, r.Y + py);
            return r;
        }

        void DrawTooltip(Graphics g, Btn b)
        {
            string name = TipName(b.Id), key = TipKey(b.Id);
            if (b.Id == 27 && pinMode) name += " (Enter)";
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
            DrawPanel(g, toolPanel);
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
                bool active = (b.Id >= 1 && b.Id <= 6 && (int)tool == b.Id) || (b.Id == 7 && paletteOpen);
                var r = b.R; r.Inflate(-S(2), -S(2));
                if (active) Art.FillRound(g, Color.FromArgb(40, Art.Accent), r, S(6));
                else if (b == hover) Art.FillRound(g, b.Id == 26 ? Color.FromArgb(255, 236, 236) : Color.FromArgb(240, 240, 245), r, S(6));
                Color fg = active ? Art.Accent : (b.Id == 26 ? Color.FromArgb(215, 50, 60) : (b.Id == 27 && pinMode ? Art.Accent : Art.Ink));
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
            ContextMenuStrip = m;

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
            if (e.Button == MouseButtons.Left) { dragging = true; dragOff = e.Location; }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging) { var c = Cursor.Position; Location = new Point(c.X - dragOff.X, c.Y - dragOff.Y); }
        }

        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = false; }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
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
            if (e.KeyCode == Keys.Escape) Close();
            else if (e.Control && e.KeyCode == Keys.C) app.CopyImage(img);
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

    class SettingsForm : Form
    {
        readonly TextBox folder;
        readonly ComboBox format;
        readonly CheckBox quick, openAfter, startup;
        readonly TrackBar iconSize;
        readonly HotkeyBox hkCapture, hkFull, hkPin;
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
            ClientSize = new Size(D(500), D(610));
            Icon = Art.AppIcon();
            TopMost = true;
            var S = TrayApp.S;

            // --- Atalhos
            var gk = new GroupBox { Text = "Atalhos de teclado", Location = P(12, 10), Size = new Size(D(476), D(168)) };
            Controls.Add(gk);
            problem = new Label { Location = P(14, 138), Size = new Size(D(450), D(20)), ForeColor = Color.FromArgb(200, 40, 40) };
            gk.Controls.Add(new Label { Text = "Clique no campo e pressione a combinação (Esc cancela, Backspace limpa).", Location = P(14, 22), AutoSize = true, ForeColor = SystemColors.GrayText });
            hkCapture = AddHotkeyRow(gk, "Capturar área", 46, S.CaptureKey);
            hkFull = AddHotkeyRow(gk, "Salvar tela inteira", 78, S.FullKey);
            hkPin = AddHotkeyRow(gk, "Capturar e fixar na tela", 110, S.PinKey);
            gk.Controls.Add(problem);

            // --- Salvamento
            var gs = new GroupBox { Text = "Salvar", Location = P(12, 188), Size = new Size(D(476), D(134)) };
            Controls.Add(gs);
            gs.Controls.Add(new Label { Text = "Pasta:", Location = P(14, 30), AutoSize = true });
            folder = new TextBox { Text = S.SaveFolder, Location = P(90, 26), Width = D(278) };
            var browse = new Button { Text = "Procurar...", Location = P(376, 24), Size = new Size(D(86), D(28)) };
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
            var gg = new GroupBox { Text = "Geral", Location = P(12, 332), Size = new Size(D(476), D(220)) };
            Controls.Add(gg);
            openAfter = new CheckBox { Text = "Abrir o link no navegador após enviar para a nuvem", Checked = S.OpenAfterUpload, Location = P(16, 28), AutoSize = true };
            startup = new CheckBox { Text = "Iniciar com o Windows", Checked = Settings.StartWithWindows, Location = P(16, 58), AutoSize = true };
            gg.Controls.Add(openAfter); gg.Controls.Add(startup);

            gg.Controls.Add(new Label { Text = "Tamanho dos ícones:", Location = P(14, 98), AutoSize = true });
            iconSize = new TrackBar
            {
                Minimum = Settings.MinIcon / 10, Maximum = Settings.MaxIcon / 10, SmallChange = 1, LargeChange = 2,
                TickFrequency = 1, Value = S.IconScale / 10, Location = P(150, 90), Size = new Size(D(210), D(34)), AutoSize = false
            };
            var iconPct = new Label { Location = P(364, 98), Size = new Size(D(46), D(20)), TextAlign = ContentAlignment.TopRight };
            var iconReset = new Button { Text = "100%", Location = P(414, 94), Size = new Size(D(50), D(26)), Font = new Font(Font.FontFamily, 10.5f * k, GraphicsUnit.Pixel) };
            var preview = new IconPreview { Location = P(14, 132), Size = new Size(D(448), D(76)), Dpi = k };
            EventHandler sync = (s, e) => { iconPct.Text = iconSize.Value * 10 + "%"; preview.Percent = iconSize.Value * 10; preview.Invalidate(); };
            iconSize.ValueChanged += sync;
            iconReset.Click += (s, e) => iconSize.Value = 10;
            sync(null, null);
            gg.Controls.Add(iconSize); gg.Controls.Add(iconPct); gg.Controls.Add(iconReset); gg.Controls.Add(preview);

            var ok = new Button { Text = "Salvar", Location = P(310, 566), Size = new Size(D(86), D(30)) };
            var cancel = new Button { Text = "Cancelar", Location = P(402, 566), Size = new Size(D(86), D(30)), DialogResult = DialogResult.Cancel };
            Controls.Add(ok); Controls.Add(cancel);
            CancelButton = cancel;
            ok.Click += (s, e) =>
            {
                var keys = new[] { hkCapture.Value, hkFull.Value, hkPin.Value };
                for (int i = 0; i < 3; i++)
                    for (int j = i + 1; j < 3; j++)
                        if (keys[i].Same(keys[j])) { problem.Text = "Dois comandos estão com o mesmo atalho (" + keys[i] + ")."; return; }
                S.CaptureKey = keys[0]; S.FullKey = keys[1]; S.PinKey = keys[2];
                S.SaveFolder = folder.Text.Trim().Length > 0 ? folder.Text.Trim() : S.SaveFolder;
                S.Format = (string)format.SelectedItem;
                S.QuickSave = quick.Checked;
                S.OpenAfterUpload = openAfter.Checked;
                S.IconScale = iconSize.Value * 10;
                try { Settings.StartWithWindows = startup.Checked; } catch { }
                S.Save();
                DialogResult = DialogResult.OK;
            };
            Deactivate += (s, e) => { hkCapture.StopRecording(); hkFull.StopRecording(); hkPin.StopRecording(); };
            Shown += (s, e) => { Native.ForceForeground(Handle); cancel.Focus(); };
        }

        HotkeyBox AddHotkeyRow(GroupBox parent, string label, int y, Hotkey value)
        {
            parent.Controls.Add(new Label { Text = label, Location = P(14, y + 4), AutoSize = true });
            var box = new HotkeyBox { Location = P(170, y), Width = D(214) };
            box.Value = value;
            box.Problem += msg => problem.Text = msg ?? "";
            var clear = new Button { Text = "Limpar", Location = P(390, y - 1), Size = new Size(D(72), D(28)) };
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
        readonly ToolStripMenuItem historyMenu, miCapture, miFull, miPin, miClosePins;
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
            historyMenu = new ToolStripMenuItem("Envios recentes");
            menu.Items.Add(miCapture);
            menu.Items.Add(miPin);
            menu.Items.Add(miFull);
            menu.Items.Add(miClosePins);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(historyMenu);
            menu.Items.Add("Abrir pasta de capturas", null, (s, e) => { Directory.CreateDirectory(S.SaveFolder); Services.Open(S.SaveFolder); });
            menu.Items.Add("Configurações...", null, (s, e) => ShowSettings());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Sair", null, (s, e) => ExitThread());
            menu.Opening += (s, e) => RefreshMenu();

            tray = new NotifyIcon { Icon = Art.AppIcon(), ContextMenuStrip = menu, Visible = true };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) StartCapture(false); };
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
                        if (hk.Same(S.CaptureKey)) act = () => StartCapture(false);
                        else if (hk.Same(S.PinKey)) act = () => StartCapture(true);
                        else if (hk.Same(S.FullKey)) act = QuickFullScreen;
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

        public void StartCapture(bool pinMode)
        {
            if (overlay != null && !overlay.IsDisposed) return;
            Rectangle vs;
            Bitmap shot;
            try { shot = Services.CaptureVirtualScreen(out vs); }
            catch (Exception ex) { Notify("Erro na captura", ex.Message, null); return; }
            Log("captura " + vs + (pinMode ? " (fixar)" : ""));
            overlay = new OverlayForm(this, shot, vs, pinMode);
            overlay.FormClosed += (s, e) => { overlay = null; };
            overlay.Show();
        }

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
            f.FormClosed += (s, e) => pins.Remove(f);
            f.Show();
            Native.ForceForeground(f.Handle);
        }

        public void CopyImage(Bitmap bmp)
        {
            try { Clipboard.SetImage(bmp); Notify("Copiado", "A captura está na área de transferência.", null); }
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
                Application.ThreadException += (s, e) => { TrayApp.Log(e.Exception.ToString()); MessageBox.Show(e.Exception.ToString(), "Capturo - erro"); };
                Application.Run(new TrayApp());
            }
        }
    }
}
