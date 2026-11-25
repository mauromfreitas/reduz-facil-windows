# Reduz Fácil para Windows

Versão para Windows do Reduz Fácil: reduz **fotos, vídeos, áudios e PDFs** direto no PC, sem internet e sem limites.
É **portátil**: um único `ReduzFacil.exe`, sem instalação. Na primeira abertura ele cria a pasta
`ReduzFacil-dados` ao lado do .exe (motores, configurações, histórico e registro).

## Como usar

1. Baixe o `ReduzFacil-X.Y.Z.exe` em **Releases** e coloque numa pasta sua (ex.: `Documentos\Reduz Fácil`).
2. Abra com dois cliques. Na primeira vez, o Windows pode mostrar "O Windows protegeu o computador":
   clique em **Mais informações → Executar assim mesmo**.
3. Escolha **como reduzir** (Por qualidade, WhatsApp, E-mail, gov.br, Enem, Currículo, Meta em MB ou Converter)
   e arraste os arquivos ou pastas para a janela.

O arquivo reduzido é salvo ao lado do original com `-reduzido` no nome (ou numa pasta escolhida).
**O original nunca é apagado nem substituído.** Se não houver ganho, nada é salvo.

## O que ele faz

| | |
|---|---|
| **Fotos** | JPG, PNG, WebP, HEIC (iPhone), AVIF, BMP. Remove GPS e dados da câmera (opcional), mantém o perfil de cor. PNG com transparência vira WebP. Meta em MB reduz as dimensões em vez de destruir a qualidade. |
| **Vídeos** | MP4, MOV, MKV, AVI, WebM, WMV, 3GP, MTS… H.264 (abre em tudo) ou H.265 (menor). Usa a **placa de vídeo** (NVIDIA, Intel Quick Sync, AMD) automaticamente; sem placa, o processador. Meta exata em MB (2 passadas). HDR do iPhone vira cores normais. Vídeo em pé continua em pé. |
| **Ajustes de vídeo** | Cortar um trecho, girar, limitar a resolução, tirar o som ou extrair só o áudio (MP3). |
| **Áudio** | WAV, FLAC, M4A, MP3, OGG, WMA… → MP3 ou M4A. Mantém a capa do álbum. |
| **PDF** | Recomprime as imagens (ótimo para escaneados), mantém o texto como texto. Opção de tons de cinza. PDF com senha dá aviso claro. |
| **Antes e depois** | Compara lado a lado (com zoom 100%) a foto, um quadro do vídeo ou a 1ª página do PDF antes de salvar. |
| **Onde está o peso** | Varre uma pasta ou disco, lista os arquivos que mais economizariam, reduz os selecionados e, se você quiser, manda os originais para a **Lixeira** (dá para restaurar). |
| **Pasta vigiada** | Tudo o que chegar numa pasta (ex.: Downloads, fotos do WhatsApp) é reduzido sozinho, só depois de terminar de copiar. |
| **Botão direito do Explorer** | "Reduzir com Reduz Fácil" com os atalhos (WhatsApp, e-mail…), para arquivos e pastas. No Windows 11 fica em "Mostrar mais opções". |
| **Perfis** | Salve combinações de ajustes (ex.: "Fotos da família") e use também na pasta vigiada. |
| **Fila** | Vídeos e demais arquivos em pistas separadas, vários ao mesmo tempo, pausar/continuar, cancelar, tentar de novo. Motores em prioridade baixa (o PC continua respondendo). |
| **Histórico** | Últimos 1.000 arquivos e o total economizado desde sempre. |

## Pasta de dados

```
ReduzFacil.exe
ReduzFacil-dados\
  engine\        motores (FFmpeg, ImageMagick, Ghostscript) — recriado se apagar
  temp\          trabalhos em andamento (limpo sozinho)
  logs\app.log   registro
  configuracoes.json, historico.json, totais.json
```

Mover o .exe para outra pasta? Leve a `ReduzFacil-dados` junto para manter as configurações. O menu do
botão direito e o "Iniciar com o Windows" se corrigem sozinhos na próxima abertura.

## Desenvolvimento

- `src/ReduzFacil.Core` — toda a lógica (compressão, fila, pasta vigiada, "Onde está o peso"), sem pacotes externos;
  roda em Windows e Linux.
- `src/ReduzFacil` — app WPF (.NET 10), .exe único self-contained.
- `tests/ReduzFacil.Tests` — testes com arquivos reais gerados na hora (`dotnet run --project tests/ReduzFacil.Tests`).
  No Linux usa `ffmpeg`, `gs` e ImageMagick do sistema; no Windows, `-- --engine engine-src`.
- `tests/smoke.ps1` — teste do .exe pronto num Windows limpo (GitHub Actions), com capturas no ramo `capturas`.
- `tools/baixar-motores.ps1` — baixa os motores para `engine-src` (necessário para compilar o .exe).

Build e publicação só pelo GitHub Actions: mudar `VersionPrefix` em `src/ReduzFacil/ReduzFacil.csproj`
e enviar para a `main` publica o .exe em Releases.

Veja também `docs/decisoes.md`, `docs/arquitetura.md` e `docs/roteiro-de-teste.md`.

## Licenças dos motores

FFmpeg (GPL, build do BtbN), ImageMagick (licença ImageMagick) e Ghostscript (AGPL). Uso pessoal.
