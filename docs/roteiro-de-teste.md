# Roteiro de teste manual (no seu PC)

O GitHub Actions já testa sozinho, num Windows limpo sem placa de vídeo: compressão de todos os tipos, metas, cortes,
HDR, PDF com senha, arquivo quebrado, fila, pausa, menu do Explorer, pasta vigiada, segunda abertura e capturas de tela.
O que só dá para conferir no seu PC:

1. **Primeira abertura** — coloque o .exe numa pasta nova e abra. Deve aparecer a janela em poucos segundos e a pasta
   `ReduzFacil-dados` ao lado.
2. **Placa de vídeo** — Configurações → Desempenho deve mostrar "Intel Quick Sync" (o seu Intel Ultra 7). Reduza um vídeo
   do celular e veja em "Como" (passe o mouse no item) qual codificador foi usado. Compare o tempo com a opção
   "Usar a placa de vídeo" desligada.
3. **Vídeo do iPhone (HDR, em pé)** — reduza para WhatsApp; as cores devem ficar normais e o vídeo em pé.
4. **Fotos HEIC** — arraste fotos do iPhone; devem virar JPG com as cores certas (compare com "Comparar").
5. **Botão direito** — ligue em Configurações → Windows. No Explorer, botão direito numa foto → "Mostrar mais opções"
   → Reduzir com Reduz Fácil → Para WhatsApp. Selecione 20 arquivos e faça o mesmo: todos devem entrar na mesma janela.
6. **Pasta vigiada** — ligue numa pasta (ex.: Downloads), baixe uma foto grande pelo navegador: ela deve ser reduzida
   só depois de terminar o download, uma vez só.
7. **Bandeja** — com a pasta vigiada ligada, feche a janela: o ícone continua perto do relógio; clique duas vezes para abrir.
8. **Iniciar com o Windows** — ligue, reinicie o PC: o app abre direto na bandeja, vigiando.
9. **Onde está o peso** — procure em Imagens; reduza alguns; use "Originais para a Lixeira" e confira que dá para restaurar.
10. **Mover o .exe** — mova o .exe e a pasta `ReduzFacil-dados` para outro lugar e abra: o menu do botão direito deve
    continuar funcionando (ele se corrige sozinho).
11. **Windows 10** — se tiver um PC com Windows 10, repita 1, 4 e 5.
