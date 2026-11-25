# Decisões

| # | Decisão | Por quê |
|---|---|---|
| 1 | C# / .NET 10 / WPF, .exe único self-contained, dados em `ReduzFacil-dados` ao lado | Mesmo formato do Sobradinho TV para Windows; portátil, sem instalar nada. |
| 2 | Motores por linha de comando: FFmpeg (BtbN gpl-shared), ImageMagick portátil, Ghostscript | São os melhores em cada tipo; o núcleo fica sem pacotes .NET e testável no Linux. |
| 3 | Motores e DLLs nativas do WPF dentro do .exe (zip), extraídos para `engine\` | Um arquivo só para baixar; nada vai para a pasta temporária do Windows. |
| 4 | O original nunca é tocado; saída nova com sufixo `-reduzido`, sem sobrescrever (`(2)`, `(3)`) | Pedido do plano. Ao lado do original o sufixo é obrigatório. |
| 5 | Sem ganho → nada é salvo (mesma regra do web). Exceção: conversão pedida (Converter, formato JPG/WebP escolhido, cortes de vídeo) | Evita "reduzidos" maiores que o original. |
| 6 | Arquivo novo aparece só quando está completo (gravado na pasta `temp` e movido) | Nunca deixa arquivo pela metade no destino, nem com queda de energia. |
| 7 | Mesmos níveis e atalhos do web (Máxima/Equilibrado/Menor; WhatsApp 16, e-mail 18, gov 2, Enem 1, currículo 2 MB) | Quem usa o web reconhece. |
| 8 | Meta em MB: vídeo em 2 passadas no processador; na placa de vídeo 1 passada + nova tentativa se passar | Placa não faz 2 passadas; a conferência garante a meta. |
| 9 | Pouco bit para a resolução → baixa a resolução (e 60→30 fps) automaticamente | Vídeo pequeno e nítido vale mais que vídeo grande e borrado. |
| 10 | Placa de vídeo detectada com teste real; falha numa placa → refaz no processador e desliga após 2 falhas | O FFmpeg lista codificadores que não funcionam sem a placa/driver. |
| 11 | HDR (HLG/PQ) convertido para SDR BT.709 | Vídeo de iPhone fica "lavado" em outros aparelhos se não converter. |
| 12 | Privacidade ligada por padrão: tira EXIF/XMP/IPTC (GPS, câmera) mas mantém o perfil de cor; no vídeo tira metadados e mantém a data de gravação | Fotos P3 do iPhone mudariam de cor sem o perfil. |
| 13 | Data do arquivo novo = data do original | Galerias continuam em ordem. |
| 14 | Fila com duas pistas (vídeos: 1 por padrão; demais: metade dos núcleos, até 4) e motores em prioridade baixa | Vídeo já usa o PC inteiro; o PC continua utilizável. |
| 15 | Pausar = suspender os motores (NtSuspendProcess) | Pausa de verdade, sem perder o progresso. |
| 16 | Pasta vigiada só pega arquivos novos, depois de 2 leituras com o mesmo tamanho e arquivo livre; ignora `-reduzido`, `.crdownload`, `.part`… | Não pega download pela metade nem entra em laço com a própria saída. |
| 17 | Botão direito no registro do usuário (HKCU), por extensão (`SystemFileAssociations`) e pastas | Não precisa de administrador; só aparece em arquivos suportados; some ao desligar. |
| 18 | "Onde está o peso" estima pelo cabeçalho (fotos, WAV/FLAC, PDF) e ffprobe (vídeos); mostra só ganho ≥ 20% e ≥ 300 KB | Varre milhares de arquivos rápido; só o que vale a pena. |
| 19 | Originais para a Lixeira só por botão, com confirmação, só de itens reduzidos com sucesso e menores | É o único jeito de liberar espaço de verdade, e é reversível. |
| 20 | Uma instância; aberturas seguintes mandam os argumentos por "cano" nomeado | O Explorer abre um processo por arquivo selecionado. |
| 21 | Sem assinatura digital e sem atualização automática na 1.0 | Uso pessoal. |
