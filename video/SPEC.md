# SPEC: "Aqui quebra" (storyline A)

GOAL:    quem assiste pensa "aperto uma tecla, desenho uma seta e o print já está no chat" e quer o Capturo.
OUTPUT:  HyperFrames, 1920 x 1080, 30 fps
TIME:    31 s x 30 = 930 frames. Sem música (sem login HeyGen); efeitos sonoros locais (Openverse).
LOOK:    chão #EEF1F5 (desktop claro, sem textura); tinta #16161C; acento #7C4DFF (a cor real do Capturo),
         só em: a palavra "mandou", a marca no cartão final e a borda da seleção (que já é do app).
         Campos chapados da "tensão": #16161C com tipo branco.
         Display: Inter 900, 120-170 px, tracking -3%; texto: Inter 700, 34-44 px.
         Meio: interface real do Capturo (renderizada pelo próprio código do app) sobre telas de um
         desktop claro; janelas com raio 18 px, sombra 0 24px 60px rgba(20,24,40,.18).
OBJECTS: janela de chat (cabeçalho com avatar "MQ", balões cinza/brancos), tecla física (keycap
         branco com borda inferior de 10 px #C9CED8 e legenda Inter 700), tela da loja com "R$ NaN",
         a sobreposição real do Capturo (lupa, barras, seta, texto), imagem fixada + menu real.
MOTION:  entrada expo.out 0.5-0.7 s; câmera power2.inOut 0.6 s; saída power3.in 0.3 s;
         tecla afunda 4 frames (y +10 px, borda 10 → 2 px) e volta em 6 frames.
         Títulos: por palavra, blur 6 → 0 px e opacidade, 0.067 s entre palavras, sem translate.
SHOTS:
  1  0.0-3.0   chat: balão "o checkout quebrou de novo" já visível no frame 0; chega "consegue mandar
               print de onde dá o erro?" (0.6 s, chime); indicador "digitando" do lado de cá. Sai por
               whip para a esquerda.
  2  3.0-6.0   o jeito antigo, 3 cortes secos de 1 s em #16161C: "Print da tela inteira", "Abre um editor",
               "Recorta. Salva. Anexa." Um relógio de traço gira 1 volta por corte.
  3  6.0-8.6   revelação: "Ou só:" (6.0) → a tecla PrtScr gigante entra (6.5), afunda em 8.0 (mechkey),
               flash branco de 3 frames vira a próxima cena. A linha de cima nomeia o produto:
               "Com o [ícone] Capturo, é só:".
  4  8.6-17.6  prova: gravação real (overlay-rec.mp4, 8.2 s) em tela cheia; câmera dá punch-in 1.45x na
               seleção em 10.8 s e volta em 15.4 s. Verbos grandes no canto inferior esquerdo:
               "Recorta." (9.2), "Marca." (11.4), "Escreve." (13.6). Teclas Ctrl + C entram em 16.6.
  5  17.6-21.6 recompensa: a imagem anotada sai do lugar da seleção e voa (carry across, 0.7 s) para o
               chat, virando a mensagem enviada; Marina responde "achei! é o cupom vazio" (chime).
  6  21.6-25.6 fixar: teclas Ctrl + T; a imagem sai do chat e pousa sobre o editor de código como janela
               fixada real ("Sempre visível: ligado"); o menu real aparece com "Sempre visível" em destaque.
               Título: "Fica por cima de tudo."
  7  25.6-27.6 reforço: "Uma tecla." / "Marcou, mandou." em fundo #EEF1F5, "mandou" em #7C4DFF.
  8  27.6-31.0 cartão final: o ícone real cai como uma tecla e afunda; "Capturo" entra por wipe; linha
               "Windows · menos de 100 KB · sem instalação"; segura 2.4 s.
BANNED:  fundo escuro com coisas pequenas no meio; brilho, partículas, gradiente roxo (fora o ícone real);
         três cards em linha; título sobre print escurecido; logo no canto; tudo entrando com fade+slide-up;
         títulos que continuam se mexendo depois de pousar; interface inventada para o Capturo.
QA:      0.0 (thumbnail: chat com o pedido legível), 4.0 (tensão legível), 8.0 (tecla afundando),
         9.5 (lupa real), 13.0 (punch-in com seta), 17.0 (Ctrl+C), 19.5 (imagem no chat), 23.5 (pin+menu),
         26.5 (reforço), 30.0 (cartão final).
