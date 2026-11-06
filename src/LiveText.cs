// "Texto vivo": deixa selecionar o texto direto sobre a imagem, como num documento.
// Lê a imagem com o OCR em segundo plano e depois: arrastar seleciona do primeiro ao último
// caractere (na ordem de leitura), duplo clique pega a palavra, triplo clique a linha.
// O texto copiado mantém quebras de linha e colunas (tabulação).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace Capturo
{
    class LiveText
    {
        public enum State { Idle, Reading, Ready, Empty, Error }
        public State Status = State.Idle;
        public string Error;
        public event Action Changed;

        // posição da imagem na tela do host: ponto da tela = Origin + ponto da imagem × Scale
        public PointF Origin;
        public float Scale = 1;

        readonly Control host;
        OcrDoc doc;
        string text = "";
        List<OcrWord> order = new List<OcrWord>();             // palavras na ordem de leitura
        readonly Dictionary<OcrWord, OcrLine> lineOf = new Dictionary<OcrWord, OcrLine>();
        int anchor = -1, focus = -1, gen;
        bool dragging;
        DateTime lastClick; Point lastClickAt; int clickCount;

        public LiveText(Control host) { this.host = host; }

        public bool Ready { get { return Status == State.Ready; } }
        public bool HasSelection { get { return anchor >= 0 && focus >= 0 && order.Count > 0; } }
        public string AllText { get { return text; } }
        public int WordCount { get { return order.Count; } }
        public bool Dragging { get { return dragging; } }

        public string SelectedText
        {
            get
            {
                if (!HasSelection) return "";
                var a = order[Math.Min(anchor, focus)]; var b = order[Math.Max(anchor, focus)];
                return text.Substring(a.Start, b.Start + b.Length - a.Start);
            }
        }

        // Começa a ler a imagem (fica com ela e a descarta no fim).
        public void Start(Bitmap image)
        {
            int my = ++gen;
            Status = State.Reading; doc = null; order.Clear(); lineOf.Clear(); text = ""; anchor = focus = -1; dragging = false;
            Raise();
            var t = new Thread(() =>
            {
                OcrDoc d = null; string err = null;
                try { d = Read(image); }
                catch (Exception ex) { err = ex is TypeLoadException || ex is System.IO.FileNotFoundException ? "Este Windows não tem o reconhecimento de texto." : ex.Message; }
                finally { image.Dispose(); }
                try { host.BeginInvoke((Action)(() => Done(my, d, err))); } catch { }
            }) { IsBackground = true, Name = "texto-vivo" };
            t.Start();
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static OcrDoc Read(Bitmap b) { return Ocr.Read(b, null); }

        void Done(int my, OcrDoc d, string err)
        {
            if (my != gen) return;   // a área mudou enquanto lia
            if (err != null) { Status = State.Error; Error = err; Raise(); return; }
            doc = d;
            text = d.Build(OcrLayout.Lines);
            order = d.Words.OrderBy(w => w.Start).ToList();
            lineOf.Clear();
            foreach (var l in d.Lines) foreach (var w in l.Words) lineOf[w] = l;
            Status = order.Count > 0 ? State.Ready : State.Empty;
            Raise();
        }

        public void Reset() { gen++; Status = State.Idle; doc = null; order.Clear(); lineOf.Clear(); text = ""; anchor = focus = -1; dragging = false; }

        public void ClearSelection() { anchor = focus = -1; Raise(); }

        public void SelectAll()
        {
            if (order.Count == 0) return;
            anchor = 0; focus = order.Count - 1; Raise();
        }

        void Raise() { if (Changed != null) Changed(); }

        // ---- geometria
        RectangleF Screen(RectangleF r) { return new RectangleF(Origin.X + r.X * Scale, Origin.Y + r.Y * Scale, r.Width * Scale, r.Height * Scale); }

        public bool Over(Point p)
        {
            if (!Ready) return false;
            foreach (var w in order) { var r = Screen(w.Box); r.Inflate(3, 3); if (r.Contains(p)) return true; }
            return false;
        }

        // A palavra mais próxima: a linha conta muito mais que a distância na horizontal,
        // assim arrastar pela margem direita pega o fim da linha, como num editor de texto.
        int Nearest(Point p)
        {
            int best = -1; double bd = double.MaxValue;
            for (int i = 0; i < order.Count; i++)
            {
                var r = Screen(lineOf.ContainsKey(order[i]) ? Row(order[i]) : order[i].Box);
                var wr = Screen(order[i].Box);
                double dy = p.Y < r.Top ? r.Top - p.Y : p.Y > r.Bottom ? p.Y - r.Bottom : 0;
                double dx = p.X < wr.Left ? wr.Left - p.X : p.X > wr.Right ? p.X - wr.Right : 0;
                double d = dy * 6 + dx;
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }

        // faixa vertical da linha da palavra (todas as palavras da linha têm a mesma altura de seleção)
        RectangleF Row(OcrWord w)
        {
            var l = lineOf[w];
            return new RectangleF(w.Box.X, l.Box.Y, w.Box.Width, l.Box.Height);
        }

        // ---- mouse (true quando o clique foi usado para selecionar texto)
        public bool MouseDown(Point p, bool shift)
        {
            if (!Ready) return false;
            // conta cliques seguidos (o Windows não avisa o triplo clique)
            if ((DateTime.Now - lastClick).TotalMilliseconds < SystemInformation.DoubleClickTime && Math.Abs(p.X - lastClickAt.X) < 6 && Math.Abs(p.Y - lastClickAt.Y) < 6) clickCount++;
            else clickCount = 1;
            lastClick = DateTime.Now; lastClickAt = p;
            int i = Nearest(p);
            if (i < 0) return false;
            if (clickCount == 2)
            {
                anchor = focus = i;   // palavra
            }
            else if (clickCount >= 3)
            {
                // a linha visual inteira (o OCR às vezes quebra uma linha em pedaços)
                var l = lineOf[order[i]];
                float cy = l.Box.Top + l.Box.Height / 2;
                var same = Enumerable.Range(0, order.Count).Where(k => { var b = lineOf[order[k]].Box; return cy > b.Top && cy < b.Bottom; }).ToList();
                anchor = same.Min(); focus = same.Max();
            }
            else
            {
                if (shift && anchor >= 0) focus = i;
                else { anchor = focus = i; dragging = true; pressedEmpty = !Over(p); }
            }
            Raise();
            return true;
        }

        bool pressedEmpty;

        public void MouseMove(Point p)
        {
            if (!dragging) return;
            int i = Nearest(p);
            if (i >= 0 && i != focus) { focus = i; pressedEmpty = false; Raise(); }
        }

        public void MouseUp()
        {
            // um clique simples fora do texto não deixa uma palavra solta selecionada
            if (dragging && pressedEmpty && anchor == focus) { anchor = focus = -1; Raise(); }
            dragging = false;
        }

        // ---- desenho
        public void Paint(Graphics g, bool hint)
        {
            if (!Ready) return;
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // palavras disponíveis: um véu bem leve, só para mostrar o que dá para selecionar
            if (hint)
                using (var b = new SolidBrush(Color.FromArgb(26, 90, 140, 255)))
                    foreach (var w in order) { var r = Screen(w.Box); r.Inflate(2, 1); g.FillRectangle(b, r); }
            if (HasSelection)
            {
                int a = Math.Min(anchor, focus), z = Math.Max(anchor, focus);
                // junta as palavras seguidas da mesma linha num bloco só, como uma seleção de texto
                using (var b = new SolidBrush(Color.FromArgb(120, 40, 110, 245)))
                {
                    RectangleF cur = RectangleF.Empty; OcrLine curLine = null;
                    for (int i = a; i <= z; i++)
                    {
                        var w = order[i]; var l = lineOf[w];
                        var r = Row(w);
                        if (curLine == l && !cur.IsEmpty) cur = RectangleF.Union(cur, r);
                        else
                        {
                            if (!cur.IsEmpty) Fill(g, b, cur);
                            cur = r; curLine = l;
                        }
                    }
                    if (!cur.IsEmpty) Fill(g, b, cur);
                }
            }
            g.SmoothingMode = old;
        }

        void Fill(Graphics g, Brush b, RectangleF r)
        {
            var s = Screen(r);
            s.Inflate(2 * Math.Max(1, Scale) * 0.6f, 1.5f * Math.Max(1, Scale) * 0.6f);
            g.FillRectangle(b, s);
        }
    }
}
