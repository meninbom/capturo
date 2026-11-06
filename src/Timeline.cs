// Linha do tempo do editor de vídeo: régua, faixa de zoom (trechos editáveis), faixa do vídeo
// com miniaturas e alças de corte, marcas de clique e cursor de reprodução.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace Capturo
{
    class Timeline : Control
    {
        public event Action<int> Seek;
        public event Action Changed, Commit;
        public event Action<ZoomRegion> SelectionChanged;
        public event Action<SpeedRegion> SpeedSelectionChanged;
        readonly VideoEditorForm f;
        enum DragKind { None, Seek, TrimStart, TrimEnd, ZoomMove, ZoomLeft, ZoomRight, SpeedMove, SpeedLeft, SpeedRight }
        DragKind drag;
        ZoomRegion dragZoom;
        SpeedRegion dragSpeed;
        static readonly Color SpeedColor = Color.FromArgb(14, 150, 128), SpeedSlow = Color.FromArgb(196, 120, 24);
        int dragOffset, dragStartX;
        bool dragMoved;
        int hoverX = -1;
        readonly Dictionary<int, Image> thumbs = new Dictionary<int, Image>();
        int thumbW;
        readonly ContextMenuStrip menu = new ContextMenuStrip();

        public Timeline(VideoEditorForm f)
        {
            this.f = f;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(24, 24, 31);
        }

        VideoProject P { get { return f.Project; } }
        int D(float v) { return Ui.D(v); }
        int X0 { get { return D(20); } }
        int X1 { get { return Width - D(20); } }
        Rectangle Ruler { get { return new Rectangle(X0, 0, X1 - X0, D(26)); } }
        Rectangle ZoomRow { get { return new Rectangle(X0, D(30), X1 - X0, D(28)); } }
        Rectangle SpeedRow { get { return new Rectangle(X0, D(62), X1 - X0, D(24)); } }
        Rectangle VideoRow { get { return new Rectangle(X0, D(92), X1 - X0, D(56)); } }

        RectangleF SpeedRect(SpeedRegion z)
        {
            var sr = SpeedRow;
            return new RectangleF(XOf(z.Start), sr.Y, Math.Max(D(8), XOf(z.End) - XOf(z.Start)), sr.Height);
        }

        SpeedRegion SpeedAt(Point pt, out int edge)
        {
            edge = 0;
            if (!SpeedRow.Contains(pt)) return null;
            foreach (var z in P.Speeds)
            {
                var r = SpeedRect(z);
                if (pt.X < r.Left - D(4) || pt.X > r.Right + D(4)) continue;
                edge = Math.Abs(pt.X - r.Left) <= D(7) ? 1 : Math.Abs(pt.X - r.Right) <= D(7) ? 2 : 3;
                return z;
            }
            return null;
        }

        float XOf(int t) { return X0 + (X1 - X0) * (t / (float)Math.Max(1, P.Duration)); }
        int TOf(int x) { return (int)Math.Round((x - X0) / (float)Math.Max(1, X1 - X0) * P.Duration); }

        protected override void OnResize(EventArgs e) { base.OnResize(e); ClearThumbs(); }
        void ClearThumbs() { foreach (var t in thumbs.Values) if (t != null) t.Dispose(); thumbs.Clear(); }

        RectangleF ZoomRect(ZoomRegion z)
        {
            var zr = ZoomRow;
            return new RectangleF(XOf(z.Start), zr.Y, Math.Max(D(8), XOf(z.End) - XOf(z.Start)), zr.Height);
        }

        ZoomRegion ZoomAt(Point pt, out int edge)
        {
            edge = 0;
            if (!ZoomRow.Contains(pt)) return null;
            foreach (var z in P.Zooms)
            {
                var r = ZoomRect(z);
                if (pt.X < r.Left - D(4) || pt.X > r.Right + D(4)) continue;
                edge = Math.Abs(pt.X - r.Left) <= D(7) ? 1 : Math.Abs(pt.X - r.Right) <= D(7) ? 2 : 3;
                return z;
            }
            return null;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var p = P;
            var vr = VideoRow;
            var zr = ZoomRow;

            // régua
            using (var pen = new Pen(Color.FromArgb(70, 70, 84)))
            {
                int step = p.Duration <= 10000 ? 1000 : p.Duration <= 30000 ? 2000 : p.Duration <= 90000 ? 5000 : p.Duration <= 300000 ? 15000 : 60000;
                for (int t = 0; t <= p.Duration; t += step)
                {
                    float x = XOf(t);
                    g.DrawLine(pen, x, D(18), x, D(24));
                    Ui.Draw(g, (t / 60000) + ":" + (t / 1000 % 60).ToString("00"), Ui.FontS, Ui.Text3, new Rectangle((int)x + D(3), D(2), D(50), D(16)), ContentAlignment.MiddleLeft);
                }
                for (int t = step / 2; t <= p.Duration; t += step) { float x = XOf(t); g.DrawLine(pen, x, D(21), x, D(24)); }
            }

            // faixa de zoom
            Art.FillRound(g, Color.FromArgb(32, 32, 41), zr, D(7));
            if (p.Zooms.Count == 0)
                Ui.Draw(g, "Duplo clique aqui (ou Z) para criar um zoom", Ui.Font, Ui.Text3, zr, ContentAlignment.MiddleCenter);
            foreach (var z in p.Zooms)
            {
                var r = ZoomRect(z);
                bool sel = z == f.Selected, on = p.Style.Zoom;
                // automáticos: pelos cliques (cor forte) ou pelo movimento do cursor (mais clara); à mão: roxo
                Color fill = !on ? Color.FromArgb(60, 60, 72) : sel ? Ui.AccentHi : z.Auto ? (z.Motion ? Color.FromArgb(70, 128, 220) : Ui.Accent) : Color.FromArgb(98, 76, 214);
                using (var path = Art.RoundRect(r, D(6)))
                {
                    using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                    if (sel) using (var pen = new Pen(Color.White, D(2))) g.DrawPath(pen, path);
                }
                if (r.Width > D(60))
                    Ui.Draw(g, "⌕ " + z.Depth.ToString("0.##") + "×" + (z.Manual ? " · fixo" : ""), Ui.FontS, on ? Color.White : Ui.Text2,
                        Rectangle.Round(new RectangleF(r.X + D(8), r.Y, r.Width - D(16), r.Height)), ContentAlignment.MiddleLeft);
                if (sel || r.Contains(PointToClient(MousePosition)))
                    using (var b = new SolidBrush(Color.FromArgb(220, 255, 255, 255)))
                    {
                        g.FillRectangle(b, r.Left + D(3), r.Y + r.Height / 2 - D(6), D(2), D(12));
                        g.FillRectangle(b, r.Right - D(5), r.Y + r.Height / 2 - D(6), D(2), D(12));
                    }
            }

            // faixa de velocidade
            var spr = SpeedRow;
            Art.FillRound(g, Color.FromArgb(30, 30, 38), spr, D(6));
            if (p.Speeds.Count == 0)
                Ui.Draw(g, "Velocidade: duplo clique aqui (ou V) para acelerar um trecho", Ui.FontS, Ui.Text3, spr, ContentAlignment.MiddleCenter);
            foreach (var z in p.Speeds)
            {
                var r = SpeedRect(z);
                bool sel = z == f.SelectedSpeed;
                Color fill = z.Rate >= 1 ? SpeedColor : SpeedSlow;
                if (sel) fill = ControlPaint.Light(fill, 0.35f);
                using (var path = Art.RoundRect(r, D(5)))
                {
                    using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                    if (sel) using (var pen = new Pen(Color.White, D(2))) g.DrawPath(pen, path);
                }
                if (r.Width > D(34))
                    Ui.Draw(g, (z.Rate >= 1 ? "» " : "‹ ") + SpeedRegion.Name(z.Rate), Ui.FontS, Color.White,
                        Rectangle.Round(new RectangleF(r.X + D(6), r.Y, r.Width - D(12), r.Height)), ContentAlignment.MiddleLeft);
                if (sel || r.Contains(PointToClient(MousePosition)))
                    using (var b = new SolidBrush(Color.FromArgb(220, 255, 255, 255)))
                    {
                        g.FillRectangle(b, r.Left + D(3), r.Y + r.Height / 2 - D(5), D(2), D(10));
                        g.FillRectangle(b, r.Right - D(5), r.Y + r.Height / 2 - D(5), D(2), D(10));
                    }
            }

            // miniaturas do vídeo
            Art.FillRound(g, Color.FromArgb(40, 40, 50), vr, D(7));
            float aspect = p.W / (float)p.H;
            thumbW = Math.Max(D(40), (int)(vr.Height * aspect));
            var clip = g.Clip;
            using (var rp = Art.RoundRect(vr, D(7))) g.SetClip(rp);
            g.InterpolationMode = InterpolationMode.Bilinear;
            for (int x = vr.X, i = 0; x < vr.Right; x += thumbW, i++)
            {
                Image img;
                if (!thumbs.TryGetValue(i, out img) || img == null)
                {
                    img = f.Cache.Thumb(TOf(x + thumbW / 2));
                    if (img != null) thumbs[i] = img;
                }
                if (img != null) g.DrawImage(img, new Rectangle(x, vr.Y, thumbW, vr.Height));
            }
            using (var dim = new SolidBrush(Color.FromArgb(185, 16, 16, 20)))
            {
                g.FillRectangle(dim, vr.X, vr.Y, XOf(p.TrimStart) - vr.X, vr.Height);
                g.FillRectangle(dim, XOf(p.TrimEnd), vr.Y, vr.Right - XOf(p.TrimEnd), vr.Height);
            }
            if (!f.Cache.Done)
                using (var ld = new SolidBrush(Color.FromArgb(130, 24, 24, 31)))
                    g.FillRectangle(ld, XOf(f.Cache.LoadedMs), vr.Y, vr.Right - XOf(f.Cache.LoadedMs), vr.Height);
            g.Clip = clip;
            clip.Dispose();

            // corte
            float xs = XOf(p.TrimStart), xe = XOf(p.TrimEnd);
            using (var pen = new Pen(Ui.Trim, D(2.5f))) g.DrawRectangle(pen, xs, vr.Y, xe - xs, vr.Height);
            foreach (float hx in new[] { xs, xe })
            {
                var hr = new RectangleF(hx - D(6), vr.Y - D(3), D(12), vr.Height + D(6));
                using (var path = Art.RoundRect(hr, D(4))) using (var b = new SolidBrush(Ui.Trim)) g.FillPath(b, path);
                using (var pen = new Pen(Color.FromArgb(140, 90, 60, 0), D(1.4f)))
                    g.DrawLine(pen, hx, vr.Y + vr.Height / 2 - D(8), hx, vr.Y + vr.Height / 2 + D(8));
            }

            // cliques
            using (var cb = new SolidBrush(Color.FromArgb(220, 230, 230, 240)))
                foreach (var c in p.Clicks)
                    if (p.InFrame(c.X, c.Y)) g.FillEllipse(cb, XOf(c.T) - D(2.5f), vr.Bottom + D(8), D(5), D(5));
            if (p.Clicks.Length > 0)
                Ui.Draw(g, "cliques", Ui.FontS, Ui.Text3, new Rectangle(X0, vr.Bottom + D(4), D(60), D(14)), ContentAlignment.MiddleLeft);

            // posição sob o mouse
            if (hoverX >= X0 && hoverX <= X1 && drag == DragKind.None)
                using (var pen = new Pen(Color.FromArgb(70, 255, 255, 255))) g.DrawLine(pen, hoverX, zr.Y, hoverX, vr.Bottom);

            // cursor de reprodução
            float px = XOf(f.Position);
            using (var pen = new Pen(Color.White, D(2))) g.DrawLine(pen, px, D(14), px, vr.Bottom + D(4));
            using (var path = new GraphicsPath())
            {
                path.AddPolygon(new[] { new PointF(px - D(7), D(4)), new PointF(px + D(7), D(4)), new PointF(px + D(7), D(12)), new PointF(px, D(19)), new PointF(px - D(7), D(12)) });
                g.FillPath(Brushes.White, path);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            var p = P;
            if (e.Button == MouseButtons.Right)
            {
                int ed;
                var z = ZoomAt(e.Location, out ed);
                if (z != null) { if (SelectionChanged != null) SelectionChanged(z); ShowMenu(z, e.Location); }
                var sp = SpeedAt(e.Location, out ed);
                if (sp != null) { if (SpeedSelectionChanged != null) SpeedSelectionChanged(sp); ShowSpeedMenu(sp, e.Location); }
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            dragMoved = false; dragStartX = e.X;
            float xs = XOf(p.TrimStart), xe = XOf(p.TrimEnd);
            var vr = VideoRow;
            if (e.Y >= vr.Y - D(4) && e.Y <= vr.Bottom + D(4))
            {
                if (Math.Abs(e.X - xs) <= D(9)) { drag = DragKind.TrimStart; return; }
                if (Math.Abs(e.X - xe) <= D(9)) { drag = DragKind.TrimEnd; return; }
            }
            int edge;
            var hit = ZoomAt(e.Location, out edge);
            if (hit != null)
            {
                if (SelectionChanged != null) SelectionChanged(hit);
                dragZoom = hit;
                drag = edge == 1 ? DragKind.ZoomLeft : edge == 2 ? DragKind.ZoomRight : DragKind.ZoomMove;
                dragOffset = TOf(e.X) - hit.Start;
                return;
            }
            if (ZoomRow.Contains(e.Location))
            {
                if (e.Clicks >= 2) { f.AddZoomAt(TOf(e.X)); return; }
                if (SelectionChanged != null) SelectionChanged(null);
                return;
            }
            var sh = SpeedAt(e.Location, out edge);
            if (sh != null)
            {
                if (SpeedSelectionChanged != null) SpeedSelectionChanged(sh);
                dragSpeed = sh;
                drag = edge == 1 ? DragKind.SpeedLeft : edge == 2 ? DragKind.SpeedRight : DragKind.SpeedMove;
                dragOffset = TOf(e.X) - sh.Start;
                return;
            }
            if (SpeedRow.Contains(e.Location))
            {
                if (e.Clicks >= 2) { f.AddSpeedAt(TOf(e.X)); return; }
                if (SpeedSelectionChanged != null) SpeedSelectionChanged(null);
                return;
            }
            drag = DragKind.Seek;
            if (Seek != null) Seek(Math.Max(p.TrimStart, Math.Min(p.TrimEnd, TOf(e.X))));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var p = P;
            hoverX = e.X;
            int t = Math.Max(0, Math.Min(p.Duration, TOf(e.X)));
            if (Math.Abs(e.X - dragStartX) > 2) dragMoved = true;
            const int minLen = 500;
            switch (drag)
            {
                case DragKind.Seek: if (Seek != null) Seek(Math.Max(p.TrimStart, Math.Min(p.TrimEnd, t))); Invalidate(); return;
                case DragKind.TrimStart: p.TrimStart = Math.Min(t, p.TrimEnd - minLen); if (Seek != null) Seek(p.TrimStart); return;
                case DragKind.TrimEnd: p.TrimEnd = Math.Max(t, p.TrimStart + minLen); if (Seek != null) Seek(p.TrimEnd); return;
                case DragKind.ZoomMove:
                case DragKind.ZoomLeft:
                case DragKind.ZoomRight:
                    if (!dragMoved) return;
                    MoveZoom(t);
                    if (Changed != null) Changed();
                    return;
                case DragKind.SpeedMove:
                case DragKind.SpeedLeft:
                case DragKind.SpeedRight:
                    if (!dragMoved) return;
                    MoveSpeed(t);
                    if (Changed != null) Changed();
                    return;
            }
            int edge, sedge;
            var z = ZoomAt(e.Location, out edge);
            var sp = SpeedAt(e.Location, out sedge);
            float xs = XOf(p.TrimStart), xe = XOf(p.TrimEnd);
            var vr = VideoRow;
            bool onTrim = (Math.Abs(e.X - xs) <= D(9) || Math.Abs(e.X - xe) <= D(9)) && e.Y >= vr.Y - D(4) && e.Y <= vr.Bottom + D(4);
            Cursor = onTrim || (z != null && edge != 3) || (sp != null && sedge != 3) ? Cursors.SizeWE : z != null || sp != null ? Cursors.SizeAll
                : ZoomRow.Contains(e.Location) || SpeedRow.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        void MoveSpeed(int t)
        {
            var p = P; var z = dragSpeed;
            int prevEnd = 0, nextStart = p.Duration;
            foreach (var o in p.Speeds)
            {
                if (o == z) continue;
                if (o.End <= z.Start + 1 && o.End > prevEnd) prevEnd = o.End;
                if (o.Start >= z.End - 1 && o.Start < nextStart) nextStart = o.Start;
            }
            const int minLen = 300;
            if (drag == DragKind.SpeedMove)
            {
                int len = z.End - z.Start;
                int s = Math.Max(prevEnd, Math.Min(nextStart - len, t - dragOffset));
                z.Start = s; z.End = s + len;
            }
            else if (drag == DragKind.SpeedLeft) z.Start = Math.Max(prevEnd, Math.Min(z.End - minLen, t));
            else z.End = Math.Min(nextStart, Math.Max(z.Start + minLen, t));
        }

        void ShowSpeedMenu(SpeedRegion z, Point at)
        {
            menu.Items.Clear();
            foreach (var r in SpeedRegion.Rates)
            {
                float v = r;
                menu.Items.Add(new ToolStripMenuItem((v >= 1 ? "Acelerar " : "Desacelerar ") + SpeedRegion.Name(v), null, (s, e) =>
                {
                    z.Rate = v; P.Invalidate();
                    if (Commit != null) Commit();
                    if (SpeedSelectionChanged != null) SpeedSelectionChanged(z);
                }) { Checked = Math.Abs(z.Rate - v) < 0.01f });
            }
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Excluir trecho", null, (s, e) => f.DeleteSelected());
            menu.Show(this, at);
        }

        // move ou redimensiona o trecho arrastado sem invadir os vizinhos
        void MoveZoom(int t)
        {
            var p = P; var z = dragZoom;
            int prevEnd = 0, nextStart = p.Duration;
            foreach (var o in p.Zooms)
            {
                if (o == z) continue;
                if (o.End <= z.Start + 1 && o.End > prevEnd) prevEnd = o.End;
                if (o.Start >= z.End - 1 && o.Start < nextStart) nextStart = o.Start;
            }
            const int minZoom = 300;
            if (drag == DragKind.ZoomMove)
            {
                int len = z.End - z.Start;
                int s = Math.Max(prevEnd, Math.Min(nextStart - len, t - dragOffset));
                z.Start = s; z.End = s + len;
            }
            else if (drag == DragKind.ZoomLeft) z.Start = Math.Max(prevEnd, Math.Min(z.End - minZoom, t));
            else z.End = Math.Min(nextStart, Math.Max(z.Start + minZoom, t));
            z.Auto = false;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            var d = drag;
            drag = DragKind.None;
            if ((d == DragKind.TrimStart || d == DragKind.TrimEnd) || ((d == DragKind.ZoomMove || d == DragKind.ZoomLeft || d == DragKind.ZoomRight) && dragMoved))
            {
                P.SortZooms();
                if (Commit != null) Commit();
                if (dragZoom != null && SelectionChanged != null) SelectionChanged(dragZoom);
            }
            if ((d == DragKind.SpeedMove || d == DragKind.SpeedLeft || d == DragKind.SpeedRight) && dragMoved)
            {
                P.SortSpeeds();
                P.Invalidate();
                if (Commit != null) Commit();
                if (SpeedSelectionChanged != null) SpeedSelectionChanged(dragSpeed);
            }
            dragZoom = null; dragSpeed = null;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hoverX = -1; Invalidate(); }

        void ShowMenu(ZoomRegion z, Point at)
        {
            menu.Items.Clear();
            var follow = new ToolStripMenuItem("Seguir o cursor", null, (s, e) => { z.Manual = !z.Manual; P.Invalidate(); if (Commit != null) Commit(); if (SelectionChanged != null) SelectionChanged(z); }) { Checked = !z.Manual };
            menu.Items.Add(follow);
            var depth = new ToolStripMenuItem("Profundidade");
            foreach (var d in new[] { 1.25f, 1.5f, 1.8f, 2.2f, 3f })
            {
                float v = d;
                depth.DropDownItems.Add(new ToolStripMenuItem(v.ToString("0.##") + "×", null, (s, e) => { z.Depth = v; z.Auto = false; P.Invalidate(); if (Commit != null) Commit(); if (SelectionChanged != null) SelectionChanged(z); }) { Checked = Math.Abs(z.Depth - v) < 0.01f });
            }
            menu.Items.Add(depth);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Excluir trecho", null, (s, e) => f.DeleteSelected());
            menu.Show(this, at);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { ClearThumbs(); menu.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
