# Capturo

Ferramenta de print para Windows no estilo do Lightshot. Fiz porque queria algo pequeno que fizesse só isso e fizesse bem: selecionar uma área, rabiscar em cima e copiar, salvar ou fixar na tela.

É um único `.exe` de uns 80 KB, sem instalação e sem dependências. Fica na bandeja do sistema.

## Stack

C# com WinForms, num arquivo só (`src/Program.cs`). Compila com o `csc.exe` do .NET Framework 4.x que já vem no Windows, então não precisa instalar Visual Studio nem SDK.

## Uso
Rode `dist\Capturo.exe`. Ele fica na bandeja do sistema.

### Atalhos globais (configuráveis em *bandeja → Configurações*)
| Ação | Padrão |
|---|---|
| Capturar área | **Print Screen** (ou clique no ícone da bandeja) |
| Capturar e fixar na tela | **Ctrl + Print Screen** |
| Salvar tela inteira na hora | **Shift + Print Screen** |

Para trocar um atalho, clique no campo e pressione a nova combinação. Esc cancela e Backspace deixa o comando sem atalho.
Teclas comuns (letras, números) exigem Ctrl, Alt, Shift ou Win. F1–F24, Print Screen, Pause, Insert e Scroll Lock podem ser usadas sozinhas.

### Durante a captura
| Ação | Atalho |
|---|---|
| Mover o cursor pixel a pixel (com a lupa) | setas |
| Copiar a cor do pixel (antes de selecionar) | Ctrl+C |
| Selecionar a tela inteira | Ctrl+A |
| Mover/redimensionar a seleção | arrastar / alças / bordas / setas (Shift+setas redimensiona) |
| Lápis, Linha, Seta, Retângulo, Marcador, Texto | P, L, A, R, M, T |
| Paleta de cores | C |
| Espessura do traço/fonte | roda do mouse |
| Linha a 45° / quadrado | segurar Shift ao desenhar |
| Desfazer / Refazer | Ctrl+Z / Ctrl+Y |
| Copiar | Ctrl+C, Enter ou duplo clique |
| Salvar | Ctrl+S |
| **Fixar na tela** | **Ctrl+T** |
| Enviar para a nuvem (copia o link) | Ctrl+D |
| Buscar imagens semelhantes (Google Lens) | Ctrl+G |
| Imprimir | Ctrl+P |
| Cancelar | Esc / botão direito |

### Imagem fixada
A captura vira uma janela flutuante, **sempre visível por cima** das outras.
- Arrastar move a janela; a roda do mouse dá zoom; Ctrl+roda muda a opacidade.
- `0` volta ao tamanho original. Ctrl+C copia, Ctrl+S salva.
- **Ctrl+T** liga/desliga o "sempre visível". Desligado, a imagem aparece na barra de tarefas.
- Esc ou duplo clique fecha. O botão direito abre um menu com todas as opções.
- *Bandeja → Fechar imagens fixadas* fecha todas de uma vez.

### Linha de comando
`Capturo.exe --capture` inicia uma captura e `Capturo.exe --settings` abre as configurações na instância que já está aberta.

## Upload
O envio usa o **catbox.moe** (anônimo, sem conta). A imagem fica **pública** para quem tiver o link. Os últimos links ficam em *bandeja → Envios recentes*.

## Configurações
Os arquivos ficam em `%APPDATA%\Capturo`.

## Compilar
```powershell
./build.ps1
```
O build usa o `csc.exe` que já vem no Windows (.NET Framework 4.x).
