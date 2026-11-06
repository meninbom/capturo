// Janela do texto reconhecido (OCR): a imagem com as palavras marcadas ao lado do texto editável.
// Clicar ou arrastar sobre as palavras da imagem seleciona o texto, e vice-versa.
// Abrir o resultado não mexe na área de transferência: só os botões e o Ctrl+C copiam.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace Capturo
{
    sealed class OcrTextForm : Form
    {
        readonly RichTextBox textBox;
        readonly UiButton copySelection, copyAll, close, search, translate, save, langBtn;
        readonly Label status, title, subtitle;
        readonly UiSegmented layoutSel;
        readonly FlowLayoutPanel links;
        readonly DoubleBufferedPanel imagePanel;
        readonly Panel textHost;
        readonly Label placeholder;          // "lendo..." ou "nada encontrado" no lugar do texto

        void ShowPlaceholder(string text)
        {
            placeholder.Text = text ?? "";
            placeholder.Visible = text != null;
        }
        readonly Bitmap image;              // a captura (null quando só há texto)
        OcrDoc doc;
        string language;                    // null = idioma do usuário
        bool reading, settingText, edited;
        string emptyMessage;
        readonly System.Windows.Forms.Timer flash = new System.Windows.Forms.Timer { Interval = 1400 };

        int D(float v) { return Ui.D(v); }

        // Só o texto (sem imagem).
        public OcrTextForm(string text) : this(null, text) { }

        // Com a imagem: a janela abre na hora e o texto chega depois (SetResult).
        public OcrTextForm(Bitmap capture) : this(capture, null) { }

        OcrTextForm(Bitmap capture, string text)
        {
            image = capture;
            Text = "Texto reconhecido · Capturo";
            Name = "OcrTextForm";
            Icon = Art.AppIcon();
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;
            using (var g = CreateGraphics()) Ui.K = Math.Max(1f, g.DpiX / 96f);
            Font = Ui.Font;
            BackColor = Ui.Bg; ForeColor = Ui.Text;
            KeyPreview = true;
            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            ClientSize = new Size(Math.Min(wa.Width - D(60), D(image != null ? 1080 : 760)), Math.Min(wa.Height - D(60), D(620)));
            MinimumSize = new Size(D(image != null ? 760 : 560), D(420));

            // ---- cabeçalho
            var header = new Panel { Dock = DockStyle.Top, Height = D(62), BackColor = Ui.Bg, Padding = new Padding(D(20), D(10), D(16), D(6)) };
            title = new Label { Text = "Texto reconhecido", AutoSize = false, Dock = DockStyle.Top, Height = D(26), ForeColor = Ui.Text, Font = new Font("Segoe UI Semibold", 12f), BackColor = Ui.Bg };
            subtitle = new Label { Text = "", AutoSize = false, Dock = DockStyle.Top, Height = D(20), ForeColor = Ui.Text2, BackColor = Ui.Bg };
            langBtn = new UiButton("Idioma", D(190), D(32)) { Dock = DockStyle.Right, BackColor = Ui.Bg, Visible = false, Font = Ui.Font };
            header.Controls.Add(subtitle); header.Controls.Add(title); header.Controls.Add(langBtn);

            // ---- ações (embaixo)
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = D(86), BackColor = Ui.Bg, Padding = new Padding(D(20), D(6), D(20), D(8)) };
            var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = D(44), BackColor = Ui.Bg, WrapContents = false, Margin = new Padding(0) };
            copyAll = new UiButton("Copiar tudo", D(124), D(38)) { Name = "CopyAll", Fill = Ui.Accent, Fore = Color.White, BackColor = Ui.Bg, Margin = new Padding(0, 0, D(8), 0) };
            copySelection = new UiButton("Copiar seleção", D(130), D(38)) { Name = "CopySelection", BackColor = Ui.Bg, Enabled = false, Margin = new Padding(0, 0, D(16), 0) };
            search = new UiButton("Pesquisar", D(100), D(38)) { BackColor = Ui.Bg, Margin = new Padding(0, 0, D(8), 0) };
            translate = new UiButton("Traduzir", D(92), D(38)) { BackColor = Ui.Bg, Margin = new Padding(0, 0, D(8), 0) };
            save = new UiButton("Salvar .txt", D(104), D(38)) { BackColor = Ui.Bg, Margin = new Padding(0, 0, D(8), 0) };
            close = new UiButton("Fechar", D(86), D(38)) { BackColor = Ui.Bg, DialogResult = DialogResult.Cancel, Margin = new Padding(0) };
            actions.Controls.AddRange(new Control[] { copyAll, copySelection, search, translate, save });
            var closeHost = new Panel { Dock = DockStyle.Right, Width = D(86), BackColor = Ui.Bg };
            close.Location = new Point(0, D(6)); closeHost.Controls.Add(close);
            var actionRow = new Panel { Dock = DockStyle.Top, Height = D(50), BackColor = Ui.Bg, Padding = new Padding(0, D(6), 0, 0) };
            actionRow.Controls.Add(actions); actionRow.Controls.Add(closeHost);
            actions.Dock = DockStyle.Fill;
            status = new Label { Name = "SelectionStatus", AutoSize = false, Dock = DockStyle.Top, Height = D(22), ForeColor = Ui.Text2, BackColor = Ui.Bg };
            bottom.Controls.Add(status); bottom.Controls.Add(actionRow);

            // ---- texto
            var right = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg, Padding = new Padding(image != null ? D(10) : D(20), D(4), D(20), D(4)) };
            layoutSel = new UiSegmented(null, new[] { "Linhas", "Texto corrido", "Uma linha" }, 0, D(330)) { BackColor = Ui.Bg, Dock = DockStyle.Top, Visible = false };
            var layoutRow = new Panel { Dock = DockStyle.Top, Height = D(40), BackColor = Ui.Bg };
            layoutSel.Dock = DockStyle.Left; layoutRow.Controls.Add(layoutSel);
            links = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = D(40), BackColor = Ui.Bg, WrapContents = false, Visible = false, Padding = new Padding(0, D(6), 0, 0) };
            textHost = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Panel2, Padding = new Padding(D(14), D(12), D(8), D(8)) };
            textBox = new RichTextBox
            {
                Name = "RecognizedText", Dock = DockStyle.Fill, ReadOnly = false, DetectUrls = false, HideSelection = false,
                WordWrap = true, ScrollBars = RichTextBoxScrollBars.Vertical, BorderStyle = BorderStyle.None,
                BackColor = Ui.Panel2, ForeColor = Ui.Text, Font = new Font("Segoe UI", 11f), Text = text ?? "",
                AccessibleName = "Texto reconhecido; selecione o trecho para copiar", TabIndex = 0
            };
            textHost.Controls.Add(textBox);
            placeholder = new Label { Dock = DockStyle.Fill, BackColor = Ui.Panel2, ForeColor = Ui.Text2, TextAlign = ContentAlignment.MiddleCenter, Visible = false, Font = new Font("Segoe UI", 10.5f) };
            textHost.Controls.Add(placeholder);
            placeholder.BringToFront();
            right.Controls.Add(textHost); right.Controls.Add(links); right.Controls.Add(layoutRow);

            // ---- imagem
            imagePanel = new DoubleBufferedPanel { Dock = DockStyle.Left, BackColor = Color.FromArgb(14, 14, 18), Visible = image != null, Cursor = Cursors.IBeam };
            imagePanel.Paint += (s, e) => PaintImage(e.Graphics);
            imagePanel.MouseDown += ImageDown; imagePanel.MouseMove += ImageMove; imagePanel.MouseUp += ImageUp;
            imagePanel.Resize += (s, e) => imagePanel.Invalidate();
            var imageWrap = new Panel { Dock = DockStyle.Left, BackColor = Ui.Bg, Padding = new Padding(D(20), D(4), D(10), D(4)), Visible = image != null };
            imagePanel.Dock = DockStyle.Fill; imageWrap.Controls.Add(imagePanel);
            Resize += (s, e) => imageWrap.Width = (int)(ClientSize.Width * 0.48);
            imageWrap.Width = (int)(ClientSize.Width * 0.48);

            Controls.Add(right); Controls.Add(imageWrap); Controls.Add(bottom); Controls.Add(header);

            // ---- eventos
            copySelection.Click += (s, e) => Copy(textBox.SelectedText, "Seleção copiada");
            copyAll.Click += (s, e) => Copy(textBox.Text, "Texto copiado");
            close.Click += (s, e) => Close();
            search.Click += (s, e) => OpenWeb("https://www.google.com/search?q=" + Uri.EscapeDataString(Pick(300)));
            translate.Click += (s, e) => OpenWeb("https://translate.google.com/?sl=auto&tl=pt&op=translate&text=" + Uri.EscapeDataString(Pick(4000)));
            save.Click += (s, e) => SaveTxt();
            langBtn.Click += (s, e) => ShowLanguages();
            layoutSel.SelectedChanged += () => { if (doc != null) ShowDoc(); };
            textBox.SelectionChanged += (s, e) => { UpdateSelection(); imagePanel.Invalidate(); };
            textBox.TextChanged += (s, e) => { if (!settingText) { edited = true; imagePanel.Invalidate(); UpdateSelection(); } };
            textBox.KeyDown += (s, e) =>
            {
                if (e.Control && !e.Shift && e.KeyCode == Keys.C) { e.SuppressKeyPress = true; Copy(textBox.SelectedText, "Seleção copiada"); }
                else if (e.Control && e.KeyCode == Keys.A) { e.SuppressKeyPress = true; textBox.SelectAll(); }
            };
            flash.Tick += (s, e) => { flash.Stop(); copyAll.Text = "Copiar tudo"; copySelection.Text = "Copiar seleção"; };
            CancelButton = close;
            Shown += (s, e) => { textBox.Focus(); textBox.Select(0, 0); };

            if (image != null) { reading = true; subtitle.Text = "Lendo o texto..."; SetButtons(false); ShowPlaceholder("Lendo o texto da captura..."); }
            else subtitle.Text = Summary(text);
            UpdateSelection();
        }

        void SetButtons(bool on)
        {
            copyAll.Enabled = search.Enabled = translate.Enabled = save.Enabled = on;
            textBox.ReadOnly = !on;
        }

        // ======================================================== resultado
        public event Action<string> LanguageRequested;   // o app relê a imagem em outro idioma

        public void SetResult(OcrDoc d)
        {
            reading = false;
            doc = d;
            language = d.Language;
            var langs = SafeLanguages();
            langBtn.Visible = image != null && langs.Length > 1;
            langBtn.Text = "Idioma: " + Ocr.LanguageName(d.Language) + "  ▾";
            layoutSel.Visible = d.Lines.Count > 0;
            textBox.Font = d.LooksLikeCode ? new Font("Consolas", 10.5f) : new Font("Segoe UI", 11f);
            textBox.WordWrap = !d.LooksLikeCode;
            emptyMessage = d.Lines.Count == 0 ? "Nenhum texto encontrado nesta área." : null;
            ShowDoc();
            SetButtons(d.Lines.Count > 0);
            if (d.Lines.Count == 0)
            {
                subtitle.Text = "Nenhum texto encontrado";
                ShowPlaceholder("Não encontrei texto nesta área.\n\nDicas: selecione só a parte com texto, evite fundos com\nimagens ou degradês" + (langs.Length > 1 ? " e experimente outro idioma (botão acima)." : "."));
            }
            else ShowPlaceholder(null);
            imagePanel.Invalidate();
        }

        public void SetError(string message)
        {
            reading = false;
            emptyMessage = message;
            subtitle.Text = "Não foi possível ler o texto";
            settingText = true; textBox.Text = ""; settingText = false;
            status.Text = message;
            ShowPlaceholder(message);
            imagePanel.Invalidate();
        }

        public void SetReading(string lang)
        {
            reading = true; doc = null; language = lang;
            subtitle.Text = "Lendo o texto em " + Ocr.LanguageName(lang) + "...";
            SetButtons(false);
            ShowPlaceholder("Lendo o texto em " + Ocr.LanguageName(lang) + "...");
            imagePanel.Invalidate();
        }

        static string[] SafeLanguages() { try { return Ocr.Languages(); } catch { return new string[0]; } }

        void ShowDoc()
        {
            var mode = layoutSel.Selected == 1 ? OcrLayout.Paragraphs : layoutSel.Selected == 2 ? OcrLayout.SingleLine : OcrLayout.Lines;
            settingText = true;
            textBox.Text = doc.Build(mode);
            settingText = false;
            edited = false;
            textBox.Select(0, 0);
            subtitle.Text = Summary(textBox.Text) + (doc.Language.Length > 0 ? " · " + Ocr.LanguageName(doc.Language) : "");
            BuildLinks();
            UpdateSelection();
            imagePanel.Invalidate();
        }

        static string Summary(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            int words = text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
            int lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).Length;
            return words + (words == 1 ? " palavra" : " palavras") + " · " + lines + (lines == 1 ? " linha" : " linhas");
        }

        // Links e e-mails encontrados viram atalhos
        void BuildLinks()
        {
            links.Controls.Clear();
            var found = new List<string>();
            foreach (Match m in Regex.Matches(textBox.Text, @"(https?://[^\s<>""]+|www\.[^\s<>""]+|[\w.+-]+@[\w-]+\.[\w.-]+)"))
            {
                string v = m.Value.TrimEnd('.', ',', ';', ')');
                if (!found.Contains(v)) found.Add(v);
            }
            foreach (var v in found.Take(3))
            {
                bool mail = v.Contains("@") && !v.StartsWith("http");
                string label = (mail ? "✉  " : "↗  ") + (v.Length > 34 ? v.Substring(0, 32) + "…" : v);
                var b = new UiButton(label, Math.Min(D(300), TextRenderer.MeasureText(label, Ui.FontB).Width + D(28)), D(30)) { BackColor = Ui.Bg, Margin = new Padding(0, 0, D(8), 0) };
                string target = mail ? "mailto:" + v : v.StartsWith("www.") ? "https://" + v : v;
                b.Click += (s, e) => OpenWeb(target);
                links.Controls.Add(b);
            }
            links.Visible = found.Count > 0;
        }

        void ShowLanguages()
        {
            var m = new ContextMenuStrip();
            foreach (var tag in SafeLanguages())
            {
                string t = tag;
                var it = new ToolStripMenuItem(Ocr.LanguageName(t), null, (s, e) => { if (LanguageRequested != null && t != language) LanguageRequested(t); }) { Checked = t == language };
                m.Items.Add(it);
            }
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Instalar outros idiomas...", null, (s, e) => OpenWeb("ms-settings:regionlanguage"));
            m.Show(langBtn, new Point(0, langBtn.Height));
        }

        // ======================================================== ações
        string Pick(int max)
        {
            string t = textBox.SelectionLength > 0 ? textBox.SelectedText : textBox.Text;
            t = Regex.Replace(t, @"\s+", " ").Trim();
            return t.Length > max ? t.Substring(0, max) : t;
        }

        void OpenWeb(string url)
        {
            try { Process.Start(url); } catch (Exception ex) { status.Text = "Não foi possível abrir: " + ex.Message; }
        }

        void SaveTxt()
        {
            using (var d = new SaveFileDialog { Filter = "Texto|*.txt", FileName = "Texto_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm") + ".txt", InitialDirectory = TrayApp.S != null ? TrayApp.S.SaveFolder : "" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try { File.WriteAllText(d.FileName, textBox.Text, System.Text.Encoding.UTF8); status.Text = "Salvo em " + d.FileName; }
                catch (Exception ex) { status.Text = "Não foi possível salvar: " + ex.Message; }
            }
        }

        void UpdateSelection()
        {
            copySelection.Enabled = textBox.SelectionLength > 0;
            if (flash.Enabled) return;
            if (reading) { status.Text = "Reconhecendo o texto da captura..."; return; }
            status.Text = copySelection.Enabled
                ? textBox.SelectionLength + " caracteres selecionados · Ctrl+C copia"
                : image != null && doc != null && doc.Lines.Count > 0
                    ? (edited ? "Texto editado. " : "") + "Selecione no texto ou arraste sobre as palavras da imagem."
                    : "Selecione um trecho com o mouse ou com Shift + setas.";
        }

        void Copy(string text, string what)
        {
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                Clipboard.SetText(text);
                status.Text = what + " (" + text.Length + " caracteres). Dá para selecionar e copiar outro trecho.";
                var b = what.StartsWith("Seleção") ? copySelection : copyAll;
                b.Text = "Copiado ✓";
                flash.Stop(); flash.Start();
            }
            catch { status.Text = "Não foi possível copiar. Tente novamente."; }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Control && e.Shift && e.KeyCode == Keys.C) { Copy(textBox.Text, "Texto copiado"); e.Handled = e.SuppressKeyPress = true; }
            else if (e.Control && e.KeyCode == Keys.S) { SaveTxt(); e.Handled = e.SuppressKeyPress = true; }
        }

        // ======================================================== imagem com as palavras
        RectangleF imgRect; float imgScale;
        Point dragStart; Rectangle dragRect; bool dragging;
        OcrWord hoverWord;

        bool Linked { get { return doc != null && !edited; } }

        void PaintImage(Graphics g)
        {
            g.Clear(imagePanel.BackColor);
            if (image == null) return;
            float s = Math.Min((imagePanel.Width - D(24)) / (float)image.Width, (imagePanel.Height - D(24)) / (float)image.Height);
            s = Math.Min(s, 2f);   // não amplia demais imagens pequenas
            float w = image.Width * s, h = image.Height * s;
            imgRect = new RectangleF((imagePanel.Width - w) / 2, (imagePanel.Height - h) / 2, w, h);
            imgScale = s;
            g.InterpolationMode = s < 1 ? InterpolationMode.HighQualityBicubic : InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(image, imgRect);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (reading)
            {
                using (var dim = new SolidBrush(Color.FromArgb(120, 0, 0, 0))) g.FillRectangle(dim, imgRect);
                var r = new Rectangle((int)(imgRect.X + imgRect.Width / 2 - D(90)), (int)(imgRect.Y + imgRect.Height / 2 - D(18)), D(180), D(36));
                Art.FillRound(g, Color.FromArgb(230, 24, 24, 30), r, D(8));
                Ui.Draw(g, "Lendo o texto...", Ui.FontB, Ui.Text, r, ContentAlignment.MiddleCenter);
                return;
            }
            if (doc == null) return;
            int s0 = textBox.SelectionStart, s1 = s0 + textBox.SelectionLength;
            foreach (var word in doc.Words)
            {
                var r = Map(word.Box);
                r.Inflate(D(2), D(1.5f));
                bool sel = Linked && textBox.SelectionLength > 0 && word.Start < s1 && word.Start + word.Length > s0;
                using (var path = Art.RoundRect(r, D(3)))
                {
                    if (sel) using (var b = new SolidBrush(Color.FromArgb(110, Ui.Accent))) g.FillPath(b, path);
                    else if (word == hoverWord) using (var b = new SolidBrush(Color.FromArgb(60, Ui.Accent))) g.FillPath(b, path);
                    using (var p = new Pen(Color.FromArgb(sel ? 230 : 90, Ui.AccentHi), D(1))) g.DrawPath(p, path);
                }
            }
            if (dragging && dragRect.Width > 2)
                using (var b = new SolidBrush(Color.FromArgb(40, Ui.AccentHi)))
                using (var p = new Pen(Ui.AccentHi, D(1)) { DashStyle = DashStyle.Dash })
                { g.FillRectangle(b, dragRect); g.DrawRectangle(p, dragRect); }
            if (doc.Lines.Count == 0 && emptyMessage != null)
            {
                var r = new Rectangle((int)imgRect.X, (int)imgRect.Bottom + D(4), (int)imgRect.Width, D(20));
                Ui.Draw(g, emptyMessage, Ui.Font, Ui.Text2, r, ContentAlignment.MiddleCenter);
            }
        }

        RectangleF Map(RectangleF r) { return new RectangleF(imgRect.X + r.X * imgScale, imgRect.Y + r.Y * imgScale, r.Width * imgScale, r.Height * imgScale); }

        OcrWord WordAt(Point p)
        {
            if (doc == null) return null;
            foreach (var w in doc.Words) { var r = Map(w.Box); r.Inflate(D(3), D(3)); if (r.Contains(p)) return w; }
            return null;
        }

        void SelectWords(IEnumerable<OcrWord> ws)
        {
            var list = ws.ToList();
            if (list.Count == 0 || !Linked) return;
            int a = list.Min(w => w.Start), b = list.Max(w => w.Start + w.Length);
            textBox.Select(a, b - a);
            textBox.Focus();
        }

        void ImageDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || doc == null) return;
            if (edited) { status.Text = "O texto foi editado: a seleção pela imagem fica desligada. Troque o formato acima para voltar ao texto original."; return; }
            dragStart = e.Location; dragging = true; dragRect = Rectangle.Empty;
            var w = WordAt(e.Location);
            if (w != null)
            {
                if ((ModifierKeys & Keys.Shift) != 0 && textBox.SelectionLength > 0)
                {
                    int a = Math.Min(textBox.SelectionStart, w.Start), b = Math.Max(textBox.SelectionStart + textBox.SelectionLength, w.Start + w.Length);
                    textBox.Select(a, b - a);
                }
                else SelectWords(new[] { w });
                if (e.Clicks >= 2) Copy(w.Text, "Palavra copiada");
            }
        }

        void ImageMove(object sender, MouseEventArgs e)
        {
            var w = WordAt(e.Location);
            if (w != hoverWord) { hoverWord = w; imagePanel.Invalidate(); }
            imagePanel.Cursor = w != null || dragging ? Cursors.IBeam : Cursors.Cross;
            if (!dragging) return;
            dragRect = Rectangle.FromLTRB(Math.Min(dragStart.X, e.X), Math.Min(dragStart.Y, e.Y), Math.Max(dragStart.X, e.X), Math.Max(dragStart.Y, e.Y));
            if (dragRect.Width > 4 || dragRect.Height > 4)
                SelectWords(doc.Words.Where(x => Map(x.Box).IntersectsWith(dragRect)));
            imagePanel.Invalidate();
        }

        void ImageUp(object sender, MouseEventArgs e)
        {
            dragging = false; dragRect = Rectangle.Empty;
            imagePanel.Invalidate();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            flash.Dispose();
            if (image != null) image.Dispose();
            base.OnFormClosed(e);
        }
    }
}
