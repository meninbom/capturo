using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Capturo;

static class OcrSelectionSmoke
{
    static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
        Console.WriteLine("OK: " + message);
    }

    static void Key(RichTextBox box, Keys keys)
    {
        typeof(Control).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(box, new object[] { new KeyEventArgs(keys) });
    }

    [STAThread]
    static int Main()
    {
        Application.EnableVisualStyles();
        var previous = Clipboard.GetDataObject();
        try
        {
            using (var bitmap = new Bitmap(800, 150))
            using (var graphics = Graphics.FromImage(bitmap))
            using (var font = new Font("Segoe UI", 32))
            {
                graphics.Clear(Color.White);
                graphics.DrawString("CAPTURO TEXTO TESTE", font, Brushes.Black, 20, 35);
                Check(Ocr.Recognize(bitmap).Contains("CAPTURO"), "OCR real reconhece imagem de teste");
            }
            // janela com a imagem: as palavras da imagem selecionam o texto
            using (var img = new Bitmap(640, 120))
            {
                using (var g = Graphics.FromImage(img)) using (var f = new Font("Segoe UI", 20))
                { g.Clear(Color.White); g.DrawString("Pedido 48213 aprovado", f, Brushes.Black, 20, 30); }
                var doc = Ocr.Read(img, null);
                using (var form = new OcrTextForm(new Bitmap(img)))
                {
                    form.Show(); Application.DoEvents();
                    form.SetResult(doc); Application.DoEvents();
                    var box = (RichTextBox)form.Controls.Find("RecognizedText", true)[0];
                    Check(box.Text.Contains("48213"), "janela com imagem mostra o texto lido: " + box.Text);
                    var word = System.Linq.Enumerable.First(doc.Words, w => w.Text == "48213");
                    typeof(OcrTextForm).GetMethod("SelectWords", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(form, new object[] { new[] { word } });
                    Check(box.SelectedText == "48213", "clicar na palavra da imagem seleciona o texto dela");
                }
            }
            const string sample = "Nome\tValor\r\nJoão Silva\t123,45\r\nObservação final";
            Clipboard.SetText("clipboard anterior");
            using (var form = new OcrTextForm(sample))
            {
                form.Show();
                Application.DoEvents();
                var box = (RichTextBox)form.Controls.Find("RecognizedText", true)[0];
                var selection = (UiButton)form.Controls.Find("CopySelection", true)[0];
                var all = (UiButton)form.Controls.Find("CopyAll", true)[0];
                Check(Clipboard.GetText() == "clipboard anterior", "abrir resultado preserva clipboard");
                Check(!box.ReadOnly && !selection.Enabled, "texto editável (para corrigir o OCR) e cópia desabilitada sem seleção");
                Key(box, Keys.Control | Keys.C);
                Check(Clipboard.GetText() == "clipboard anterior", "Ctrl+C sem seleção preserva clipboard");
                box.Select(box.Text.IndexOf("João"), "João Silva".Length);
                Application.DoEvents();
                Check(selection.Enabled, "selecionar trecho habilita botão");
                selection.PerformClick();
                Check(Clipboard.GetText() == "João Silva", "botão copia apenas seleção com acentos");
                box.Select(box.Text.IndexOf("123,45"), 6);
                Key(box, Keys.Control | Keys.C);
                Check(Clipboard.GetText() == "123,45", "Ctrl+C copia outro trecho");
                Key(box, Keys.Control | Keys.A);
                Check(box.SelectionLength == box.TextLength, "Ctrl+A seleciona tudo");
                box.Select(0, 0);
                all.PerformClick();
                Check(Clipboard.GetText() == box.Text && Clipboard.GetText().Contains("\t"), "copiar tudo preserva linhas e tabulações");
                Check(form.Visible, "resultado permanece aberto após copiar");
                form.Size = form.MinimumSize;
                Application.DoEvents();
                Check(all.Parent.ClientRectangle.Contains(all.Bounds), "botões acessíveis no tamanho mínimo");
                form.CancelButton.PerformClick();
                Check(form.IsDisposed, "fechar encerra janela");
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            if (previous != null) Clipboard.SetDataObject(previous, true);
            else Clipboard.Clear();
        }
    }
}
