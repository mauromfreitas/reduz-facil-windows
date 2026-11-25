# Arquitetura

```
ReduzFacil.exe (WPF)                         ReduzFacil.Core (sem dependências)
├─ Program.Main                              ├─ Engines/   EngineTools (onde estão os motores), ProcessRunner (rodar, cancelar, pausar)
│   instância única → pasta de dados →       ├─ Media/     MediaProbe (ffprobe → duração, rotação, HDR…)
│   extrai motores → DLLs do WPF → App       ├─ Compress/  Compressor (regras gerais) → Image/Video/Audio/PdfCompressor
├─ App: AppServices, bandeja, argumentos     │             VideoPlanner (comandos do FFmpeg, puro), Hardware (placas de vídeo)
├─ MainWindow: menu lateral                  │             PreviewMaker (antes e depois)
│   ├─ ReduzirView (atalhos, ajustes, fila)  ├─ Output/    OutputNamer (onde salvar, nunca sobrescreve)
│   ├─ PesoView (Onde está o peso)           ├─ Queue/     JobQueue (duas pistas, pausa, cancelar), FileCollector
│   ├─ HistoricoView                         ├─ Watch/     FolderWatcher (pasta vigiada)
│   └─ ConfigView                            ├─ Scan/      WeightScanner + HeaderReader (estimativas rápidas)
├─ Dialogs: perfil, ajustes do vídeo,        ├─ Settings/  AppSettings, perfis, SettingsStore (gravação segura)
│   antes e depois                           └─ Stats/     HistoryStore (histórico e totais)
├─ Infra: Boot (pasta, motores), SingleInstance (cano nomeado), WindowsIntegration (menu do Explorer, iniciar com o Windows),
│   TrayIcon, RecycleBin, NativeLoader
└─ SmokeRunner (teste automático do .exe)
```

## Fluxo de um arquivo

1. Entra na fila (arrastar, botões, botão direito, pasta vigiada, "Onde está o peso") com uma **cópia** das opções do momento.
2. A fila escolhe a pista (vídeo ou demais) e chama `Compressor.RunAsync`.
3. O compressor do tipo trabalha numa pasta própria em `ReduzFacil-dados\temp\job-xxxx` (nomes simples, sem acentos).
4. Regras gerais: sem ganho → nada é salvo; com ganho → `OutputNamer` decide o nome (nunca sobrescreve), o arquivo é **movido**
   para o destino já completo e recebe a data do original.
5. Resultado vai para o histórico; a pasta de trabalho é apagada (também em erro ou cancelamento).

## Motores no .exe

O workflow roda `tools/baixar-motores.ps1` (FFmpeg BtbN gpl-shared, ImageMagick portátil Q16, Ghostscript 64 bits + runtime do
Visual C++). O `.csproj` junta essa pasta com as DLLs nativas do WPF num `engine.zip` embutido. Na primeira abertura
`EngineInstaller` extrai para `ReduzFacil-dados\engine` (troca atômica: `.novo` → `engine`) e grava o hash; um .exe novo com
motores diferentes extrai de novo.
