# Janela que imita a "Confirmação!" do Metre para pedido cancelado (vista em 04/11): texto, "Sim" e "Não".
# Sai com código 2 se clicarem "Sim" (o teste falha) e 0 com "Não". -Texto troca a pergunta (para ver que outra janela com "Não" fica de fora).
param([string]$Texto = 'Este pedido foi cancelado, deseja excluir esta nota e inutilizar a numeração?')
Add-Type -AssemblyName PresentationFramework
$script:codigo = 1
$w = New-Object System.Windows.Window
$w.Title = 'Confirmacao de teste'; $w.Width = 450; $w.Height = 220; $w.Topmost = $true; $w.WindowStartupLocation = 'CenterScreen'
$sp = New-Object System.Windows.Controls.StackPanel
foreach ($t in 'Confirmação!', $Texto) {
    $tb = New-Object System.Windows.Controls.TextBlock; $tb.Text = $t; [void]$sp.Children.Add($tb)
}
$sim = New-Object System.Windows.Controls.Button; $sim.Content = 'Sim'; $sim.Add_Click({ $script:codigo = 2; $w.Close() }); [void]$sp.Children.Add($sim)
$nao = New-Object System.Windows.Controls.Button; $nao.Content = 'Não'; $nao.Add_Click({ $script:codigo = 0; $w.Close() }); [void]$sp.Children.Add($nao)
$w.Content = $sp
[void]$w.ShowDialog()
exit $script:codigo
