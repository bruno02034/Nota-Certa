# Janela que imita a "Rejeição de Nota" do Metre (vista em vídeo em 22/10): código, texto, "X" e "Entendi !".
Add-Type -AssemblyName PresentationFramework
$w = New-Object System.Windows.Window
$w.Title = 'Rejeicao de teste'; $w.Width = 500; $w.Height = 300; $w.Topmost = $true; $w.WindowStartupLocation = 'CenterScreen'
$sp = New-Object System.Windows.Controls.StackPanel
foreach ($t in 'Rejeição de Nota', 'código', '539', 'Rejeição: Duplicidade de NF-e com diferença na Chave de Acesso [chNFe:31261011222333000181650060000888709880705883][nRec:310020000000001]') {
    $tb = New-Object System.Windows.Controls.TextBlock; $tb.Text = $t; [void]$sp.Children.Add($tb)
}
foreach ($b in 'X', 'Entendi !') {
    $bt = New-Object System.Windows.Controls.Button; $bt.Content = $b
    if ($b -ne 'X') { $bt.Add_Click({ $w.Close() }) }
    [void]$sp.Children.Add($bt)
}
$w.Content = $sp
[void]$w.ShowDialog()
