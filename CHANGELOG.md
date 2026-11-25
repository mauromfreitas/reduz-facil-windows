# Changelog

## 1.2.2

- **Mapa do disco:** os maiores arquivos de cada pasta (até 20, a partir de 5 MB) aparecem como blocos próprios;
  os pequenos ficam agrupados. Clique num arquivo para reduzir na hora, abrir ou mostrar no Explorer.

## 1.2.1

- **Início:** nova primeira tela com o espaço livre de cada disco, o total economizado (desde sempre e no mês)
  e até 3 sugestões (disco quase cheio, pastas com muito peso em mídia).
- **Mapa do disco** (em Onde está o peso): blocos do tamanho de cada pasta, com cor pelo tipo que mais ocupa
  (fotos, vídeos, áudios, PDFs, outros); clique para entrar na pasta.
- **Reduzir selecionados** (Onde está o peso): confirma antes (quantos, como e a economia estimada), começa na hora
  e mostra o progresso na própria tela; no fim destaca "Originais para a Lixeira".
- **Nome do arquivo sem versão:** as Releases passam a ser `ReduzFacil.exe`, e quem tem `ReduzFacil-1.x.y.exe`
  passa a ter `ReduzFacil.exe` depois desta atualização (o menu do botão direito se ajusta sozinho).

## 1.2.0

- **Atalho Discord:** cabe no limite de envio do Discord e sai em MP4 H.264 (toca direto no chat). Escolha o plano:
  Grátis · 20 MB (padrão), Nitro Basic · 50 MB ou Nitro · 500 MB. Também no botão direito do Explorer, nas pastas
  vigiadas e nos perfis; funciona com "dividir vídeo longo em partes".
- **"Começar sozinho" vem desligado:** os arquivos esperam o botão Iniciar, para dar tempo de conferir os ajustes
  (pasta vigiada e Ctrl+V continuam começando sozinhos).

## 1.1.3

- "Procurar agora": mensagem mais simples quando já está na versão mais nova.

## 1.1.2

- Trocar o tema com o app aberto atualiza na hora também os ícones coloridos das listas.

## 1.1.1

- Tema escuro: nomes dos arquivos nas listas (fila, Onde está o peso, Histórico) ficavam escuros demais para ler.

## 1.1.0

- **Atualização com um clique:** o app procura versão nova ao abrir (e uma vez por dia), mostra a faixa
  "Versão X disponível · Atualizar", baixa, confere (tamanho e SHA-256), espera a fila terminar e reabre já atualizado.
  Configurações e histórico continuam. (A partir desta versão; da 1.0 para a 1.1 é preciso baixar uma vez.)
- **Repetidos** (em Onde está o peso): arquivos idênticos com nomes diferentes e fotos parecidas (sequências, mesma foto
  em outro tamanho). Parecidas vêm com aviso e nunca pré-marcadas; sempre fica um de cada grupo; vai para a Lixeira.
- **Dividir vídeo longo** em partes do tamanho da meta (ex.: 16 MB para o WhatsApp): em Ajustes do vídeo ou na opção
  "Vídeo longo: dividir em partes" dos atalhos com meta.
- **Ctrl+V:** cola arquivos copiados no Explorer, prints e imagens. Print colado já volta copiado depois de reduzido.
  Botão **Copiar** em cada item pronto.
- **Várias pastas vigiadas**, cada uma com o seu jeito de reduzir. A pasta da 1.0 vira a primeira da lista.
- **Ferramentas de PDF:** juntar fotos num PDF (A4 ou tamanho da foto, já reduzido) e separar páginas.
- **Tema escuro:** Claro, Escuro ou Igual ao Windows (em Configurações).

## 1.0.0 — primeira versão

Tudo do plano aprovado (1.0 + 1.1 juntas):

- Fotos (JPG, PNG, WebP, HEIC, AVIF, BMP), vídeos, áudios e PDFs, com os níveis e atalhos do Reduz Fácil web.
- Fila sem limite, vários ao mesmo tempo, pausar/continuar, cancelar, tentar de novo.
- Vídeo pela placa (NVIDIA, Intel Quick Sync, AMD) com volta automática ao processador; H.264 ou H.265;
  meta exata em MB; HDR → cores normais; cortar, girar, resolução, tirar o som, só o áudio.
- PDF pelo Ghostscript, com tons de cinza; aviso claro para PDF com senha.
- Antes e depois lado a lado, com zoom 100%.
- Onde está o peso: estimativa por pasta/disco, reduzir selecionados, originais para a Lixeira.
- Pasta vigiada, perfis salvos, botão direito do Explorer, bandeja, iniciar com o Windows.
- Privacidade: remove GPS e dados da câmera.
- Histórico e total economizado.
