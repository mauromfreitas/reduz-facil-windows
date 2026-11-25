# Teste automático do .exe num Windows limpo (roda no GitHub Actions).
# 1. Abre o .exe portátil numa pasta vazia: extrai os motores, reduz as amostras (fotos, HEIC, vídeos, HDR,
#    áudio, PDFs, arquivo quebrado, PDF com senha), passa por todas as telas tirando capturas,
#    testa meta + corte, menu do Explorer, pasta vigiada e pausa.
# 2. Segunda abertura: com o app aberto, abrir o .exe de novo com um arquivo (como o botão direito do
#    Explorer faz) manda o arquivo para a instância aberta, que reduz com o atalho pedido.
# 3. Atualização: troca o .exe "aberto" por outro, reabre e apaga o antigo.
param(
    [Parameter(Mandatory)] [string] $Exe,
    [Parameter(Mandatory)] [string] $Amostras
)

$ErrorActionPreference = 'Stop'
$dir = Join-Path $env:RUNNER_TEMP 'teste-portatil'
Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $dir | Out-Null
Copy-Item $Exe (Join-Path $dir 'ReduzFacil.exe')
$app = Join-Path $dir 'ReduzFacil.exe'
$data = Join-Path $dir 'ReduzFacil-dados'
$log = Join-Path $data 'logs/app.log'
$shots = Join-Path $env:RUNNER_TEMP 'capturas'
New-Item -ItemType Directory -Force $shots | Out-Null
$failures = @()

# As amostras vão para uma pasta com acentos e espaços (caminhos de verdade do Windows)
$samples = Join-Path $env:RUNNER_TEMP 'Fotos e vídeos da família'
Remove-Item $samples -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $samples | Out-Null
Copy-Item (Join-Path $Amostras '*') $samples

function Fail($text) { Write-Host "::error title=teste::$text"; $script:failures += $text }
function LogLines { if (Test-Path $log) { @(Get-Content $log -Encoding UTF8) } else { @() } }

# ---------- 1. teste completo ----------
$sw = [Diagnostics.Stopwatch]::StartNew()
$p = Start-Process $app -ArgumentList @('--teste-auto', "`"$samples`"", '--capturas', "`"$shots`"") -PassThru
if (-not $p.WaitForExit(30 * 60 * 1000)) { $p.Kill(); Fail "o teste completo não terminou em 30 minutos" }
$lines = LogLines
$lines | Where-Object { $_ -match 'TESTE' } | ForEach-Object { Write-Host $_ }
if ($p.ExitCode -ne 0) { Fail "teste completo: código de saída $($p.ExitCode)" }
if (-not ($lines -match 'TESTE OK')) { Fail "teste completo não chegou ao TESTE OK" }
$lines -match 'TESTE ERRO' | ForEach-Object { Fail $_ }
Write-Host ("::notice title=teste completo::{0:N0} s · {1} verificações ok" -f $sw.Elapsed.TotalSeconds, @($lines -match 'TESTE ok:').Count)
($lines -match 'TESTE placa') | Select-Object -First 1 | ForEach-Object { Write-Host "::notice title=placa::$_" }
($lines -match 'Motor extraído') | Select-Object -First 1 | ForEach-Object { Write-Host "::notice title=motor::$_" }

# ---------- 2. segunda abertura manda arquivos para a primeira ----------
$before = (LogLines).Count
$first = Start-Process $app -ArgumentList @('--teste-espera', '15') -PassThru
$until = (Get-Date).AddSeconds(60)
while (-not ((LogLines | Select-Object -Skip $before) -match 'TESTE espera: pronto') -and (Get-Date) -lt $until) { Start-Sleep -Milliseconds 300 }
$photo = Get-ChildItem $samples -Filter '*.jpg' | Sort-Object Length -Descending | Select-Object -First 1
$second = Start-Process $app -ArgumentList @('--perfil', 'whatsapp', "`"$($photo.FullName)`"") -PassThru
if (-not $second.WaitForExit(30000)) { $second.Kill(); Fail "segunda abertura não fechou" }
if ($second.ExitCode -ne 0) { Fail "segunda abertura saiu com $($second.ExitCode)" }
if (-not $first.WaitForExit(5 * 60 * 1000)) { $first.Kill(); Fail "primeira instância não fechou" }
$tail = LogLines | Select-Object -Skip $before
$tail -match 'TESTE recebido' | ForEach-Object { Write-Host $_ }
if (-not ($tail -match "TESTE recebido: $([regex]::Escape($photo.Name)) Done origem=ShellMenu")) { Fail "a foto mandada pela segunda abertura não foi reduzida" }

# ---------- 3. atualização com um clique (troca do .exe e reabertura) ----------
$before = (LogLines).Count
$newExe = (Resolve-Path $Exe).Path
$up = Start-Process $app -ArgumentList @('--teste-atualizar', "`"$newExe`"") -PassThru
if (-not $up.WaitForExit(60000)) { $up.Kill(); Fail "atualização: a versão antiga não fechou" }
$until = (Get-Date).AddSeconds(120)
do {
    Start-Sleep -Seconds 1
    $tail = LogLines | Select-Object -Skip $before
    $done = ($tail -match 'Atualizado: de') -and (@($tail -match 'TESTE OK').Count -ge 2)
} while (-not $done -and (Get-Date) -lt $until)
$tail -match 'Atualiza|TESTE atualizar' | ForEach-Object { Write-Host $_ }
if (-not $done) { Fail "atualização: a versão nova não abriu" }
Start-Sleep -Seconds 3
if (Test-Path "$app.antigo") { Fail "atualização: sobrou o ReduzFacil.exe.antigo" }
if (-not (Test-Path $app)) { Fail "atualização: o ReduzFacil.exe sumiu" }

# ---------- 4. conferências gerais ----------
$engine = Get-ChildItem (Join-Path $data 'engine') -Recurse -File | Measure-Object Length -Sum
Write-Host ("::notice title=arquivos::motores: {0} arquivos, {1:N0} MB" -f $engine.Count, ($engine.Sum / 1MB))
if (Get-ChildItem $env:TEMP -Directory -Filter '.net' -ErrorAction SilentlyContinue) { Fail 'Existe %TEMP%\.net (algo extraiu na pasta temporária)' }
$leftovers = Get-ChildItem (Join-Path $data 'temp') -Directory -Filter 'job-*' -ErrorAction SilentlyContinue
if ($leftovers) { Fail "sobrou pasta de trabalho: $($leftovers.Name -join ', ')" }
if (Test-Path $log) { Copy-Item $log (Join-Path $shots 'app.log') }

if ($failures.Count -gt 0) { throw "$($failures.Count) verificação(ões) falharam" }
Write-Host "::notice title=resultado::Todos os cenários passaram"
