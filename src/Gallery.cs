// Histórico local das capturas e a janela de galeria.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Capturo
{
    // Guarda uma cópia de cada captura finalizada (copiada, salva, fixada, enviada...) em %APPDATA%\Capturo\historico.
    static class CaptureHistory
    {
        public const int Keep = 60;
        public static string Dir { get { return Path.Combine(Settings.Dir, "historico"); } }
        static readonly object gate = new object();

        public static void Add(Bitmap bmp)
        {
            if (!TrayApp.S.KeepHistory) return;
            Bitmap copy;
            try { copy = new Bitmap(bmp); } catch { return; }
            string name = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff") + ".png";
            new Thread(() =>
            {
                try
                {
                    lock (gate)
                    {
                        Directory.CreateDirectory(Dir);
                        copy.Save(Path.Combine(Dir, name), ImageFormat.Png);
                        var files = Files();
                        for (int i = Keep; i < files.Count; i++) try { File.Delete(files[i]); } catch { }
                    }
                }
                catch { }
                finally { copy.Dispose(); }
            }) { IsBackground = true }.Start();
        }

        // Mais recentes primeiro.
        public static List<string> Files()
        {
            var list = new List<string>();
            try { if (Directory.Exists(Dir)) list.AddRange(Directory.GetFiles(Dir, "*.png")); } catch { }
            list.Sort((a, b) => string.CompareOrdinal(Path.GetFileName(b), Path.GetFileName(a)));
            return list;
        }

        public static void Clear()
        {
            lock (gate) foreach (var f in Files()) try { File.Delete(f); } catch { }
        }

        // Carrega sem travar o arquivo.
        public static Bitmap Load(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var img = Image.FromStream(fs))
                return new Bitmap(img);
        }

        public static string Label(string path)
        {
            DateTime t;
            string n = Path.GetFileNameWithoutExtension(path);
            if (DateTime.TryParseExact(n, "yyyy-MM-dd_HH-mm-ss-fff", null, System.Globalization.DateTimeStyles.None, out t))
            {
                if (t.Date == DateTime.Today) return "Hoje, " + t.ToString("HH:mm");
                if (t.Date == DateTime.Today.AddDays(-1)) return "Ontem, " + t.ToString("HH:mm");
                return t.ToString("dd/MM, HH:mm");
            }
            return n;
        }
    }

    class GalleryForm : Form
    {
        readonly TrayApp app;
        readonly ListView list;
        readonly ImageList thumbs;
        readonly Label info;
        readonly float k;
        volatile bool closing;
        volatile int gen;   // cada recarga invalida a anterior

        int D(float v) { return (int)Math.Round(v * k); }

        public GalleryForm(TrayApp app)
        {
            this.app = app;
            using (var g = CreateGraphics()) k = Math.Max(1f, g.DpiX / 96f);
            Text = "Capturo - Galeria";
            Icon = Art.AppIcon();
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Segoe UI", 12f * k, FontStyle.Regular, GraphicsUnit.Pixel);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(D(900), D(600));
            MinimumSize = new Size(D(420), D(320));
            KeyPreview = true;

            var top = new Panel { Dock = DockStyle.Top, Height = D(48), Padding = new Padding(D(12), D(10), D(12), D(8)) };
            info = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = SystemColors.GrayText };
            var clear = new Button { Text = "Limpar histórico", Dock = DockStyle.Right, Width = D(130) };
            var folder = new Button { Text = "Abrir pasta", Dock = DockStyle.Right, Width = D(110) };
            var gap = new Panel { Dock = DockStyle.Right, Width = D(8) };
            top.Controls.Add(info); top.Controls.Add(folder); top.Controls.Add(gap); top.Controls.Add(clear);
            folder.Click += (s, e) => { Directory.CreateDirectory(CaptureHistory.Dir); Services.Open(CaptureHistory.Dir); };
            clear.Click += (s, e) =>
            {
                if (MessageBox.Show(this, "Apagar todas as capturas do histórico?", "Capturo", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                CaptureHistory.Clear();
                Reload();
            };

            thumbs = new ImageList { ImageSize = new Size(Math.Min(256, D(200)), Math.Min(256, D(124))), ColorDepth = ColorDepth.Depth32Bit };
            list = new ListView
            {
                Dock = DockStyle.Fill, View = View.LargeIcon, LargeImageList = thumbs, MultiSelect = true,
                BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(246, 247, 250)
            };
            list.ItemActivate += (s, e) => OpenSelected();

            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("Copiar", null, (s, e) => CopySelected()) { ShortcutKeyDisplayString = "Ctrl+C" });
            menu.Items.Add(new ToolStripMenuItem("Fixar na tela", null, (s, e) => PinSelected()) { ShortcutKeyDisplayString = "Ctrl+T" });
            menu.Items.Add(new ToolStripMenuItem("Copiar texto (OCR)", null, (s, e) =>
            {
                var p = Selected();
                if (p.Count == 0) return;
                try { app.CopyText(CaptureHistory.Load(p[0])); } catch (Exception ex) { app.Notify("Erro ao ler o texto", ex.Message, null); }
            }) { ShortcutKeyDisplayString = "Ctrl+O" });
            menu.Items.Add(new ToolStripMenuItem("Abrir", null, (s, e) => OpenSelected()) { ShortcutKeyDisplayString = "Enter" });
            menu.Items.Add(new ToolStripMenuItem("Salvar como...", null, (s, e) => SaveSelected()) { ShortcutKeyDisplayString = "Ctrl+S" });
            menu.Items.Add(new ToolStripMenuItem("Mostrar na pasta", null, (s, e) =>
            {
                var p = Selected();
                if (p.Count > 0) try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + p[0] + "\""); } catch { }
            }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Excluir", null, (s, e) => DeleteSelected()) { ShortcutKeyDisplayString = "Del" });
            menu.Opening += (s, e) => { if (list.SelectedItems.Count == 0) e.Cancel = true; };
            list.ContextMenuStrip = menu;

            Controls.Add(list);
            Controls.Add(top);
            Shown += (s, e) => { Reload(); Native.ForceForeground(Handle); };
        }

        List<string> Selected()
        {
            var r = new List<string>();
            foreach (ListViewItem it in list.SelectedItems) r.Add((string)it.Tag);
            return r;
        }

        void Reload()
        {
            list.Items.Clear();
            thumbs.Images.Clear();
            var files = CaptureHistory.Files();
            info.Text = files.Count == 0
                ? (TrayApp.S.KeepHistory ? "Nenhuma captura ainda. Tudo o que você copiar, salvar, fixar ou enviar aparece aqui."
                                         : "O histórico está desligado (Configurações → Geral).")
                : files.Count + " capturas recentes · duplo clique abre · botão direito para mais opções";
            Size ts = thumbs.ImageSize;
            int my = ++gen;
            new Thread(() =>
            {
                foreach (var f in files)
                {
                    if (closing || my != gen) return;
                    Bitmap th;
                    try { th = Thumb(f, ts); } catch { continue; }
                    string file = f;
                    try
                    {
                        BeginInvoke((Action)(() =>
                        {
                            if (closing || my != gen) { th.Dispose(); return; }
                            thumbs.Images.Add(th); th.Dispose(); // a ImageList guarda a própria cópia
                            list.Items.Add(new ListViewItem(CaptureHistory.Label(file), thumbs.Images.Count - 1) { Tag = file });
                        }));
                    }
                    catch { th.Dispose(); return; }
                }
            }) { IsBackground = true }.Start();
        }

        static Bitmap Thumb(string path, Size ts)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var img = Image.FromStream(stream))
            {
                var b = new Bitmap(ts.Width, ts.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(b))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    float sc = Math.Min((float)(ts.Width - 4) / img.Width, (float)(ts.Height - 4) / img.Height);
                    sc = Math.Min(sc, 1f);
                    float w = img.Width * sc, h = img.Height * sc;
                    var r = new RectangleF((ts.Width - w) / 2, (ts.Height - h) / 2, w, h);
                    using (var sh = new SolidBrush(Color.FromArgb(40, 0, 0, 0))) g.FillRectangle(sh, r.X + 1, r.Y + 2, r.Width, r.Height);
                    g.DrawImage(img, r);
                    using (var p = new Pen(Color.FromArgb(50, 0, 0, 0))) g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
                }
                return b;
            }
        }

        void CopySelected()
        {
            var p = Selected();
            if (p.Count == 0) return;
            try { using (var b = CaptureHistory.Load(p[0])) app.CopyImage(b); } catch (Exception ex) { app.Notify("Erro ao copiar", ex.Message, null); }
        }

        void PinSelected()
        {
            foreach (var f in Selected())
            {
                try
                {
                    var b = CaptureHistory.Load(f);
                    var c = Cursor.Position;
                    app.Pin(b, new Point(c.X - b.Width / 2, c.Y - b.Height / 2));
                }
                catch { }
            }
        }

        void OpenSelected()
        {
            var p = Selected();
            if (p.Count > 0) Services.Open(p[0]);
        }

        void SaveSelected()
        {
            var p = Selected();
            if (p.Count == 0) return;
            try { using (var b = CaptureHistory.Load(p[0])) app.SaveImage(b, this); } catch (Exception ex) { app.Notify("Erro ao salvar", ex.Message, null); }
        }

        void DeleteSelected()
        {
            var items = new List<ListViewItem>();
            foreach (ListViewItem it in list.SelectedItems) items.Add(it);
            foreach (var it in items)
            {
                try { File.Delete((string)it.Tag); list.Items.Remove(it); } catch { }
            }
            info.Text = list.Items.Count + " capturas recentes · duplo clique abre · botão direito para mais opções";
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) Close();
            else if (e.KeyCode == Keys.Delete) DeleteSelected();
            else if (e.Control && e.KeyCode == Keys.C) CopySelected();
            else if (e.Control && e.KeyCode == Keys.T) PinSelected();
            else if (e.Control && e.KeyCode == Keys.O)
            {
                var p = Selected();
                if (p.Count > 0) try { app.CopyText(CaptureHistory.Load(p[0])); } catch (Exception ex) { app.Notify("Erro ao ler o texto", ex.Message, null); }
            }
            else if (e.Control && e.KeyCode == Keys.S) SaveSelected();
            else if (e.Control && e.KeyCode == Keys.A) foreach (ListViewItem it in list.Items) it.Selected = true;
            else if (e.KeyCode == Keys.F5) Reload();
            else return;
            e.Handled = true;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            closing = true;
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            thumbs.Dispose();
            base.OnFormClosed(e);
        }
    }
}
