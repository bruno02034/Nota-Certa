using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DuplicidadeNfce
{
    // Linha da grade de Notas Faltantes.
    class LinhaFaltanteGrid
    {
        public ItemFaltante Item { get; private set; }
        public LinhaFaltanteGrid(ItemFaltante i) { Item = i; }

        public string Nota { get { return Item.Nota; } }
        public string Data { get { return LinhaGrid.DataCurta(Item.DhEmi); } }
        public string Valor { get { return Item.Valor.HasValue ? "R$ " + Analisador.Moeda(Item.Valor) : "-"; } }
        public string Arquivo
        {
            get
            {
                if (Item.Destinos.Count > 0) return string.Join(" + ", Item.Destinos.Select(System.IO.Path.GetFileName));
                var a = Item.Arquivos.FirstOrDefault(x => x.Aceito) ?? Item.Arquivos.FirstOrDefault();
                return a == null ? "" : a.Nome;
            }
        }
        public string Copia { get { return Item.Copia; } }
        public string Status { get { return Item.Status; } }
        public string Tipo { get { return Item.Copia == CopiaFaltante.Erro ? "Erro" : TipoDoStatus(Item.Status); } }

        public static string TipoDoStatus(string s)
        {
            switch (s)
            {
                case StatusFaltante.Encontrada: return "Encontrada";
                case StatusFaltante.Cancelada: case StatusFaltante.CanceladaSemEvento: return "Cancelada";
                case StatusFaltante.Inutilizada: return "Inutilizada";
                case StatusFaltante.MesDiferente: return "MesDiferente";
                case StatusFaltante.NaoConsta: return "NaoConsta";
                case StatusFaltante.NaoEncontrada: return "NaoEncontrada";
                default: return "Pendente";
            }
        }
    }

    // Página "Notas Faltantes": entrada das faixas, busca, cópia e entrega.
    class TelaFaltantes
    {
        readonly Window W;
        readonly Func<Config> cfg;
        readonly Opcoes op;
        readonly ObservableCollection<LinhaFaltanteGrid> linhas = new ObservableCollection<LinhaFaltanteGrid>();
        readonly ICollectionView visao;
        MotorFaltantes motor;
        bool ocupado;

        public TelaFaltantes(Window w, Func<Config> cfg, Opcoes op)
        {
            W = w;
            this.cfg = cfg;
            this.op = op;
            visao = CollectionViewSource.GetDefaultView(linhas);
            visao.Filter = Filtrar;
            G<DataGrid>("GradeFaltantes").ItemsSource = visao;
            G<TextBox>("FalMes").Text = DateTime.Today.AddMonths(-1).ToString("MM/yyyy");

            G<TextBox>("FalFaixas").TextChanged += (s, e) => AtualizarTotal();
            G<Button>("BtnFalAdicionar").Click += (s, e) => Adicionar();
            G<Button>("BtnFalProcessar").Click += (s, e) => Processar(false);
            G<Button>("BtnFalAbrir").Click += (s, e) => AbrirPasta();
            G<Button>("BtnFalZip").Click += (s, e) => Zip();
            G<Button>("BtnFalExportar").Click += (s, e) => Exportar();
            G<Button>("BtnFalLog").Click += (s, e) => { if (motor != null) Abrir("notepad.exe", motor.ArqLog); };
            G<DataGrid>("GradeFaltantes").SelectionChanged += (s, e) => Detalhes(G<DataGrid>("GradeFaltantes").SelectedItem as LinhaFaltanteGrid);
            G<TextBox>("CaixaPesquisa").TextChanged += (s, e) => visao.Refresh();
            W.Closed += (s, e) => { if (motor != null) motor.Dispose(); };
            AtualizarTotal();
            Contagens();
        }

        T G<T>(string nome) where T : class
        {
            var o = W.FindName(nome) as T;
            if (o == null) throw new Exception("elemento da tela não encontrado: " + nome);
            return o;
        }

        List<FaixaFaltante> Faixas(List<string> invalidas)
        {
            return MotorFaltantes.LerFaixas(G<TextBox>("FalFaixas").Text, invalidas);
        }

        // Mostra quantos números serão buscados antes de processar.
        void AtualizarTotal()
        {
            var inv = new List<string>();
            var f = Faixas(inv);
            long total = MotorFaltantes.Total(f);
            var t = G<TextBlock>("FalTotal");
            t.Text = f.Count + " faixa(s) · " + total + " número(s) a buscar" + (inv.Count > 0 ? " · " + inv.Count + " linha(s) não reconhecida(s)" : "");
            t.Foreground = inv.Count > 0 || total > cfg().LimiteNumerosFaltantes ? Brushes.Firebrick : (Brush)W.FindResource("Azul");
            t.ToolTip = inv.Count > 0 ? "Não reconhecidas (use Inicial  Final  Série):\n" + string.Join("\n", inv) : null;
        }

        void Adicionar()
        {
            long ini, fim;
            int serie;
            string sIni = G<TextBox>("FalIni").Text.Trim(), sFim = G<TextBox>("FalFim").Text.Trim();
            if (sFim == "") sFim = sIni;
            if (!long.TryParse(sIni, out ini) || !long.TryParse(sFim, out fim) || !int.TryParse(G<TextBox>("FalSerie").Text.Trim(), out serie) || ini <= 0 || fim < ini)
            {
                Aviso("Informe Inicial, Final (maior ou igual ao Inicial) e Série.");
                return;
            }
            var caixa = G<TextBox>("FalFaixas");
            string atual = caixa.Text.TrimEnd();
            caixa.Text = (atual == "" ? "" : atual + Environment.NewLine) + ini + "  " + fim + "  " + serie + "  NFC-e";
            G<TextBox>("FalIni").Text = G<TextBox>("FalFim").Text = "";
            G<TextBox>("FalIni").Focus();
        }

        void Processar(bool capturar)
        {
            if (ocupado) return;
            var inv = new List<string>();
            var faixas = Faixas(inv);
            if (inv.Count > 0) { Aviso("Linha(s) não reconhecida(s):\n" + string.Join("\n", inv) + "\n\nUse: Inicial  Final  Série  (ex.: 501  512  7  NFC-e)"); return; }
            if (faixas.Count == 0) { Aviso("Informe ao menos uma faixa."); return; }
            DateTime mes;
            try
            {
                mes = MotorFaltantes.LerMes(G<TextBox>("FalMes").Text);
            }
            catch (Exception ex) { Aviso(ex.Message); return; }
            bool recopiar = G<CheckBox>("FalRecopiar").IsChecked == true;

            Ocupado(true, "Procurando os XMLs em " + cfg().PastaRaiz + "...");
            if (motor != null) motor.Dispose();
            motor = null;
            var c = cfg();
            var barra = G<ProgressBar>("FalProgresso");
            Task.Factory.StartNew(() =>
            {
                var m = new MotorFaltantes(c, AppDomain.CurrentDomain.BaseDirectory);
                m.Log.AoMostrar = Status;
                try
                {
                    m.Executar(mes, faixas, true, recopiar, (feitos, total) => W.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        barra.Value = total == 0 ? 100 : 100.0 * feitos / total;
                        G<TextBlock>("FalStatus").Text = "Conferindo " + Math.Min(feitos + 1, total) + " de " + total + "...";
                    })));
                }
                catch { m.Dispose(); throw; }
                return m;
            }).ContinueWith(t =>
            {
                Ocupado(false, null);
                if (t.IsFaulted) { Erro(t.Exception.InnerException.Message); if (capturar) Salvar(); return; }
                motor = t.Result;
                linhas.Clear();
                foreach (var i in motor.Itens) linhas.Add(new LinhaFaltanteGrid(i));
                G<TextBlock>("GradeFaltantesVazia").Visibility = linhas.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                barra.Value = 100;
                Contagens();
                if (linhas.Count > 0) G<DataGrid>("GradeFaltantes").SelectedIndex = 0;
                var emit = G<TextBlock>("FalEmitente");
                emit.Text = motor.DescricaoEmitente;
                emit.Foreground = motor.Cnpj == null ? Brushes.Firebrick : Brushes.Black;
                var itens = motor.Itens;
                int copiados = itens.Count(i => i.Copia == CopiaFaltante.Copiado);
                Status(itens.Count + " número(s): " + string.Join(", ", itens.GroupBy(i => i.Status).Select(g => g.Count() + " " + g.Key)) +
                    " · " + copiados + " copiado(s) para " + motor.PastaExecucao);
                if (capturar) Salvar();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void Ocupado(bool sim, string msg)
        {
            ocupado = sim;
            foreach (var b in new[] { "BtnFalProcessar", "BtnFalZip", "BtnFalAdicionar" })
                G<Button>(b).IsEnabled = !sim;
            W.Cursor = sim ? System.Windows.Input.Cursors.Wait : null;
            if (msg != null) { G<TextBlock>("FalStatus").Text = msg; G<ProgressBar>("FalProgresso").Value = sim ? 5 : 0; }
        }

        void Status(string msg)
        {
            W.Dispatcher.BeginInvoke(new Action(() => G<TextBlock>("FalStatus").Text = msg));
        }

        bool Filtrar(object o)
        {
            string q = G<TextBox>("CaixaPesquisa").Text.Trim();
            if (q == "") return true;
            var l = (LinhaFaltanteGrid)o;
            return new[] { l.Nota, l.Item.Numero.ToString(), l.Item.Chave, l.Status, l.Arquivo }
                .Any(x => x != null && x.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        void Contagens()
        {
            Func<Func<ItemFaltante, bool>, string> conta = f => linhas.Count(l => f(l.Item)).ToString();
            G<TextBlock>("FalNumSolicitadas").Text = linhas.Count.ToString();
            G<TextBlock>("FalNumEncontradas").Text = conta(i => i.Status == StatusFaltante.Encontrada);
            G<TextBlock>("FalNumCanceladas").Text = conta(i => i.Status == StatusFaltante.Cancelada || i.Status == StatusFaltante.CanceladaSemEvento);
            G<TextBlock>("FalNumInutilizadas").Text = conta(i => i.Status == StatusFaltante.Inutilizada);
            G<TextBlock>("FalNumNaoEncontradas").Text = conta(i => i.Status == StatusFaltante.NaoEncontrada);
            G<TextBlock>("FalNumProblema").Text = conta(i => i.Status == StatusFaltante.MesDiferente || i.Status == StatusFaltante.NaoConsta ||
                i.Status == StatusFaltante.Pendente || i.Copia == CopiaFaltante.Erro);
        }

        void Detalhes(LinhaFaltanteGrid g)
        {
            G<FrameworkElement>("FalDetVazio").Visibility = g == null ? Visibility.Visible : Visibility.Collapsed;
            G<FrameworkElement>("FalDetPainel").Visibility = g == null ? Visibility.Collapsed : Visibility.Visible;
            if (g == null) return;
            var i = g.Item;
            G<TextBlock>("FalDetNota").Text = "Nota " + i.Nota;
            G<ContentControl>("FalDetEtiqueta").Content = g;
            G<TextBlock>("FalDetEmissao").Text = i.DhEmi == null ? "sem data de emissão" : "emitida em " + g.Data + " · " + g.Valor;
            G<TextBlock>("FalDetEmitente").Text = i.Cnpj == null ? "não identificado" : MotorFaltantes.FormatarCnpj(i.Cnpj) + (i.Emitente != null ? " - " + i.Emitente : "");
            G<TextBox>("FalDetChave").Text = i.Chave ?? "-";
            G<TextBlock>("FalDetSefaz").Text = string.IsNullOrEmpty(i.SituacaoSefaz) ? "não consultada (nenhuma chave para consultar)" : i.SituacaoSefaz;
            G<TextBox>("FalDetProt").Text = string.IsNullOrEmpty(i.NProt) && string.IsNullOrEmpty(i.DigVal) ? "-" : (i.NProt ?? "-") + " / " + (i.DigVal ?? "-");
            G<TextBlock>("FalDetMotivo").Text = i.Motivo ?? "";
            G<TextBox>("FalDetDestinos").Text = i.Destinos.Count > 0 ? string.Join("\n", i.Destinos) : (i.Copia == "" ? "nada copiado" : i.Copia);
            var arqs = i.Arquivos.Select(a => new CandidatoView
            {
                Nome = a.Nome, Pasta = System.IO.Path.GetDirectoryName(a.Caminho),
                Motivo = (a.Aceito ? "Aceito: " : "") + (a.Motivo ?? ""), Aceito = a.Aceito,
                // Sem resposta da SEFAZ o candidato fica em aberto (?), não descartado.
                Avaliado = a.Aceito || a.Motivo == null || !a.Motivo.StartsWith("candidato")
            }).ToList();
            G<ItemsControl>("FalDetArquivos").ItemsSource = arqs;
            G<FrameworkElement>("FalDetSemArquivos").Visibility = arqs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        string PastaEntrega { get { return motor != null && motor.PastaExecucao != null && Directory.Exists(motor.PastaExecucao) ? motor.PastaExecucao : null; } }

        void AbrirPasta()
        {
            string p = PastaEntrega ?? System.IO.Path.GetFullPath(cfg().PastaNotasFaltantes);
            if (!Directory.Exists(p)) { Aviso("A pasta " + p + " ainda não existe: nenhuma busca foi copiada."); return; }
            Abrir("explorer.exe", p);
        }

        void Zip()
        {
            if (PastaEntrega == null) { Aviso("Processe uma busca antes de gerar o zip."); return; }
            try
            {
                string zip = MotorFaltantes.GerarZip(PastaEntrega);
                motor.Log.Info("Zip gerado: " + zip);
                Status("Zip gerado: " + zip);
                Process.Start("explorer.exe", "/select,\"" + zip + "\"");
            }
            catch (Exception ex) { Erro(ex.Message); }
        }

        void Exportar()
        {
            if (linhas.Count == 0) { Aviso("Não há linhas para exportar."); return; }
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Relatório CSV (*.csv)|*.csv",
                FileName = "notas_faltantes_" + (motor.Cnpj ?? "sem_emitente") + "_" + motor.Mes.ToString("yyyy-MM") + ".csv",
                InitialDirectory = PastaEntrega ?? motor.PastaRel
            };
            if (dlg.ShowDialog(W) != true) return;
            MotorFaltantes.GravarCsv(dlg.FileName, visao.Cast<LinhaFaltanteGrid>().Select(l => l.Item));
            Status("Relatório exportado: " + dlg.FileName);
        }

        void Abrir(string programa, string arquivo)
        {
            try { Process.Start(programa, "\"" + arquivo + "\""); }
            catch (Exception ex) { Erro(ex.Message); }
        }

        void Aviso(string msg)
        {
            if (op.Captura != null) { G<TextBlock>("FalStatus").Text = msg; return; }
            MessageBox.Show(W, msg, Program.NomeSistema, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        void Erro(string msg)
        {
            G<TextBlock>("FalStatus").Text = "ERRO: " + msg;
            if (op.Captura != null) return;
            MessageBox.Show(W, msg, Program.NomeSistema, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        // Desenvolvimento (--captura com NOTACERTA_PAGINA=faltantes): preenche com NOTACERTA_FALT_MES e
        // NOTACERTA_FALT_FAIXAS (arquivo), processa e salva a tela em PNG.
        public void Capturar()
        {
            G<TextBox>("FalMes").Text = Environment.GetEnvironmentVariable("NOTACERTA_FALT_MES") ?? "";
            string arq = Environment.GetEnvironmentVariable("NOTACERTA_FALT_FAIXAS");
            if (arq != null && File.Exists(arq)) G<TextBox>("FalFaixas").Text = File.ReadAllText(arq);
            Processar(true);
        }

        void Salvar()
        {
            var grade = G<DataGrid>("GradeFaltantes");
            int sel;
            if (int.TryParse(Environment.GetEnvironmentVariable("NOTACERTA_FALT_SELECIONAR"), out sel) && sel < linhas.Count) grade.SelectedIndex = sel;
            W.Dispatcher.BeginInvoke(new Action(() =>
            {
                var raiz = (FrameworkElement)W.Content;
                var bmp = new RenderTargetBitmap((int)raiz.ActualWidth, (int)raiz.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                var fundo = new DrawingVisual();
                using (var dc = fundo.RenderOpen()) dc.DrawRectangle(W.Background, null, new Rect(0, 0, raiz.ActualWidth, raiz.ActualHeight));
                bmp.Render(fundo);
                bmp.Render(raiz);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using (var f = File.Create(op.Captura)) enc.Save(f);
                W.Close();
            }), DispatcherPriority.ApplicationIdle);
        }
    }
}
