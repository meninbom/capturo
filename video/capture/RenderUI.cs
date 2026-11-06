// Renderiza telas reais do Capturo fora da tela: carrega dist\Capturo.exe e chama o próprio
// código de desenho do app (OverlayForm.PaintCore, RenderFinal e PinForm.OnPaint) via reflexão.
// Uso: RenderUI.exe <Capturo.exe> <bug.png> <saída>
using System;
using System.Collections;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

static class RenderUI
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    const BindingFlags F = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    static Assembly asm;
    static Type T(string n) { return asm.GetType("Capturo." + n, true); }
    static void Set(object o, string f, object v) { o.GetType().GetField(f, F).SetValue(o, v); }
    static object Get(object o, string f) { return o.GetType().GetField(f, F).GetValue(o); }
    static object Call(object o, string m, params object[] a) { return o.GetType().GetMethod(m, F).Invoke(o, a); }

    static object overlay; static Bitmap shot; static string outDir;
    static Type toolT, dragT, annotT;

    [STAThread]
    static int Main(string[] args)
    {
        SetProcessDPIAware();
        asm = Assembly.LoadFrom(args[0]);
        shot = new Bitmap(args[1]);
        outDir = args[2];
        Directory.CreateDirectory(Path.Combine(outDir, "rec"));
        toolT = T("Tool"); annotT = T("Annot"); dragT = T("OverlayForm").GetNestedType("Drag", F);

        // Configurações: cor vermelha padrão, traço 4, ícones 100%
        var settings = T("Settings").GetMethod("Load", F).Invoke(null, null);
        Set(settings, "ColorArgb", Color.FromArgb(255, 235, 30, 50).ToArgb());
        Set(settings, "Thickness", 4);
        Set(settings, "IconScale", 100);
        T("TrayApp").GetField("S", F).SetValue(null, settings);

        overlay = Activator.CreateInstance(T("OverlayForm"), F, null,
            new object[] { null, shot, new Rectangle(0, 0, shot.Width, shot.Height), false }, null);
        var o = overlay;

        Rectangle selFinal = Rectangle.FromLTRB(1150, 440, 1846, 712);
        PointF arrowA = new PointF(1436, 482), arrowB = new PointF(1708, 474);
        Point textAt = new Point(1262, 455);
        string text = "aqui quebra";

        // botões (depois do layout)
        Func<int, Rectangle> btnRect = id =>
        {
            foreach (var b in (IList)Get(o, "btns")) if ((int)Get(b, "Id") == id) return (Rectangle)Get(b, "R");
            return Rectangle.Empty;
        };
        Func<int, object> btn = id =>
        {
            foreach (var b in (IList)Get(o, "btns")) if ((int)Get(b, "Id") == id) return b;
            return null;
        };
        Func<Rectangle, Point> center = r => new Point(r.X + r.Width / 2, r.Y + r.Height / 2);

        // Pré-calcula as posições dos botões
        Set(o, "sel", selFinal); Set(o, "hasSel", true); Call(o, "LayoutBars");
        Point bArrow = center(btnRect(3)), bText = center(btnRect(6)), bCopy = center(btnRect(24)), bPin = center(btnRect(27));
        Set(o, "hasSel", false); Set(o, "sel", Rectangle.Empty); Call(o, "LayoutBars");

        object annArrow = null, annText = null;
        int typed = 0;
        int N = 246;
        for (int f = 0; f < N; f++)
        {
            Point mouse; Cursor cur = Cursors.Cross;
            Set(o, "hover", null);
            if (f < 24)
            {
                mouse = Lerp(new Point(1330, 650), selFinal.Location, Ease(f / 24f));
            }
            else if (f < 54)
            {
                float k = Ease((f - 24) / 30f);
                var r = Rectangle.FromLTRB(selFinal.Left, selFinal.Top,
                    (int)(selFinal.Left + (selFinal.Right - selFinal.Left) * k) + 1, (int)(selFinal.Top + (selFinal.Bottom - selFinal.Top) * k) + 1);
                Set(o, "hasSel", true); Set(o, "sel", r); Set(o, "drag", Enum.ToObject(dragT, 1)); // NewSel
                mouse = new Point(r.Right, r.Bottom);
            }
            else
            {
                if (f == 54) { Set(o, "sel", selFinal); Set(o, "drag", Enum.ToObject(dragT, 0)); Call(o, "LayoutBars"); }
                if (f < 60) mouse = new Point(selFinal.Right, selFinal.Bottom);
                else if (f < 80) { mouse = Lerp(new Point(selFinal.Right, selFinal.Bottom), bArrow, Ease((f - 60) / 20f)); cur = Cursors.Default; }
                else if (f < 86) { mouse = bArrow; cur = Cursors.Default; }
                else if (f < 100) mouse = Lerp(bArrow, Point.Round(arrowA), Ease((f - 86) / 14f));
                else if (f < 124)
                {
                    float k = Ease((f - 100) / 24f);
                    var b = new PointF(arrowA.X + (arrowB.X - arrowA.X) * k, arrowA.Y + (arrowB.Y - arrowA.Y) * k);
                    if (annArrow == null) annArrow = MakeAnnot(3, arrowA, arrowA, "");
                    Set(annArrow, "B", b); Set(o, "cur", annArrow); Set(o, "drag", Enum.ToObject(dragT, 4)); // Draw
                    mouse = Point.Round(b);
                }
                else if (f < 140)
                {
                    if (f == 124) { Set(annArrow, "B", arrowB); Set(o, "cur", null); Set(o, "drag", Enum.ToObject(dragT, 0)); ((IList)Get(o, "annots")).Add(annArrow); }
                    mouse = Lerp(Point.Round(arrowB), bText, Ease((f - 124) / 16f)); cur = Cursors.Default;
                    if (f > 130) cur = Cursors.Default;
                }
                else if (f < 156) { mouse = Lerp(bText, new Point(textAt.X + 6, textAt.Y + 16), Ease((f - 140) / 16f)); cur = f < 146 ? Cursors.Default : Cursors.IBeam; }
                else if (f < 212)
                {
                    if (f == 156)
                    {
                        annText = MakeAnnot(6, textAt, textAt, "");
                        ((IList)Get(o, "annots")).Add(annText); Set(o, "editingText", annText); Set(o, "caretOn", true);
                    }
                    int want = Math.Min(text.Length, Math.Max(0, (f - 160) / 3 + 1));
                    if (want != typed) { typed = want; Set(annText, "Text", text.Substring(0, typed)); }
                    Set(o, "caretOn", ((f - 156) / 15) % 2 == 0 || typed < text.Length);
                    mouse = new Point(textAt.X + 6, textAt.Y + 16); cur = Cursors.IBeam;
                }
                else
                {
                    if (f == 212) Set(o, "editingText", null);
                    mouse = Lerp(new Point(textAt.X + 6, textAt.Y + 16), bCopy, Ease(Math.Min(1f, (f - 212) / 18f))); cur = Cursors.Default;
                }
                // estado de ferramenta / hover
                if (f >= 80) Set(o, "tool", Enum.ToObject(toolT, f >= 140 ? 6 : 3));
                if (f >= 60 && f < 86 && Near(mouse, btnRect(3))) Set(o, "hover", btn(3));
                if (f >= 124 && f < 146 && Near(mouse, btnRect(6))) Set(o, "hover", btn(6));
                if (f >= 212 && Near(mouse, btnRect(24))) Set(o, "hover", btn(24));
            }
            Set(o, "mouse", mouse);
            using (var bmp = Paint())
            {
                DrawCursor(bmp, cur, mouse);
                bmp.Save(Path.Combine(outDir, "rec", string.Format("f{0:D4}.png", f)), ImageFormat.Png);
            }
            if (f == 0) SaveStill("overlay-loupe.png");
        }
        SaveStill("overlay-annotated.png");

        // Imagem final copiada (Ctrl+C) = RenderFinal()
        var final = (Bitmap)Call(o, "RenderFinal");
        final.Save(Path.Combine(outDir, "clip.png"), ImageFormat.Png);

        // Imagem fixada (PinForm) com o aviso real de "Sempre visível: ligado"
        var pin = (Form)Activator.CreateInstance(T("PinForm"), F, null, new object[] { null, final, new Point(0, 0) }, null);
        Call(pin, "Flash", "Sempre visível: ligado");
        SavePin(pin, final.Size, "pin-flash.png");
        Set(pin, "flash", null);
        SavePin(pin, final.Size, "pin.png");

        // Menu de contexto real da imagem fixada
        var menu = pin.ContextMenuStrip;
        menu.PerformLayout();
        var ms = menu.GetPreferredSize(Size.Empty);
        menu.Size = ms;
        using (var mb = new Bitmap(ms.Width, ms.Height))
        {
            menu.DrawToBitmap(mb, new Rectangle(Point.Empty, ms));
            mb.Save(Path.Combine(outDir, "pin-menu.png"), ImageFormat.Png);
        }
        Console.WriteLine("arrow btn " + bArrow + " text " + bText + " copy " + bCopy + " pin " + bPin + " clip " + final.Size + " menu " + ms);
        Console.WriteLine("toolPanel " + Get(o, "toolPanel") + " actPanel " + Get(o, "actPanel"));
        return 0;
    }

    static void SaveStill(string name)
    {
        using (var b = Paint()) b.Save(Path.Combine(outDir, name), ImageFormat.Png);
    }

    static void SavePin(Form pin, Size sz, string name)
    {
        using (var b = new Bitmap(sz.Width, sz.Height))
        using (var g = Graphics.FromImage(b))
        {
            pin.GetType().GetMethod("OnPaint", F).Invoke(pin, new object[] { new PaintEventArgs(g, new Rectangle(Point.Empty, sz)) });
            b.Save(Path.Combine(outDir, name), ImageFormat.Png);
        }
    }

    static Bitmap Paint()
    {
        var b = new Bitmap(shot.Width, shot.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(b))
            overlay.GetType().GetMethod("PaintCore", F).Invoke(overlay, new object[] { new PaintEventArgs(g, new Rectangle(0, 0, b.Width, b.Height)) });
        return b;
    }

    static object MakeAnnot(int kind, PointF a, PointF b, string text)
    {
        var x = Activator.CreateInstance(annotT, true);
        Set(x, "Kind", Enum.ToObject(toolT, kind));
        Set(x, "Color", Color.FromArgb(255, 235, 30, 50));
        Set(x, "Width", 4f);
        Set(x, "A", a); Set(x, "B", b); Set(x, "Text", text);
        return x;
    }

    // cursor desenhado (o cursor do sistema não é renderizado fora da tela), ~1,6x
    static void DrawCursor(Bitmap bmp, Cursor c, Point p)
    {
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float z = 1.9f;
            if (c == Cursors.Default)
            {
                PointF[] pts = { new PointF(0,0), new PointF(0,17), new PointF(4,13), new PointF(7,20), new PointF(10,19), new PointF(7,12), new PointF(12,12) };
                for (int i = 0; i < pts.Length; i++) pts[i] = new PointF(p.X + pts[i].X * z, p.Y + pts[i].Y * z);
                using (var path = new GraphicsPath()) { path.AddPolygon(pts); g.FillPath(Brushes.White, path); using (var pen = new Pen(Color.Black, 1.4f * z / 1.6f)) { pen.LineJoin = LineJoin.Round; g.DrawPath(pen, path); } }
            }
            else if (c == Cursors.IBeam)
            {
                using (var w = new Pen(Color.White, 4.5f)) using (var k = new Pen(Color.Black, 2f))
                {
                    float h = 11 * z, a = 3.5f * z;
                    foreach (var pen in new[] { w, k })
                    {
                        g.DrawLine(pen, p.X, p.Y - h, p.X, p.Y + h);
                        g.DrawLine(pen, p.X - a, p.Y - h, p.X + a, p.Y - h);
                        g.DrawLine(pen, p.X - a, p.Y + h, p.X + a, p.Y + h);
                    }
                }
            }
            else
            {
                using (var w = new Pen(Color.White, 4.5f)) using (var k = new Pen(Color.Black, 2f))
                {
                    float h = 10 * z;
                    foreach (var pen in new[] { w, k })
                    {
                        g.DrawLine(pen, p.X - h, p.Y, p.X - 3, p.Y); g.DrawLine(pen, p.X + 3, p.Y, p.X + h, p.Y);
                        g.DrawLine(pen, p.X, p.Y - h, p.X, p.Y - 3); g.DrawLine(pen, p.X, p.Y + 3, p.X, p.Y + h);
                    }
                }
            }
        }
    }

    static bool Near(Point p, Rectangle r) { return r.Contains(p); }
    static float Ease(float t) { t = Math.Max(0, Math.Min(1, t)); return t < 0.5f ? 4 * t * t * t : 1 - (float)Math.Pow(-2 * t + 2, 3) / 2; }
    static Point Lerp(Point a, Point b, float k) { return new Point((int)Math.Round(a.X + (b.X - a.X) * k), (int)Math.Round(a.Y + (b.Y - a.Y) * k)); }
}
