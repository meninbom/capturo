# REVIEW: "Aqui quebra" (31 s, 1920×1080, 30 fps)

## Decisões criativas
- **Roteiro escolhido:** A, "Aqui quebra". Fluxo: pedido no chat → jeito antigo → tecla PrtScr → captura real → imagem no chat → fixar com Ctrl+T → cartão final.
- **Não construídos:** B ("A lupa", zoom sem fim) e C ("PrtScr", cortes na batida). Os dois estão descritos em `STORYLINES.md`.
- **Interface real do Capturo, sem mockup.** Ela foi renderizada pelo próprio código de desenho do app, que `capture/RenderUI.cs` carrega de `dist\Capturo.exe` por reflexão, sobre a tela da loja fictícia (`capture/bug.html`). São reais: a gravação da sobreposição, a imagem final copiada, a janela fixada com "Sempre visível: ligado" e o menu de contexto.
  - Motivo: o PC estava bloqueado, então não deu para gravar a tela de verdade.
  - **Única peça desenhada:** o cursor do mouse, porque o cursor do sistema não é renderizado fora da tela.
- **Telas que não são do produto:** o chat, a loja "feirinha" e o editor de código foram desenhados para a história. Nenhum imita um app real.
- **Fatos:** "menos de 100 KB" vem do exe atual, que tem 84 KB. "Windows" e "sem instalação" vêm do README.

## O que foi medido e o que só foi olhado
- **`hyperframes check`:** ok, sem erros.
- **Snapshots olhados:** quadros de cada cena, cortes e folha de celular.
- **Som:** só efeitos (cliques, teclas, whoosh e chime do Openverse), posicionados pelo pico de cada arquivo.
  - Não ouvi o resultado; a mixagem foi medida, não escutada.
  - **Não há música:** o catálogo precisa de login no HeyGen.

## Ponto mais fraco
- **Abertura no chat:** a janela tem muito branco vazio, e a primeira mensagem ocupa pouco do quadro. Com mais tempo eu aproximaria a câmera dos balões nos primeiros 2 s.
- **Transição chat → editor (21,9–22,6 s):** fica carregada, com teclas, chat desfocando e editor abrindo ao mesmo tempo.

## Avisos mantidos de propósito
- **`line_unread` nos três cortes do "jeito antigo":** é uma montagem de 1 s por frase, feita para passar pressa. O texto é grande (150–170 px).
- **`line_unread` nas mensagens do chat na cena "pin":** são as mesmas mensagens já lidas antes. Só servem de contexto enquanto a janela sai.
