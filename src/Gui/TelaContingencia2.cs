using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace DuplicidadeNfce
{
    // Linha da grade de Contingência 2.
    class LinhaCont2Grid : INotifyPropertyChanged
    {
        public LinhaContingencia C { get; private set; }
        public LinhaCont2Grid(LinhaContingencia c) { C = c; }
        public event PropertyChangedEventHandler PropertyChanged;

        bool marcada;
        public bool Marcada
        {
            get { return marcada; }
            set { marcada = value && PodeMarcar; Avisar("Marcada"); if (AoMarcar != null) AoMarcar(); }
        }
        public Action AoMarcar;
        public bool PodeMarcar { get { return Resultado == null; } }
        public string Resultado;   // depois de "Voltar para Contingência": o que aconteceu com a linha

        public string Nota { get { return C.Serie + "/" + C.Numero; } }
        public string Pedido { get { return C.Pedido ?? "-"; } }
        public string Emissao { get { return Data(C.DtEmissao); } }
        public string Contingencia { get { return Data(C.DtContingencia); } }
        public string Valor { get { return C.Valor.HasValue ? "R$ " + Analisador.Moeda(C.Valor) : "-"; } }
        public string Situacao { get { return Resultado ?? C.Situacao; } }
        public string Chave { get { return C.Chave ?? "-"; } }
        public string Motivo { get { return C.Motivo; } }

        public void Atualizar(string resultado)
        {
            Resultado = resultado;
            marcada = false;
            foreach (var p in new[] { "Marcada", "PodeMarcar", "Situacao" }) Avisar(p);
        }

        void Avisar(string p) { if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs(p)); }
        static string Data(DateTime? d) { return d.HasValue ? d.Value.ToString("dd/MM/yy HH:mm") : "-"; }
    }

    // Página "Contingência 2": lista as notas que o cliente escondeu da aba Contingência do Metre e, nas marcadas,
    // volta a situação para a contingência normal. Busca no banco só quando o usuário clica em Listar.
    class TelaContingencia2
    {
        readonly Window W;
        readonly Func<Config> cfg;
        readonly ObservableCollection<LinhaCont2Grid> linhas = new ObservableCollection<LinhaCont2Grid>();
        readonly ICollectionView visao;
        bool ocupado;

        public TelaContingencia2(Window w, Func<Config> cfg)
        {
            W = w;
            this.cfg = cfg;
            visao = CollectionViewSource.GetDefaultView(linhas);
            visao.Filter = Filtrar;
            G<DataGrid>("GradeCont2").ItemsSource = visao;
            G<Button>("BtnListarCont2").Click += (s, e) => Listar();
            G<Button>("BtnVoltarCont2").Click += (s, e) => Voltar();
            G<Button>("BtnMarcarCont2").Click += (s, e) => { foreach (LinhaCont2Grid l in visao) l.Marcada = true; };
            G<Button>("BtnDesmarcarCont2").Click += (s, e) => { foreach (var l in linhas) l.Marcada = false; };
            G<TextBox>("CaixaPesquisa").TextChanged += (s, e) => visao.Refresh();
            G<TextBox>("Cont2Numeros").TextChanged += (s, e) => Refiltrar();
            G<DatePicker>("Cont2De").SelectedDateChanged += (s, e) => Refiltrar();
            G<DatePicker>("Cont2Ate").SelectedDateChanged += (s, e) => Refiltrar();
            AtualizarBotao();
        }

        T G<T>(string nome) where T : class { return Gui.Elemento<T>(W, nome); }

        void Listar()
        {
            if (ocupado) return;
            var c = cfg();
            if (string.IsNullOrEmpty(c.DbSenha))
            {
                Status(c.ErroSenhaProtegida ?? "Senha do banco não informada: use o botão \"Senha do banco\" em Configurações.");
                return;
            }
            Ocupado(true);
            Status("Consultando o banco " + c.DbBanco + "...");
            Task.Factory.StartNew(() => Banco.LerContingencia2(c)).ContinueWith(t =>
            {
                Ocupado(false);
                if (t.IsFaulted) { Status("ERRO: " + t.Exception.InnerException.Message); return; }
                linhas.Clear();
                foreach (var l in t.Result) { var g = new LinhaCont2Grid(l); g.AoMarcar = AtualizarBotao; linhas.Add(g); }
                G<TextBlock>("GradeCont2Vazia").Visibility = linhas.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                AtualizarBotao();
                Status(linhas.Count == 0 ? "Nenhuma nota em Contingência 2." :
                    linhas.Count + " nota(s): " + string.Join(", ", t.Result.GroupBy(x => x.Situacao).Select(g => g.Count() + " em \"" + g.Key + "\"")) +
                    " · listado em " + DateTime.Now.ToString("dd/MM/yyyy HH:mm"));
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // Só as marcadas que estão visíveis no filtro: mudar o filtro não leva junto o que ficou escondido.
        List<LinhaCont2Grid> Marcadas() { return visao.Cast<LinhaCont2Grid>().Where(l => l.Marcada).ToList(); }

        void Voltar()
        {
            if (ocupado) return;
            var sel = Marcadas();
            if (sel.Count == 0) { Status("Marque as notas que devem voltar para a contingência."); return; }
            var c = cfg();
            string lista = string.Join("\n", sel.Take(20).Select(l => l.Nota + " · pedido " + l.Pedido + " · " + l.Emissao + " · " + l.Valor + " (" + l.C.Situacao + ")")) +
                (sel.Count > 20 ? "\n... e mais " + (sel.Count - 20) : "");
            if (MessageBox.Show(W, "Voltar " + sel.Count + " nota(s) para a contingência normal do Metre?\n\n" + lista + "\n\n" +
                "Só o campo situacao muda, para \"" + AjusteChave.SituacaoMetre + "\" (o texto que o Metre já usa), com backup de cada linha e transação própria. " +
                "As notas voltam à aba CONTIGÊNCIA do Metre e podem ser transmitidas.",
                Program.NomeSistema, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            string dir = AppDomain.CurrentDomain.BaseDirectory;
            string rel = c.PastaRelatoriosEm(dir);
            Directory.CreateDirectory(rel);
            string arqLog = Path.Combine(rel, "contingencia2_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");
            string backups = Path.Combine(dir, "backups");
            Ocupado(true);
            Status("Voltando 1 de " + sel.Count + "...");
            Task.Factory.StartNew(() =>
            {
                var res = new List<string>();   // null = voltou; texto = erro
                using (var log = new Log(arqLog))
                {
                    log.Info(Program.NomeSistema + " - Contingência 2 -> contingência - " + DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss") + " - " + AjusteChave.Usuario);
                    for (int i = 0; i < sel.Count; i++)
                    {
                        int n = i;
                        W.Dispatcher.BeginInvoke(new Action(() => Status("Voltando " + (n + 1) + " de " + sel.Count + "...")));
                        try { AjusteChave.VoltarParaContingencia(c, sel[i].C, backups, log); res.Add(null); }
                        catch (Exception ex) { res.Add(ex.Message); }
                    }
                    log.Info(res.Count(r => r == null) + " de " + sel.Count + " voltaram para a contingência");
                }
                return res;
            }).ContinueWith(t =>
            {
                Ocupado(false);
                if (t.IsFaulted) { Status("ERRO: " + t.Exception.InnerException.Message); return; }
                var falhas = new List<string>();
                for (int i = 0; i < sel.Count; i++)
                {
                    string erro = t.Result[i];
                    sel[i].Atualizar(erro == null ? "→ " + AjusteChave.SituacaoMetre : "ERRO: " + erro);
                    if (erro != null) falhas.Add(sel[i].Nota + ": " + erro);
                }
                AtualizarBotao();
                string resumo = (sel.Count - falhas.Count) + " de " + sel.Count + " nota(s) voltaram para a contingência." +
                    (falhas.Count > 0 ? "\n\nNão alteradas:\n" + string.Join("\n", falhas) : "");
                Status(resumo.Replace("\n", " ") + " Log: " + arqLog);
                MessageBox.Show(W, resumo + "\n\nFeche e abra o Metre para atualizar a aba CONTIGÊNCIA.\n\nLog: " + arqLog,
                    Program.NomeSistema, MessageBoxButton.OK, falhas.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void Ocupado(bool sim)
        {
            ocupado = sim;
            G<Button>("BtnListarCont2").IsEnabled = !sim;
            W.Cursor = sim ? System.Windows.Input.Cursors.Wait : null;
            AtualizarBotao();
        }

        void AtualizarBotao()
        {
            int n = Marcadas().Count;
            var b = G<Button>("BtnVoltarCont2");
            b.Content = n > 0 ? "Voltar para Contingência (" + n + ")" : "Voltar para Contingência";
            b.IsEnabled = n > 0 && !ocupado;
        }

        void Refiltrar()
        {
            var e = EntradaNumeros.Ler(G<TextBox>("Cont2Numeros").Text, 10000);
            G<TextBlock>("Cont2Aviso").Text = e.Invalidos.Count > 0 ? e.Avisos() : "";
            visao.Refresh();
            AtualizarBotao();
        }

        void Status(string msg) { G<TextBlock>("TxtCont2Status").Text = msg; }

        bool Filtrar(object o)
        {
            var l = (LinhaCont2Grid)o;
            var de = G<DatePicker>("Cont2De").SelectedDate;
            var ate = G<DatePicker>("Cont2Ate").SelectedDate;
            if ((de.HasValue || ate.HasValue) && !l.C.DtEmissao.HasValue) return false;
            if (de.HasValue && l.C.DtEmissao.Value.Date < de.Value.Date) return false;
            if (ate.HasValue && l.C.DtEmissao.Value.Date > ate.Value.Date) return false;
            var nums = EntradaNumeros.Ler(G<TextBox>("Cont2Numeros").Text, 10000).Numeros;
            long numero;
            if (nums.Count > 0 && !(long.TryParse(l.C.Numero, out numero) && nums.Contains(numero))) return false;
            string q = G<TextBox>("CaixaPesquisa").Text.Trim();
            if (q == "") return true;
            return new[] { l.Nota, l.C.Numero, l.C.Pedido, l.C.Chave, l.Motivo }
                .Any(x => x != null && x.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }
}
