// Controles escuros desenhados à mão para o editor de vídeo (os do Windows não seguem tema escuro).
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Capturo
{
    static class Ui
    {
        public static readonly Color Bg = Color.FromArgb(20, 20, 26), Panel = Color.FromArgb(29, 29, 37), Panel2 = Color.FromArgb(40, 40, 50),
            Line = Color.FromArgb(52, 52, 64), Text = Color.FromArgb(236, 236, 244), Text2 = Color.FromArgb(150, 150, 168),
            Text3 = Color.FromArgb(105, 105, 122), Accent = Color.FromArgb(123, 92, 250), AccentHi = Color.FromArgb(146, 120, 255),
            Danger = Color.FromArgb(240, 110, 120), Trim = Color.FromArgb(250, 200, 60);

        public static float K = 1;
        public static int D(float v) { return (int)Math.Round(v * K); }

        static Font font, fontB, fontS;
        public static Font Font { get { return font ?? (font = new Font("Segoe UI", 9.5f)); } }
        public static Font FontB { get { return fontB ?? (fontB = new Font("Segoe UI Semibold", 9.5f)); } }
        public static Font FontS { get { return fontS ?? (fontS = new Font("Segoe UI Semibold", 8f)); } }

        public static void Draw(Graphics g, string s, Font f, Color c, Rectangle r, ContentAlignment a)
        {
            var flags = TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
            if (a == ContentAlignment.MiddleLeft) flags |= TextFormatFlags.VerticalCenter | TextFormatFlags.Left;
            else if (a == ContentAlignment.MiddleRight) flags |= TextFormatFlags.VerticalCenter | TextFormatFlags.Right;
            else if (a == ContentAlignment.MiddleCenter) flags |= TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter;
            TextRenderer.DrawText(g, s, f, r, c, flags);
        }
    }

    abstract class UiControl : Control
    {
        protected bool hover, down;
        protected UiControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Ui.Panel; ForeColor = Ui.Text; Font = Ui.Font;
            Margin = new Padding(0, 0, 0, Ui.D(6));
        }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected Graphics Smooth(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            return g;
        }
    }

    // Título de seção
    class UiSection : UiControl
    {
        public UiSection(string text, int width)
        {
            Text = text; Width = width; Height = Ui.D(30);
            Margin = new Padding(0, Ui.D(10), 0, Ui.D(2));
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Smooth(e);
            Ui.Draw(g, Text.ToUpperInvariant(), Ui.FontS, Ui.Text3, new Rectangle(0, 0, Width, Height), ContentAlignment.MiddleLeft);
        }
    }

    // Texto de apoio (várias linhas)
    class UiNote : Label
    {
        public UiNote(string text, int width)
        {
            Text = text; AutoSize = false; Width = width; ForeColor = Ui.Text2; BackColor = Ui.Panel; Font = Ui.Font;
            Margin = new Padding(0, Ui.D(2), 0, Ui.D(6));
            using (var g = CreateGraphics())
                Height = (int)Math.Ceiling(g.MeasureString(text, Font, width).Height) + Ui.D(4);
        }
        public void SetText(string text)
        {
            Text = text;
            using (var g = CreateGraphics())
                Height = (int)Math.Ceiling(g.MeasureString(text, Font, Width).Height) + Ui.D(4);
        }
    }

    // Slider: rótulo à esquerda, valor à direita, trilho com preenchimento.
    class UiSlider : UiControl
    {
        public event Action ValueChanged, Committed;
        public float Min, Max, Step;
        float value;
        public Func<float, string> Format = v => v.ToString("0");
        bool dragging;

        public UiSlider(string label, float min, float max, float val, float step, int width)
        {
            Text = label; Min = min; Max = max; Step = step; value = Clamp(val);
            Width = width; Height = Ui.D(48);
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        float Clamp(float v)
        {
            v = Math.Max(Min, Math.Min(Max, v));
            if (Step > 0) v = (float)(Math.Round((v - Min) / Step) * Step + Min);
            return v;
        }

        public float Value
        {
            get { return value; }
            set { float v = Clamp(value); if (Math.Abs(v - this.value) > 1e-6) { this.value = v; Invalidate(); if (ValueChanged != null) ValueChanged(); } }
        }

        Rectangle Track { get { return new Rectangle(Ui.D(8), Ui.D(30), Width - Ui.D(16), Ui.D(4)); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Smooth(e);
            bool en = Enabled;
            Ui.Draw(g, Text, Font, en ? Ui.Text : Ui.Text3, new Rectangle(0, 0, Width - Ui.D(70), Ui.D(22)), ContentAlignment.MiddleLeft);
            Ui.Draw(g, Format(value), Ui.FontB, en ? Ui.Text2 : Ui.Text3, new Rectangle(Width - Ui.D(90), 0, Ui.D(90), Ui.D(22)), ContentAlignment.MiddleRight);
            var tr = Track;
            Art.FillRound(g, Ui.Panel2, tr, tr.Height / 2f);
            float f = Max > Min ? (value - Min) / (Max - Min) : 0;
            int fx = tr.X + (int)(tr.Width * f);
            if (fx > tr.X) Art.FillRound(g, en ? Ui.Accent : Ui.Text3, new Rectangle(tr.X, tr.Y, fx - tr.X, tr.Height), tr.Height / 2f);
            float kr = (hover || dragging) && en ? Ui.D(8) : Ui.D(7);
            using (var b = new SolidBrush(en ? Color.White : Ui.Text2))
                g.FillEllipse(b, fx - kr, tr.Y + tr.Height / 2f - kr, kr * 2, kr * 2);
            if (Focused && en)
                using (var p = new Pen(Color.FromArgb(120, Ui.AccentHi), Ui.D(2)))
                    g.DrawEllipse(p, fx - kr - Ui.D(2), tr.Y + tr.Height / 2f - kr - Ui.D(2), kr * 2 + Ui.D(4), kr * 2 + Ui.D(4));
        }

        void SetFromX(int x)
        {
            var tr = Track;
            float f = Math.Max(0, Math.Min(1, (x - tr.X) / (float)tr.Width));
            Value = Min + (Max - Min) * f;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !Enabled) return;
            Focus();
            dragging = true; SetFromX(e.X);
        }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (dragging) SetFromX(e.X); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragging) { dragging = false; Invalidate(); if (Committed != null) Committed(); }
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!Enabled || !Focused) return;
            Value += (e.Delta > 0 ? 1 : -1) * (Step > 0 ? Step : (Max - Min) / 50);
            if (Committed != null) Committed();
        }
        protected override bool IsInputKey(Keys k) { return k == Keys.Left || k == Keys.Right || base.IsInputKey(k); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            float st = Step > 0 ? Step : (Max - Min) / 50;
            if (e.KeyCode == Keys.Left) { Value -= st; e.Handled = true; if (Committed != null) Committed(); }
            if (e.KeyCode == Keys.Right) { Value += st; e.Handled = true; if (Committed != null) Committed(); }
        }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    }

    // Chave liga/desliga com texto
    class UiToggle : UiControl
    {
        public event Action CheckedChanged;
        bool on;
        public UiToggle(string text, bool value, int width)
        {
            Text = text; on = value; Width = width; Height = Ui.D(32); Cursor = Cursors.Hand; TabStop = true;
        }
        public bool Checked
        {
            get { return on; }
            set { if (on != value) { on = value; Invalidate(); if (CheckedChanged != null) CheckedChanged(); } }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Smooth(e);
            bool en = Enabled;
            Ui.Draw(g, Text, Font, en ? Ui.Text : Ui.Text3, new Rectangle(0, 0, Width - Ui.D(50), Height), ContentAlignment.MiddleLeft);
            int sw = Ui.D(36), sh = Ui.D(20);
            var r = new Rectangle(Width - sw - Ui.D(2), (Height - sh) / 2, sw, sh);
            Art.FillRound(g, !en ? Ui.Panel2 : on ? Ui.Accent : (hover ? Color.FromArgb(62, 62, 76) : Ui.Panel2), r, sh / 2f);
            int kd = sh - Ui.D(6);
            int kx = on ? r.Right - kd - Ui.D(3) : r.X + Ui.D(3);
            using (var b = new SolidBrush(en ? Color.White : Ui.Text2)) g.FillEllipse(b, kx, r.Y + Ui.D(3), kd, kd);
            if (Focused)
                using (var p = new Pen(Color.FromArgb(120, Ui.AccentHi), Ui.D(1.5f)))
                using (var path = Art.RoundRect(new RectangleF(r.X - Ui.D(2), r.Y - Ui.D(2), r.Width + Ui.D(4), r.Height + Ui.D(4)), (sh + Ui.D(4)) / 2f))
                    g.DrawPath(p, path);
        }
        protected override void OnMouseClick(MouseEventArgs e) { base.OnMouseClick(e); if (Enabled && e.Button == MouseButtons.Left) { Focus(); Checked = !Checked; } }
        protected override bool IsInputKey(Keys k) { return k == Keys.Space || base.IsInputKey(k); }
        protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode == Keys.Space && Enabled) { Checked = !Checked; e.Handled = e.SuppressKeyPress = true; } }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    }

    // Escolha entre poucas opções (pílulas lado a lado), com rótulo opcional acima
    class UiSegmented : UiControl
    {
        public event Action SelectedChanged;
        readonly string[] items;
        int sel, hot = -1;
        readonly string label;
        int Top0 { get { return label != null ? Ui.D(22) : 0; } }

        public UiSegmented(string label, string[] items, int selected, int width)
        {
            this.label = label; this.items = items; sel = selected; Width = width;
            Height = Top0 + Ui.D(32); Cursor = Cursors.Hand;
        }
        public int Selected
        {
            get { return sel; }
            // -1: nenhuma opção marcada (ex.: posição livre da câmera); não avisa a mudança
            set
            {
                if (value == -1 && sel != -1) { sel = -1; Invalidate(); return; }
                if (value != sel && value >= 0 && value < items.Length) { sel = value; Invalidate(); if (SelectedChanged != null) SelectedChanged(); }
            }
        }
        Rectangle Cell(int i)
        {
            int w = Width / items.Length;
            return new Rectangle(i * w, Top0, i == items.Length - 1 ? Width - i * w : w, Ui.D(32));
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Smooth(e);
            if (label != null) Ui.Draw(g, label, Font, Enabled ? Ui.Text : Ui.Text3, new Rectangle(0, 0, Width, Ui.D(20)), ContentAlignment.MiddleLeft);
            var all = new Rectangle(0, Top0, Width, Ui.D(32));
            Art.FillRound(g, Ui.Panel2, all, Ui.D(8));
            for (int i = 0; i < items.Length; i++)
            {
                var r = Cell(i); r.Inflate(-Ui.D(3), -Ui.D(3));
                if (i == sel) Art.FillRound(g, Enabled ? Ui.Accent : Ui.Text3, r, Ui.D(6));
                else if (i == hot && Enabled) Art.FillRound(g, Color.FromArgb(58, 58, 72), r, Ui.D(6));
                var fnt = i == sel ? Ui.FontB : Font;
                if (TextRenderer.MeasureText(items[i], fnt).Width > r.Width - Ui.D(4)) fnt = Ui.FontS;   // não corta o texto
                Ui.Draw(g, items[i], fnt, i == sel ? Color.White : Enabled ? Ui.Text2 : Ui.Text3, r, ContentAlignment.MiddleCenter);
            }
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = -1;
            for (int i = 0; i < items.Length; i++) if (Cell(i).Contains(e.Location)) h = i;
            if (h != hot) { hot = h; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = -1; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Enabled || e.Button != MouseButtons.Left) return;
            for (int i = 0; i < items.Length; i++) if (Cell(i).Contains(e.Location)) Selected = i;
        }
    }

    // Botão arredondado (serve também como AcceptButton/CancelButton de um formulário)
    class UiButton : UiControl, IButtonControl
    {
        public Color Fill = Ui.Panel2, Fore = Ui.Text;
        public DialogResult DialogResult { get; set; }
        public void NotifyDefault(bool value) { }
        public void PerformClick() { if (Enabled) OnClick(EventArgs.Empty); }
        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            var f = FindForm();
            if (f != null && DialogResult != DialogResult.None) f.DialogResult = DialogResult;
        }
        public UiButton(string text, int width, int height)
        {
            Text = text; Width = width; Height = height; Cursor = Cursors.Hand; TabStop = true; Font = Ui.FontB;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Smooth(e);
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            Color f = !Enabled ? Ui.Panel2 : down ? ControlPaint.Dark(Fill, 0.05f) : hover ? ControlPaint.Light(Fill, 0.18f) : Fill;
            Art.FillRound(g, f, r, Ui.D(8));
            Ui.Draw(g, Text, Font, Enabled ? Fore : Ui.Text3, r, ContentAlignment.MiddleCenter);
            if (Focused && Enabled)
                using (var p = new Pen(Color.FromArgb(140, Ui.AccentHi), Ui.D(1.5f)))
                using (var path = Art.RoundRect(new RectangleF(1, 1, Width - 3, Height - 3), Ui.D(8)))
                    g.DrawPath(p, path);
        }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); down = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); down = false; Invalidate(); }
        protected override bool IsInputKey(Keys k) { return k == Keys.Space || k == Keys.Enter || base.IsInputKey(k); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if ((e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) && Enabled) { OnClick(EventArgs.Empty); e.Handled = e.SuppressKeyPress = true; }
        }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    }

    // Barra de progresso fina
    class UiProgress : UiControl
    {
        double v;
        public UiProgress(int width) { Width = width; Height = Ui.D(6); Margin = new Padding(0, Ui.D(4), 0, Ui.D(4)); }
        public double Value { get { return v; } set { v = Math.Max(0, Math.Min(1, value)); Invalidate(); } }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Smooth(e);
            var r = new Rectangle(0, 0, Width, Height);
            Art.FillRound(g, Ui.Panel2, r, Height / 2f);
            if (v > 0) Art.FillRound(g, Ui.Accent, new Rectangle(0, 0, Math.Max(Height, (int)(Width * v)), Height), Height / 2f);
        }
    }

    // Abas no topo do painel lateral
    class UiTabs : UiControl
    {
        public event Action SelectedChanged;
        readonly string[] items;
        int sel, hot = -1;
        public UiTabs(string[] items) { this.items = items; Height = Ui.D(44); Cursor = Cursors.Hand; BackColor = Ui.Panel; }
        public int Selected { get { return sel; } set { if (value != sel) { sel = value; Invalidate(); if (SelectedChanged != null) SelectedChanged(); } } }
        Rectangle Cell(int i)
        {
            int pad = Ui.D(14), w = (Width - pad * 2) / items.Length;
            return new Rectangle(pad + i * w, Ui.D(8), w, Ui.D(30));
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Smooth(e);
            for (int i = 0; i < items.Length; i++)
            {
                var r = Cell(i);
                if (i == sel) Art.FillRound(g, Ui.Panel2, r, Ui.D(8));
                else if (i == hot) Art.FillRound(g, Color.FromArgb(35, 35, 45), r, Ui.D(8));
                Ui.Draw(g, items[i], i == sel ? Ui.FontB : Font, i == sel ? Ui.Text : Ui.Text2, r, ContentAlignment.MiddleCenter);
            }
            using (var p = new Pen(Ui.Line)) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = -1;
            for (int i = 0; i < items.Length; i++) if (Cell(i).Contains(e.Location)) h = i;
            if (h != hot) { hot = h; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = -1; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            for (int i = 0; i < items.Length; i++) if (Cell(i).Contains(e.Location)) Selected = i;
        }
    }

    // Painel rolável em coluna (o conteúdo de uma aba)
    class UiColumn : FlowLayoutPanel
    {
        public UiColumn()
        {
            FlowDirection = FlowDirection.TopDown; WrapContents = false; AutoScroll = true;
            BackColor = Ui.Panel; Dock = DockStyle.Fill;
            Padding = new Padding(Ui.D(18), Ui.D(6), Ui.D(14), Ui.D(16));
        }
        public int Inner { get { return Width - Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 2; } }
    }
}
