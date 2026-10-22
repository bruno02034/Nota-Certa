using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DuplicidadeNfce
{
    static class Gui
    {
        public static int Executar(Opcoes op)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.DispatcherUnhandledException += (s, e) =>
            {
                MessageBox.Show(e.Exception.Message, Program.NomeSistema, MessageBoxButton.OK, MessageBoxImage.Error);
                e.Handled = true;
            };
            var janela = new Janela(op);
            return app.Run(janela.W);
        }
    }

    // Linha da grade principal.
    class LinhaGrid : INotifyPropertyChanged
    {
        bool marcada;
        public LinhaRelatorio Linha { get; private set; }
        public event PropertyChangedEventHandler PropertyChanged;

        public LinhaGrid(LinhaRelatorio l) { Atualizar(l); }

        public void Atualizar(LinhaRelatorio l)
        {
            Linha = l;
            foreach (var p in new[] { "Nota", "Pedido", "Data", "ValorSistema", "ValorSefaz", "Arquivo", "Status", "Tipo", "PodeMarcar" })
                Avisar(p);
        }

        public bool Marcada
        {
            get { return marcada; }
            set { marcada = value && PodeMarcar; Avisar("Marcada"); if (AoMarcar != null) AoMarcar(); }
        }
        public Action AoMarcar;
        public bool PodeMarcar { get { return Linha.Status != DuplicidadeNfce.Status.JaProcessada && Linha.Status != DuplicidadeNfce.Status.Copiado; } }
        public string Nota { get { return Linha.Serie + "/" + Linha.Numero; } }
        public string Pedido { get { return Linha.Pedido ?? "-"; } }
        public string Data { get { return DataCurta(Linha.DhEmiSistema); } }
        public string ValorSistema { get { return Linha.ValorSistema.HasValue ? "R$ " + Analisador.Moeda(Linha.ValorSistema) : "-"; } }
        public string ValorSefaz { get { return Linha.ValorSefaz.HasValue ? "R$ " + Analisador.Moeda(Linha.ValorSefaz) : "-"; } }
        public string Arquivo { get { return Linha.ArquivoEscolhido ?? ""; } }
        public string Status { get { return Linha.Status == DuplicidadeNfce.Status.AjusteChave ? "AJUSTE_CHAVE_BANCO" : Linha.Status; } }
        public string Tipo { get { return TipoDoStatus(Linha.Status); } }

        public static string TipoDoStatus(string s)
        {
            switch (s)
            {
                case DuplicidadeNfce.Status.Copiado: return "Copiado";
                case DuplicidadeNfce.Status.ACopiar: return "ACopiar";
                case DuplicidadeNfce.Status.AProcessar: return "AProcessar";
                case DuplicidadeNfce.Status.AjusteChave: return "Ajuste";
                case DuplicidadeNfce.Status.ErroCopia: return "Erro";
                case DuplicidadeNfce.Status.JaProcessada: return "Ja";
                default: return "Pendente";
            }
        }

        public static string DataCurta(string iso)
        {
            DateTimeOffset d;
            if (iso != null && DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
                return d.ToString("dd/MM/yy HH:mm");
            return iso ?? "-";
        }

        void Avisar(string p) { if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs(p)); }
    }

    class CandidatoView
    {
        public string Nome { get; set; }
        public string Pasta { get; set; }
        public string Motivo { get; set; }
        public bool Aceito { get; set; }
        public bool Avaliado { get; set; }
    }

    class HistoricoView
    {
        public string DataHora { get; set; }
        public string Nota { get; set; }
        public string ChaveAutorizada { get; set; }
        public string Arquivo { get; set; }
        public string CaminhoDestino { get; set; }
        public string Status { get; set; }
        public string Tipo { get; set; }
    }

    class ArquivoView
    {
        public string Nome { get; set; }
        public string Tipo { get; set; }
        public string Data { get; set; }
        public string Tamanho { get; set; }
        public string Caminho { get; set; }
    }

    class Janela
    {
        public readonly Window W;
        readonly Opcoes op;
        Config cfg;
        Motor motor;
        bool ocupado, recolhido, recolherManual;
        readonly ObservableCollection<LinhaGrid> linhas = new ObservableCollection<LinhaGrid>();
        ICollectionView visao;
        string ultimoResumo = "";
        int copiadasSessao;

        public Janela(Opcoes op)
        {
            this.op = op;
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("NotaCerta.Principal.xaml"))
                W = (Window)XamlReader.Load(s);
            EspacarTitulos(W);
            cfg = op.CarregarConfig();

            visao = CollectionViewSource.GetDefaultView(linhas);
            visao.Filter = Filtrar;
            G<DataGrid>("Grade").ItemsSource = visao;
            G<TextBlock>("TxtVersao").Text = "v" + Assembly.GetExecutingAssembly().GetName().Version.ToString(2) + (op.SemSefaz ? " · modo teste" : "");

            // Período
            var de = G<DatePicker>("DataDe");
            var ate = G<DatePicker>("DataAte");
            if (cfg.TodasDatas)
            {
                de.IsEnabled = ate.IsEnabled = false;
                de.Text = ate.Text = "";
            }
            else
            {
                de.SelectedDate = cfg.DataInicial ?? DateTime.Today.AddDays(-1);
                ate.SelectedDate = cfg.DataFinal ?? DateTime.Today;
            }

            // Eventos
            G<Button>("BtnListar").Click += (s, e) => Listar();
            G<Button>("BtnProcessar").Click += (s, e) => Processar();
            G<Button>("BtnAbrirPasta").Click += (s, e) => AbrirPastaDuplicidade();
            G<Button>("BtnExportar").Click += (s, e) => Exportar();
            G<Button>("BtnVerLog").Click += (s, e) => { if (motor != null) Abrir("notepad.exe", motor.ArqLog); };
            G<Button>("BtnMarcar").Click += (s, e) => { foreach (var l in linhas) l.Marcada = l.Linha.Status == Status.AProcessar; };
            G<Button>("BtnDesmarcar").Click += (s, e) => { foreach (var l in linhas) l.Marcada = false; };
            G<Button>("BtnRecolher").Click += (s, e) => { recolherManual = true; Recolher(); };
            // Telas pequenas (servidor via AnyDesk): recolhe o menu e estreita os detalhes para a grade caber.
            W.SizeChanged += (s, e) =>
            {
                double l = W.ActualWidth;
                ((ColumnDefinition)W.FindName("ColDetalhes")).Width = new GridLength(l < 1300 ? 300 : 360);
                // Em tela estreita o nome do arquivo sai da grade (continua nos detalhes) para o status caber.
                foreach (var c in G<DataGrid>("Grade").Columns.Where(c => "ARQUIVO".Equals(c.Header)))
                    c.Visibility = l < 1300 ? Visibility.Collapsed : Visibility.Visible;
                if (!recolherManual && (l < 1366) != recolhido) Recolher();
            };
            G<DataGrid>("Grade").SelectionChanged += (s, e) => MostrarDetalhes(G<DataGrid>("Grade").SelectedItem as LinhaGrid);
            G<TextBox>("CaixaPesquisa").TextChanged += (s, e) =>
            {
                G<TextBlock>("DicaPesquisa").Visibility = G<TextBox>("CaixaPesquisa").Text == "" ? Visibility.Visible : Visibility.Collapsed;
                visao.Refresh();
                AtualizarContagens();
            };
            foreach (var p in new[] { "PilTodas", "PilAProcessar", "PilCopiadas", "PilAjuste", "PilPendentes", "PilJa" })
                G<RadioButton>(p).Checked += (s, e) => visao.Refresh();
            G<RadioButton>("MenuDuplicidades").Checked += (s, e) => Pagina("PagDuplicidades", "Processamento");
            G<RadioButton>("MenuHistorico").Checked += (s, e) => { Pagina("PagHistorico", "Histórico"); CarregarHistorico(); };
            G<RadioButton>("MenuRelatorios").Checked += (s, e) => { Pagina("PagRelatorios", "Relatórios"); CarregarRelatorios(); };
            G<RadioButton>("MenuConfig").Checked += (s, e) => { Pagina("PagConfig", "Configurações"); CarregarConfigTela(); };
            G<Button>("BtnAtualizarHist").Click += (s, e) => CarregarHistorico();
            G<Button>("BtnAbrirRel").Click += (s, e) => AbrirRelatorioSelecionado();
            G<DataGrid>("GradeRelatorios").MouseDoubleClick += (s, e) => AbrirRelatorioSelecionado();
            G<Button>("BtnPastaRel").Click += (s, e) => { if (motor != null) Abrir("explorer.exe", motor.PastaRel); };
            G<Button>("BtnEditarIni").Click += (s, e) => Abrir("notepad.exe", op.ArqConfig);
            G<Button>("BtnRecarregar").Click += (s, e) =>
            {
                cfg = op.CarregarConfig();
                CarregarConfigTela();
                CarregarSelo();
                Listar();
            };
            W.Closed += (s, e) => { if (motor != null) motor.Dispose(); };

            if (op.Captura != null)
            {
                W.WindowStartupLocation = WindowStartupLocation.Manual;
                W.Left = -10000;
                W.ShowActivated = false;
                W.ShowInTaskbar = false;
                int largura;
                if (int.TryParse(Environment.GetEnvironmentVariable("NOTACERTA_LARGURA"), out largura))
                {
                    W.MinWidth = 800;
                    W.Width = largura;
                    W.Height = 830;
                }
            }
            W.Loaded += (s, e) =>
            {
                CarregarSelo();
                Listar();
            };
        }

        T G<T>(string nome) where T : class
        {
            var o = W.FindName(nome) as T;
            if (o == null) throw new Exception("elemento da tela não encontrado: " + nome);
            return o;
        }

        // Títulos dos cards com espaçamento entre letras (o WPF não tem letter-spacing).
        static void EspacarTitulos(DependencyObject raiz)
        {
            foreach (var filho in LogicalTreeHelper.GetChildren(raiz).OfType<DependencyObject>())
            {
                var t = filho as TextBlock;
                if (t != null && "titulo".Equals(t.Tag) && !string.IsNullOrEmpty(t.Text))
                    t.Text = string.Join(" ", t.Text.ToCharArray().Select(c => c.ToString()));
                EspacarTitulos(filho);
            }
        }

        void Pagina(string nome, string titulo)
        {
            foreach (var p in new[] { "PagDuplicidades", "PagHistorico", "PagRelatorios", "PagConfig" })
                G<FrameworkElement>(p).Visibility = p == nome ? Visibility.Visible : Visibility.Collapsed;
            ((System.Windows.Documents.Run)W.FindName("TxtBreadB")).Text = titulo;
            G<FrameworkElement>("PainelPilulas").Visibility = nome == "PagDuplicidades" ? Visibility.Visible : Visibility.Hidden;
        }

        void Recolher()
        {
            recolhido = !recolhido;
            G<Border>("Lateral").Width = recolhido ? 76 : 248;
            var vis = recolhido ? Visibility.Collapsed : Visibility.Visible;
            foreach (var n in new[] { "LogoTextos", "Rot1", "Rot2", "Rot3", "Rot4", "CaixaPesquisaGrid", "TxtVersao" })
                G<FrameworkElement>(n).Visibility = vis;
            ((ScaleTransform)W.FindName("EscalaRecolher")).ScaleX = recolhido ? -1 : 1;
            G<Button>("BtnRecolher").ToolTip = recolhido ? "Expandir menu" : "Recolher menu";
        }

        // ---------- Selo do certificado ----------
        void CarregarSelo()
        {
            var c = cfg;
            Task.Factory.StartNew(() =>
            {
                MetreIni.Carregar(c);
                string erro, arq;
                var cert = ClienteSefaz.CertificadoParaSelo(c, out erro, out arq);
                W.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var txt = G<TextBlock>("TxtSelo");
                    var fundo = G<Ellipse>("SeloFundo");
                    var icone = G<System.Windows.Shapes.Path>("SeloIcone");
                    if (cert == null)
                    {
                        txt.Text = "não encontrado";
                        G<FrameworkElement>("SeloCert").ToolTip = erro;
                        fundo.Fill = Pincel("#FDE8E8");
                        icone.Stroke = Pincel("#DC2626");
                        return;
                    }
                    int dias = (int)Math.Floor((cert.NotAfter - DateTime.Now).TotalDays);
                    txt.Inlines.Clear();
                    if (dias < 0) txt.Inlines.Add(new System.Windows.Documents.Run("vencido há " + (-dias) + " dias") { FontWeight = FontWeights.Bold, Foreground = Pincel("#DC2626") });
                    else
                    {
                        txt.Inlines.Add(new System.Windows.Documents.Run("restam "));
                        txt.Inlines.Add(new System.Windows.Documents.Run(dias.ToString()) { FontWeight = FontWeights.Bold, Foreground = Pincel(dias <= 30 ? "#DC2626" : "#111827") });
                        txt.Inlines.Add(new System.Windows.Documents.Run(" dias p/ vencer"));
                    }
                    if (dias <= 30) { fundo.Fill = Pincel("#FDE8E8"); icone.Stroke = Pincel("#DC2626"); }
                    G<FrameworkElement>("SeloCert").ToolTip = cert.Subject + "\nválido até " + cert.NotAfter.ToString("dd/MM/yyyy") + "\n" + arq;
                }));
            });
        }

        static Brush Pincel(string cor) { return (Brush)new BrushConverter().ConvertFromString(cor); }

        // ---------- Listar ----------
        void Listar()
        {
            if (ocupado) return;
            if (!cfg.TodasDatas)
            {
                var de = G<DatePicker>("DataDe").SelectedDate;
                var ate = G<DatePicker>("DataAte").SelectedDate;
                if (de == null || ate == null) { Aviso("Escolha a data inicial e a final."); return; }
                if (ate < de) { Aviso("A data final é anterior à inicial."); return; }
                cfg.DataInicial = de;
                cfg.DataFinal = ate;
            }
            Ocupado(true, "Lendo os XMLs do período...");
            if (motor != null) motor.Dispose();
            motor = null;
            var c = cfg;
            Task.Factory.StartNew(() =>
            {
                var m = new Motor(c, AppDomain.CurrentDomain.BaseDirectory, op.SemSefaz);
                m.Log.AoMostrar = MostrarStatus;
                m.Log.Info(Program.NomeSistema + " (interface) - " + DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss") + " - período " + m.DescricaoPeriodo);
                m.Varrer();
                return Tuple.Create(m, m.Listar());
            }).ContinueWith(t =>
            {
                Ocupado(false, null);
                if (t.IsFaulted) { Erro(t.Exception.InnerException.Message); return; }
                motor = t.Result.Item1;
                linhas.Clear();
                copiadasSessao = 0;
                foreach (var l in t.Result.Item2)
                {
                    var g = new LinhaGrid(l);
                    g.AoMarcar = AtualizarBotoes;
                    // Já corrigidas/copiadas e sem 539 ficam desmarcadas.
                    g.Marcada = l.Status == Status.AProcessar;
                    linhas.Add(g);
                }
                G<TextBlock>("GradeVazia").Visibility = linhas.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                AtualizarContagens();
                ultimoResumo = linhas.Count + " tentativa(s) com 539 em " + linhas.Select(x => x.Nota).Distinct().Count() + " nota(s) · " + motor.DescricaoPeriodo;
                MostrarStatus(ultimoResumo + (op.SemSefaz ? " · MODO TESTE: SEFAZ simulada, nada é copiado" : ""));
                G<ProgressBar>("Progresso").Value = 0;
                if (linhas.Count > 0) G<DataGrid>("Grade").SelectedIndex = 0;
                if (op.Captura != null) Processar();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // ---------- Processar: consulta SEFAZ, escolhe e copia ----------
        void Processar()
        {
            if (ocupado || motor == null) return;
            var chaves = new HashSet<string>(linhas.Where(l => l.Marcada).Select(l => l.Linha.ChaveSistema));
            if (chaves.Count == 0) { Aviso("Marque ao menos uma nota para processar."); return; }
            bool copiar = !op.SemSefaz;
            Ocupado(true, "Consultando a SEFAZ e escolhendo os arquivos...");
            var m = motor;
            var barra = G<ProgressBar>("Progresso");
            Task.Factory.StartNew(() =>
            {
                var res = m.Processar(t => chaves.Contains(t.Chave), new string[0], copiar,
                    (feitas, total) => W.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        barra.Value = total == 0 ? 100 : 100.0 * feitas / total;
                        G<TextBlock>("TxtStatus").Text = "Processando " + Math.Min(feitas + 1, total) + " de " + total + "...";
                    })));
                string csv = m.GravarRelatorio(res);
                return Tuple.Create(res, csv);
            }).ContinueWith(t =>
            {
                Ocupado(false, null);
                if (t.IsFaulted) { Erro(t.Exception.InnerException.Message); return; }
                foreach (var r in t.Result.Item1)
                {
                    var g = linhas.FirstOrDefault(x => x.Linha.ChaveSistema == r.ChaveSistema);
                    if (g == null) continue;
                    g.Atualizar(r);
                    g.Marcada = false;
                }
                copiadasSessao += t.Result.Item1.Count(r => r.Status == Status.Copiado);
                barra.Value = 100;
                visao.Refresh();
                AtualizarContagens();
                MostrarDetalhes(G<DataGrid>("Grade").SelectedItem as LinhaGrid);
                var res = t.Result.Item1;
                MostrarStatus("Processadas " + res.Count + ": " +
                    string.Join(", ", res.GroupBy(r => r.Status).Select(gr => gr.Count() + " " + gr.Key)) +
                    (copiar ? " · cópias em " + m.PastaCopia : " · modo teste: nada copiado") + " · relatório " + System.IO.Path.GetFileName(t.Result.Item2));
                if (op.Captura != null) Capturar();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void Ocupado(bool sim, string msg)
        {
            ocupado = sim;
            foreach (var b in new[] { "BtnListar", "BtnProcessar", "BtnRecarregar" })
                G<Button>(b).IsEnabled = !sim;
            G<DatePicker>("DataDe").IsEnabled = G<DatePicker>("DataAte").IsEnabled = !sim && !cfg.TodasDatas;
            W.Cursor = sim ? System.Windows.Input.Cursors.Wait : null;
            if (msg != null) { G<TextBlock>("TxtStatus").Text = msg; G<ProgressBar>("Progresso").Value = sim ? 5 : 0; }
            if (!sim) AtualizarBotoes();
        }

        void AtualizarBotoes()
        {
            int n = linhas.Count(l => l.Marcada);
            var b = G<Button>("BtnProcessar");
            b.Content = n > 0 ? "Processar marcadas (" + n + ")" : "Processar marcadas";
            if (!ocupado) b.IsEnabled = n > 0;
        }

        void MostrarStatus(string msg)
        {
            W.Dispatcher.BeginInvoke(new Action(() => G<TextBlock>("TxtStatus").Text = msg));
        }

        // ---------- Grade, filtros e contagens ----------
        bool Filtrar(object o)
        {
            var l = (LinhaGrid)o;
            string tipo = l.Tipo;
            if (Marcado("PilAProcessar") && tipo != "AProcessar" && tipo != "ACopiar") return false;
            if (Marcado("PilCopiadas") && tipo != "Copiado") return false;
            if (Marcado("PilAjuste") && tipo != "Ajuste") return false;
            if (Marcado("PilPendentes") && tipo != "Pendente" && tipo != "Erro") return false;
            if (Marcado("PilJa") && tipo != "Ja") return false;
            string q = G<TextBox>("CaixaPesquisa").Text.Trim();
            if (q == "") return true;
            var r = l.Linha;
            return new[] { l.Nota, r.Pedido, r.ChaveSistema, r.ChaveAutorizada, r.ArquivoEscolhido, r.Status }
                .Any(x => x != null && x.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        bool Marcado(string nome) { return G<RadioButton>(nome).IsChecked == true; }

        void AtualizarContagens()
        {
            Func<string[], int> conta = tipos => linhas.Count(l => tipos.Contains(l.Tipo));
            G<TextBlock>("NumEncontradas").Text = linhas.Count.ToString();
            G<TextBlock>("SubEncontradas").Text = linhas.Select(x => x.Nota).Distinct().Count() + " nota(s) no período";
            G<TextBlock>("NumCopiadas").Text = conta(new[] { "Copiado", "Ja" }).ToString();
            G<TextBlock>("SubCopiadas").Text = copiadasSessao + " nesta sessão · " + conta(new[] { "Ja" }) + " anteriores";
            G<TextBlock>("NumAjuste").Text = conta(new[] { "Ajuste" }).ToString();
            G<TextBlock>("NumPendentes").Text = conta(new[] { "Pendente", "Erro" }).ToString();
            Pilula("PilTodas", "Todas", linhas.Count);
            Pilula("PilAProcessar", "A processar", conta(new[] { "AProcessar", "ACopiar" }));
            Pilula("PilCopiadas", "Copiadas", conta(new[] { "Copiado" }));
            Pilula("PilAjuste", "Ajuste de chave", conta(new[] { "Ajuste" }));
            Pilula("PilPendentes", "Pendentes", conta(new[] { "Pendente", "Erro" }));
            Pilula("PilJa", "Já processadas", conta(new[] { "Ja" }));
            AtualizarBotoes();
        }

        void Pilula(string nome, string texto, int n) { G<RadioButton>(nome).Content = texto + "  " + n; }

        void MostrarDetalhes(LinhaGrid g)
        {
            G<FrameworkElement>("DetVazio").Visibility = g == null ? Visibility.Visible : Visibility.Collapsed;
            G<FrameworkElement>("DetPainel").Visibility = g == null ? Visibility.Collapsed : Visibility.Visible;
            if (g == null) return;
            var l = g.Linha;
            G<TextBlock>("DetNota").Text = "Nota " + g.Nota;
            G<ContentControl>("DetEtiqueta").Content = g;
            G<TextBlock>("DetPedido").Text = "Pedido " + g.Pedido + " · emitida em " + g.Data;
            G<TextBlock>("DetValorSistema").Text = g.ValorSistema;
            G<TextBlock>("DetValorSefaz").Text = g.ValorSefaz;
            G<TextBox>("DetChaveSistema").Text = l.ChaveSistema ?? "-";
            G<TextBox>("DetChaveAut").Text = l.ChaveAutorizada ?? "-";
            G<TextBlock>("DetSefaz").Text = string.IsNullOrEmpty(l.SituacaoSefaz) ? "ainda não consultada" : l.SituacaoSefaz;
            G<TextBox>("DetProt").Text = string.IsNullOrEmpty(l.NProtSefaz) && string.IsNullOrEmpty(l.DigValSefaz) ? "-" : (l.NProtSefaz ?? "-") + " / " + (l.DigValSefaz ?? "-");
            G<TextBox>("DetArquivo").Text = l.CaminhoOrigem == null ? "-" : l.CaminhoOrigem + (l.CaminhoDestino != null ? "\n→ " + l.CaminhoDestino : "");
            G<TextBlock>("DetMotivo").Text = l.Motivo ?? "";
            G<TextBlock>("DetOrigem").Text = string.IsNullOrEmpty(l.Origem) ? "-" : l.Origem;

            List<CandidatoView> cands;
            if (l.Sel != null && l.Sel.Candidatos.Count > 0)
                cands = l.Sel.Candidatos.Select(c => new CandidatoView
                {
                    Nome = System.IO.Path.GetFileName(c.Caminho), Pasta = System.IO.Path.GetDirectoryName(c.Caminho),
                    Motivo = (c.Aceito ? "Aceito: " : "Descartado: ") + c.Motivo + " · vNF " + Analisador.Moeda(c.VNF),
                    Aceito = c.Aceito, Avaliado = true
                }).ToList();
            else
                cands = l.ArquivosChaveAutorizada.Select(c => new CandidatoView
                {
                    Nome = System.IO.Path.GetFileName(c), Pasta = System.IO.Path.GetDirectoryName(c),
                    Motivo = l.Status == Status.JaProcessada ? "nota já processada (ver histórico)" : "será avaliado ao processar (comparação com o digVal da SEFAZ)",
                    Avaliado = false
                }).ToList();
            G<ItemsControl>("DetCandidatos").ItemsSource = cands;
        }

        // ---------- Botões ----------
        void AbrirPastaDuplicidade()
        {
            if (motor == null) return;
            string p = Directory.Exists(motor.PastaCopia) ? motor.PastaCopia : motor.Destino;
            if (!Directory.Exists(p)) { Aviso("A pasta " + motor.Destino + " ainda não existe: nenhuma nota foi copiada."); return; }
            Abrir("explorer.exe", p);
        }

        void Exportar()
        {
            if (linhas.Count == 0) { Aviso("Não há linhas para exportar."); return; }
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Relatório CSV (*.csv)|*.csv",
                FileName = "duplicidade_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".csv",
                InitialDirectory = motor != null ? motor.PastaRel : ""
            };
            if (dlg.ShowDialog(W) != true) return;
            Relatorio.GravarCsv(dlg.FileName, visao.Cast<LinhaGrid>().Select(l => l.Linha).ToList());
            MostrarStatus("Relatório exportado: " + dlg.FileName);
        }

        void CarregarHistorico()
        {
            if (motor == null) return;
            var h = new Historico(motor.Destino);
            G<TextBlock>("TxtHistCaminho").Text = h.Caminho + (File.Exists(h.Caminho) ? "" : " (ainda não existe)");
            G<DataGrid>("GradeHistorico").ItemsSource = h.Todos.AsEnumerable().Reverse().Select(r => new HistoricoView
            {
                DataHora = r.DataHora, Nota = r.Serie + "/" + r.Numero, ChaveAutorizada = r.ChaveAutorizada,
                Arquivo = r.Arquivo, CaminhoDestino = r.CaminhoDestino, Status = r.Status, Tipo = LinhaGrid.TipoDoStatus(r.Status)
            }).ToList();
        }

        void CarregarRelatorios()
        {
            if (motor == null) return;
            G<TextBlock>("TxtRelCaminho").Text = motor.PastaRel;
            G<DataGrid>("GradeRelatorios").ItemsSource = new DirectoryInfo(motor.PastaRel).GetFiles()
                .Where(f => f.Extension == ".csv" || f.Extension == ".log")
                .OrderByDescending(f => f.LastWriteTime)
                .Select(f => new ArquivoView
                {
                    Nome = f.Name, Caminho = f.FullName,
                    Tipo = f.Extension == ".csv" ? "Relatório CSV" : "Log",
                    Data = f.LastWriteTime.ToString("dd/MM/yyyy HH:mm:ss"),
                    Tamanho = (f.Length / 1024.0).ToString("0.0", CultureInfo.GetCultureInfo("pt-BR")) + " KB"
                }).ToList();
        }

        void AbrirRelatorioSelecionado()
        {
            var a = G<DataGrid>("GradeRelatorios").SelectedItem as ArquivoView;
            if (a == null) return;
            if (a.Caminho.EndsWith(".log")) Abrir("notepad.exe", a.Caminho);
            else Abrir(a.Caminho, null);
        }

        void CarregarConfigTela()
        {
            MetreIni.Carregar(cfg);
            var itens = new List<KeyValuePair<string, string>>
            {
                Par("Arquivo de configuração", op.ArqConfig),
                Par("Pasta dos XMLs", cfg.PastaRaiz),
                Par("Pasta das cópias (base)", cfg.PastaDestino),
                Par("Cópias de hoje", System.IO.Path.Combine(cfg.PastaDestino, DateTime.Today.ToString("yyyy-MM-dd"))),
                Par("Histórico", System.IO.Path.Combine(cfg.PastaDestino, Historico.NomeArquivo)),
                Par("Relatórios e logs", motor != null ? motor.PastaRel : cfg.PastaRelatorios),
                Par("UF / ambiente", cfg.UF + " / " + (cfg.Ambiente == 1 ? "1 - produção" : "2 - homologação")),
                Par("Consulta SEFAZ", SafeUrl()),
                Par("Certificado (.pfx)", MetreIni.Pfxs.Count > 0 ? string.Join("; ", MetreIni.Pfxs) + " (do INI do emissor)" : cfg.CertificadoPfx + " e " + cfg.PastaEmpresas + @"\*\Certificado"),
                Par("INIs do emissor lidos", MetreIni.Lidos.Count > 0 ? string.Join("; ", MetreIni.Lidos) : "nenhum encontrado em " + cfg.IniMetre),
                Par("Fontes de senha encontradas", MetreIni.Senhas.Count > 0 ? string.Join("; ", MetreIni.Senhas.Select(s => s.Origem)) : "nenhuma"),
                Par("Dias extras p/ candidatos", cfg.DiasExtrasCandidatos.ToString()),
                Par("Modo", op.SemSefaz ? "TESTE (--sem-sefaz): SEFAZ simulada, nada é copiado" : "normal")
            };
            G<ItemsControl>("ListaConfig").ItemsSource = itens;
        }

        string SafeUrl()
        {
            try { return ClienteSefaz.UrlConsulta(cfg); } catch (Exception ex) { return ex.Message; }
        }

        static KeyValuePair<string, string> Par(string k, string v) { return new KeyValuePair<string, string>(k, v ?? ""); }

        void Abrir(string programa, string arquivo)
        {
            try
            {
                if (arquivo == null) Process.Start(programa);
                else Process.Start(programa, "\"" + arquivo + "\"");
            }
            catch (Exception ex) { Erro(ex.Message); }
        }

        void Aviso(string msg)
        {
            if (op.Captura != null) return;
            MessageBox.Show(W, msg, Program.NomeSistema, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        void Erro(string msg)
        {
            G<TextBlock>("TxtStatus").Text = "ERRO: " + msg;
            if (op.Captura != null) { Capturar(); return; }
            MessageBox.Show(W, msg, Program.NomeSistema, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        // Desenvolvimento: salva a tela em PNG (--captura arquivo.png) e fecha.
        void Capturar()
        {
            var grade = G<DataGrid>("Grade");
            var alvo = linhas.FirstOrDefault(l => l.Linha.Status == Status.ACopiar || l.Linha.Status == Status.Copiado);
            if (alvo != null) grade.SelectedItem = alvo;
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
