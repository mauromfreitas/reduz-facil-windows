# Baixa os motores do Reduz Fácil para a pasta engine-src (usada pelo build do .exe):
#   ffmpeg\  FFmpeg "gpl-shared" do BtbN (ffmpeg.exe, ffprobe.exe e DLLs) — vídeo e áudio, com NVIDIA/Intel/AMD
#   magick\  ImageMagick portátil Q16 x64 — fotos (inclui HEIC do iPhone)
#   gs\      Ghostscript 64 bits — PDF
# Roda no GitHub Actions (Windows). Também funciona num PC com Windows e 7-Zip instalado.
param(
    [string] $Dest = "engine-src"
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root = if ([IO.Path]::IsPathRooted($Dest)) { $Dest } else { Join-Path (Get-Location) $Dest }
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("motores-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $root, $tmp | Out-Null
$headers = @{ 'User-Agent' = 'reduz-facil-build' }
if ($env:GITHUB_TOKEN) { $headers['Authorization'] = "Bearer $($env:GITHUB_TOKEN)" }
$sevenZip = @("$env:ProgramFiles\7-Zip\7z.exe", "${env:ProgramFiles(x86)}\7-Zip\7z.exe", (Get-Command 7z -ErrorAction SilentlyContinue).Source) |
    Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $sevenZip) { throw "7-Zip não encontrado (precisa para o ImageMagick e o Ghostscript)." }
$versions = @()

function Note($text) { Write-Host "::notice title=motores::$text"; Write-Host $text }

function Get-Release([string] $repo, [string] $tag = $null) {
    $url = if ($tag) { "https://api.github.com/repos/$repo/releases/tags/$tag" } else { "https://api.github.com/repos/$repo/releases/latest" }
    return Invoke-RestMethod $url -Headers $headers
}

function Download([string] $url, [string] $file) {
    for ($i = 1; $i -le 3; $i++) {
        try {
            Invoke-WebRequest $url -OutFile $file -Headers @{ 'User-Agent' = 'reduz-facil-build' } -UseBasicParsing
            if ((Get-Item $file).Length -gt 0) { return }
        } catch {
            Write-Host "Tentativa $i falhou: $($_.Exception.Message)"
            Start-Sleep -Seconds (5 * $i)
        }
    }
    throw "Não consegui baixar $url"
}

function Extract([string] $archive, [string] $to) {
    New-Item -ItemType Directory -Force $to | Out-Null
    & $sevenZip x $archive "-o$to" -y | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "7-Zip falhou ao extrair $archive" }
}

# ---------------- FFmpeg ----------------
$rel = Get-Release 'BtbN/FFmpeg-Builds' 'latest'
# Prefere a versão estável mais nova (ex.: ffmpeg-n7.1-latest-win64-gpl-shared-7.1.zip); senão a "master".
$ff = $rel.assets | Where-Object { $_.name -match '^ffmpeg-n(\d+)\.(\d+)-latest-win64-gpl-shared-[\d.]+\.zip$' } |
    Sort-Object { [int]($_.name -replace '^ffmpeg-n(\d+)\.(\d+).*', '$1') * 100 + [int]($_.name -replace '^ffmpeg-n(\d+)\.(\d+).*', '$2') } -Descending |
    Select-Object -First 1
if (-not $ff) { $ff = $rel.assets | Where-Object { $_.name -eq 'ffmpeg-master-latest-win64-gpl-shared.zip' } | Select-Object -First 1 }
if (-not $ff) { throw "FFmpeg win64 gpl-shared não encontrado nas releases do BtbN" }
$zip = Join-Path $tmp $ff.name
Download $ff.browser_download_url $zip
Extract $zip (Join-Path $tmp 'ffmpeg')
$bin = Get-ChildItem (Join-Path $tmp 'ffmpeg') -Recurse -Filter ffmpeg.exe | Select-Object -First 1
$ffDir = Join-Path $root 'ffmpeg'
New-Item -ItemType Directory -Force $ffDir | Out-Null
Get-ChildItem $bin.DirectoryName -File | Where-Object { $_.Name -ne 'ffplay.exe' } | Copy-Item -Destination $ffDir
$lic = Get-ChildItem (Join-Path $tmp 'ffmpeg') -Recurse -Filter 'LICENSE*' | Select-Object -First 1
if ($lic) { Copy-Item $lic.FullName (Join-Path $ffDir 'LICENSE-FFmpeg.txt') }
$versions += "FFmpeg: $($ff.name)"
Note "FFmpeg: $($ff.name)"

# ---------------- ImageMagick ----------------
$im = $null
try {
    $rel = Get-Release 'ImageMagick/ImageMagick'
    $im = $rel.assets | Where-Object { $_.name -match '^ImageMagick-[\d.\-]+-portable-Q16-x64\.(zip|7z)$' } | Select-Object -First 1
    if (-not $im) { $im = $rel.assets | Where-Object { $_.name -match '^ImageMagick-[\d.\-]+-portable-Q16-HDRI-x64\.(zip|7z)$' } | Select-Object -First 1 }
    if ($im) { $imUrl = $im.browser_download_url; $imName = $im.name }
} catch { Write-Host "Releases do ImageMagick no GitHub indisponíveis: $($_.Exception.Message)" }
if (-not $im) {
    # Plano B: lista oficial de binários
    $page = (Invoke-WebRequest 'https://imagemagick.org/archive/binaries/' -UseBasicParsing).Content
    $names = [regex]::Matches($page, 'ImageMagick-[\d.\-]+-portable-Q16-x64\.(zip|7z)') | ForEach-Object { $_.Value } | Sort-Object -Unique
    $imName = $names | Sort-Object { [version](($_ -replace '^ImageMagick-([\d.]+)-(\d+).*', '$1.$2')) } -Descending | Select-Object -First 1
    if (-not $imName) { throw "ImageMagick portátil não encontrado" }
    $imUrl = "https://imagemagick.org/archive/binaries/$imName"
}
$arc = Join-Path $tmp $imName
Download $imUrl $arc
Extract $arc (Join-Path $tmp 'magick')
$mexe = Get-ChildItem (Join-Path $tmp 'magick') -Recurse -Filter magick.exe | Select-Object -First 1
if (-not $mexe) { throw "magick.exe não está no pacote do ImageMagick" }
$mgDir = Join-Path $root 'magick'
New-Item -ItemType Directory -Force $mgDir | Out-Null
Copy-Item (Join-Path $mexe.DirectoryName '*') $mgDir -Recurse -Force
# Só o necessário: tira documentação, exemplos e os programas de apoio que não usamos
foreach ($junk in @('www', 'images', 'Magick++_demo', 'demo', 'uninstall', 'ImageMagick.chm')) {
    Remove-Item (Join-Path $mgDir $junk) -Recurse -Force -ErrorAction SilentlyContinue
}
Get-ChildItem $mgDir -Filter '*.exe' | Where-Object { $_.Name -notin @('magick.exe') } | Remove-Item -Force
$versions += "ImageMagick: $imName"
Note "ImageMagick: $imName"

# ---------------- Ghostscript ----------------
$rel = Get-Release 'ArtifexSoftware/ghostpdl-downloads'
$gsAsset = $rel.assets | Where-Object { $_.name -match '^gs\d+w64\.exe$' } | Select-Object -First 1
if (-not $gsAsset) { throw "Instalador do Ghostscript 64 bits não encontrado em $($rel.tag_name)" }
$gsExe = Join-Path $tmp $gsAsset.name
Download $gsAsset.browser_download_url $gsExe
Extract $gsExe (Join-Path $tmp 'gs')
$gsc = Get-ChildItem (Join-Path $tmp 'gs') -Recurse -Filter gswin64c.exe | Select-Object -First 1
if (-not $gsc) { throw "gswin64c.exe não está no instalador do Ghostscript" }
$gsDir = Join-Path $root 'gs'
New-Item -ItemType Directory -Force $gsDir | Out-Null
Copy-Item $gsc.FullName $gsDir
Copy-Item (Join-Path $gsc.DirectoryName 'gsdll64.dll') $gsDir
$gsLic = Get-ChildItem (Join-Path $tmp 'gs') -Recurse -Include 'LICENSE', 'COPYING' | Select-Object -First 1
if ($gsLic) { Copy-Item $gsLic.FullName (Join-Path $gsDir 'LICENSE-Ghostscript.txt') }
$versions += "Ghostscript: $($gsAsset.name) ($($rel.tag_name))"
Note "Ghostscript: $($gsAsset.name)"

# ---------------- Runtime do Visual C++ ----------------
# ImageMagick e Ghostscript usam o runtime do Visual C++. A maioria dos PCs já tem; levamos junto
# para funcionar também num Windows recém-instalado (arquivos redistribuíveis da Microsoft).
foreach ($dll in @('vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll', 'vcomp140.dll')) {
    $src = Join-Path $env:WINDIR "System32\$dll"
    if (Test-Path $src) {
        foreach ($d in @($mgDir, $gsDir)) { if (-not (Test-Path (Join-Path $d $dll))) { Copy-Item $src $d } }
    }
}

# ---------------- Conferência ----------------
$ver = & (Join-Path $ffDir 'ffmpeg.exe') -hide_banner -version | Select-Object -First 1
Note "ffmpeg: $ver"
$enc = (& (Join-Path $ffDir 'ffmpeg.exe') -hide_banner -encoders) -join "`n"
foreach ($e in @('libx264', 'libx265', 'h264_nvenc', 'h264_qsv', 'h264_amf', 'libmp3lame')) {
    if ($enc -notmatch $e) { throw "O FFmpeg baixado não tem o codificador $e" }
}
if (((& (Join-Path $ffDir 'ffmpeg.exe') -hide_banner -filters) -join "`n") -notmatch 'zscale') { throw "O FFmpeg baixado não tem o filtro zscale (HDR)" }
$env:MAGICK_HOME = $mgDir
$mv = & (Join-Path $mgDir 'magick.exe') -version | Select-Object -First 1
Note "magick: $mv"
$fmts = (& (Join-Path $mgDir 'magick.exe') -list format) -join "`n"
foreach ($f in @('HEIC', 'WEBP', 'JPEG', 'PNG')) {
    if ($fmts -notmatch "(?m)^\s*$f\*?\s") { throw "O ImageMagick baixado não lê $f" }
}
$gv = & (Join-Path $gsDir 'gswin64c.exe') -v | Select-Object -First 1
Note "ghostscript: $gv"

$versions | Set-Content (Join-Path $root 'versoes.txt') -Encoding UTF8
$size = (Get-ChildItem $root -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Note ("Motores prontos em {0}: {1:N0} MB" -f $root, $size)
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
