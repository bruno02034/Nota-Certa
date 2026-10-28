using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace DuplicidadeNfce
{
    // Tela para digitar a senha do banco do Metre (mascarada). A senha é testada no banco e gravada no
    // duplicidade.ini criptografada pelo Windows (SenhaBanco); nunca aparece na tela, no ini nem no log.
    static class TelaSenha
    {
        // Devolve a senha salva, ou null se o usuário cancelou.
        public static string Pedir(Window dono, Func<Config> novaConfig, string arqIni, string motivo)
        {
            var cfg = novaConfig();
            try { cfg.ResolverConexaoBanco(); } catch { }
            // Só dá para ser "dona" a janela que já tem handle (já foi exibida).
            bool comDono = dono != null && new WindowInteropHelper(dono).Handle != IntPtr.Zero;
            var d = new Window
            {
                Title = Program.NomeSistema + " – senha do banco", Width = 460, SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize, WindowStartupLocation = comDono ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                ShowInTaskbar = !comDono, Owner = comDono ? dono : null, Background = dono != null ? dono.Background : Brushes.WhiteSmoke,
                FontFamily = new FontFamily("Segoe UI"), FontSize = 13, Foreground = new SolidColorBrush(Color.FromRgb(0x2B, 0x2F, 0x36)),
                UseLayoutRounding = true
            };
            Func<string, object> rec = k => dono != null ? dono.TryFindResource(k) : null;
            Func<string, Style> estilo = k => rec(k) as Style;
            var cinza = (rec("TextoSec") as Brush) ?? Brushes.Gray;

            var pilha = new StackPanel { Margin = new Thickness(22, 20, 22, 18) };
            pilha.Children.Add(new TextBlock { Text = "SENHA DO BANCO DO METRE", Style = estilo("TituloCard") });
            pilha.Children.Add(new TextBlock { Text = motivo, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
            pilha.Children.Add(new TextBlock
            {
                Text = "Usuário " + cfg.DbUsuario + " em " + cfg.DbHost + ":" + cfg.DbPorta, Foreground = cinza, FontSize = 11.5, Margin = new Thickness(0, 4, 0, 0)
            });
            pilha.Children.Add(new TextBlock { Text = "Senha", Style = estilo("Rotulo"), Margin = new Thickness(0, 14, 0, 4) });
            var caixa = new PasswordBox { Height = 32, Padding = new Thickness(6, 0, 6, 0), VerticalContentAlignment = VerticalAlignment.Center };
            pilha.Children.Add(caixa);
            pilha.Children.Add(new TextBlock
            {
                Text = "Fica guardada no duplicidade.ini criptografada pelo Windows: só abre neste computador e nunca é exibida.",
                TextWrapping = TextWrapping.Wrap, Foreground = cinza, FontSize = 11.5, Margin = new Thickness(0, 8, 0, 0)
            });
            var situacao = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
            pilha.Children.Add(situacao);
            var botoes = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            var cancelar = new Button { Content = "Cancelar", Style = estilo("BtnSecundario"), IsCancel = true, MinWidth = 90 };
            var salvar = new Button { Content = "Testar e salvar", Style = estilo("BtnPrimario"), IsDefault = true, Margin = new Thickness(8, 0, 0, 0), MinWidth = 120 };
            botoes.Children.Add(cancelar);
            botoes.Children.Add(salvar);
            pilha.Children.Add(botoes);

            var cartao = new Grid { Margin = new Thickness(12) };
            cartao.Children.Add(new Border { Style = estilo("CardFundo"), Background = Brushes.White, CornerRadius = new CornerRadius(12) });
            if (estilo("CardLinha") != null) cartao.Children.Add(new Border { Style = estilo("CardLinha") });
            cartao.Children.Add(pilha);
            d.Content = cartao;

            string resultado = null;
            Action<string, bool> mostrar = (texto, erro) =>
            {
                situacao.Text = texto;
                situacao.Foreground = erro ? ((rec("Vermelho") as Brush) ?? Brushes.Firebrick) : cinza;
                situacao.Visibility = Visibility.Visible;
            };
            Action<bool> ocupado = o => { caixa.IsEnabled = salvar.IsEnabled = cancelar.IsEnabled = !o; };
            Action<string> gravar = senha =>
            {
                try
                {
                    SenhaBanco.GravarNoIni(arqIni, senha);
                    resultado = senha;
                    d.DialogResult = true;
                }
                catch (Exception ex) { ocupado(false); mostrar("Não foi possível gravar no " + arqIni + ": " + ex.Message, true); }
            };
            salvar.Click += (s, e) =>
            {
                string senha = caixa.Password;
                if (senha == "") { mostrar("Digite a senha.", true); caixa.Focus(); return; }
                ocupado(true);
                mostrar("Testando a conexão com o banco…", false);
                var teste = novaConfig();
                teste.DbSenha = senha;
                Task.Factory.StartNew(() =>
                {
                    using (Banco.Sessao.Abrir(teste, null, true)) { }
                }).ContinueWith(t =>
                {
                    if (t.Exception == null) { gravar(senha); return; }
                    string erro = t.Exception.GetBaseException().Message;
                    if (erro.Contains("Access denied") || erro.Contains("1045"))
                    {
                        ocupado(false);
                        mostrar("O banco recusou a senha do usuário " + teste.DbUsuario + ". Confira e digite de novo.", true);
                        caixa.SelectAll();
                        caixa.Focus();
                        return;
                    }
                    // Banco fora do ar ou DLL ausente: a senha não pôde ser conferida, mas pode ser guardada.
                    if (MessageBox.Show(d, "Não foi possível conectar para conferir a senha:\n\n" + erro + "\n\nSalvar a senha mesmo assim?",
                            Program.NomeSistema, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                        gravar(senha);
                    else { ocupado(false); mostrar("Senha não salva.", true); }
                }, TaskScheduler.FromCurrentSynchronizationContext());
            };
            d.Loaded += (s, e) => caixa.Focus();
            d.ShowDialog();
            return resultado;
        }
    }
}
