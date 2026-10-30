using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace DuplicidadeNfce
{
    // Linha da grade de Contingência 2.
    class LinhaCont2Grid
    {
        public LinhaContingencia C { get; private set; }
        public LinhaCont2Grid(LinhaContingencia c) { C = c; }

        public string Nota { get { return C.Serie + "/" + C.Numero; } }
        public string Pedido { get { return C.Pedido ?? "-"; } }
        public string Emissao { get { return Data(C.DtEmissao); } }
        public string Contingencia { get { return Data(C.DtContingencia); } }
        public string Valor { get { return C.Valor.HasValue ? "R$ " + Analisador.Moeda(C.Valor) : "-"; } }
        public string Situacao { get { return C.Situacao; } }
        public string Chave { get { return C.Chave ?? "-"; } }
        public string Motivo { get { return C.Motivo; } }

        static string Data(DateTime? d) { return d.HasValue ? d.Value.ToString("dd/MM/yy HH:mm") : "-"; }
    }

    // Página "Contingência 2": lista, só para consulta, as notas que o cliente escondeu da aba Contingência do Metre.
    // Busca no banco só quando o usuário clica em Listar; nada aqui é processado, copiado ou gravado.
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
            G<TextBox>("CaixaPesquisa").TextChanged += (s, e) => visao.Refresh();
        }

        T G<T>(string nome) where T : class
        {
            var o = W.FindName(nome) as T;
            if (o == null) throw new Exception("elemento da tela não encontrado: " + nome);
            return o;
        }

        void Listar()
        {
            if (ocupado) return;
            var c = cfg();
            if (string.IsNullOrEmpty(c.DbSenha))
            {
                Status(c.ErroSenhaProtegida ?? "Senha do banco não informada: use o botão \"Senha do banco\" em Configurações.");
                return;
            }
            ocupado = true;
            G<Button>("BtnListarCont2").IsEnabled = false;
            W.Cursor = System.Windows.Input.Cursors.Wait;
            Status("Consultando o banco " + c.DbBanco + "...");
            Task.Factory.StartNew(() => Banco.LerContingencia2(c)).ContinueWith(t =>
            {
                ocupado = false;
                G<Button>("BtnListarCont2").IsEnabled = true;
                W.Cursor = null;
                if (t.IsFaulted) { Status("ERRO: " + t.Exception.InnerException.Message); return; }
                linhas.Clear();
                foreach (var l in t.Result) linhas.Add(new LinhaCont2Grid(l));
                G<TextBlock>("GradeCont2Vazia").Visibility = linhas.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                Status(linhas.Count == 0 ? "Nenhuma nota em Contingência 2." :
                    linhas.Count + " nota(s): " + string.Join(", ", t.Result.GroupBy(x => x.Situacao).Select(g => g.Count() + " em \"" + g.Key + "\"")) +
                    " · listado em " + DateTime.Now.ToString("dd/MM/yyyy HH:mm"));
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void Status(string msg) { G<TextBlock>("TxtCont2Status").Text = msg; }

        bool Filtrar(object o)
        {
            string q = G<TextBox>("CaixaPesquisa").Text.Trim();
            if (q == "") return true;
            var l = (LinhaCont2Grid)o;
            return new[] { l.Nota, l.C.Numero, l.C.Pedido, l.C.Chave, l.Motivo }
                .Any(x => x != null && x.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }
}
