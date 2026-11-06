# Performance do Capturo — 06/11/2026

## Entrega

Executável validado: `dist/Capturo-otimizado.exe`, sem novas dependências.
O `dist/Capturo.exe` original estava em execução e bloqueado para escrita;
ele foi preservado. Para usar as melhorias, saia pelo menu da bandeja e abra
o executável otimizado. Abrir outra cópia com a instância antiga ativa não a atualiza.

## Alterações

- GIF: reaproveitamento de dois buffers de pixels e dos histogramas/paleta,
  evitando grandes alocações a cada quadro. Quadros idênticos continuam prolongando o anterior.
- Editor: os JPEGs da prévia usam até 16 MiB de RAM; acima disso, migram para um
  arquivo temporário exclusivo, apagado ao fechar, inclusive se o processo terminar.
  O índice, o quadro decodificado e os buffers de vídeo são adicionais a esse orçamento.
- Miniaturas da timeline são reduzidas a 100 pixels de altura, em vez de manter
  imagens de prévia de até 1280 pixels. Os streams usados na decodificação são descartados.
- Galeria: redução direta da imagem aberta, eliminando uma cópia intermediária em resolução integral.
- Recursos: descarte do último quadro ao fechar, das regiões de recorte GDI e de
  miniaturas de papel de parede substituídas. O reset do OCR libera seu mapa de palavras.
- Removidas as duas coletas de lixo forçadas, a espera por finalizadores e a expulsão
  do working set que ocorriam na thread da interface. O Windows e o GC gerenciam a memória;
  o número de RAM residente pode ficar maior sem significar mais memória efetivamente alocada.
- Build: destino configurável e espera pelo término real da geração do ícone.

## Medição

Benchmark local com compilação otimizada, 120 quadros de 640×360, incluindo movimento
e quadros repetidos. Cinco processos independentes por versão após uma execução inicial.
As medições incluem o encoder e a validação do GIF; não representam velocidade geral do app.

| Medida | Antes | Depois |
| --- | ---: | ---: |
| Tempo mediano de codificação | 87 ms | 61 ms |
| Alocações gerenciadas medianas | 162.376.304 bytes | 12.479.464 bytes |
| Tamanho do GIF | 47.047 bytes | 47.047 bytes |

Isso corresponde a aproximadamente 30% menos tempo e 92% menos alocações neste cenário.
O GIF foi idêntico byte a byte, com SHA-256
`2E575A4E5443374814D3D0CD50B438B17F535669D9BDAEF1931BDAE6F7CBD28E`.

## Validação

- Build de produção concluído pelo compilador .NET Framework do Windows.
- `test/performance.ps1 -Suite GifPerformance`: codificação, leitura e contagem de quadros.
- `test/performance.ps1 -Suite PreviewPerformance`: RAM, migração para disco,
  busca antes/depois da migração, quadro repetido, descarte concorrente,
  remoção do temporário e rejeição de gravações após fechar.
- O mesmo teste gera um MP4 real pelo Media Foundation, decodifica a prévia,
  verifica pixels em dois momentos, miniaturas e fechamento durante a leitura.
- `test/performance.ps1 -Suite OcrSelectionSmoke`: OCR real, seleção, cópia,
  acentos, clipboard preservado e janela aberta após copiar.
- `git diff --check` sem erros de whitespace.

Não foram medidos FPS de captura de tela/janela, webcam, áudio ou uma sessão longa
de edição com interação manual. O áudio da prévia ainda carrega as trilhas em memória;
o limite de 16 MiB se aplica somente aos JPEGs. A migração para disco troca RAM por I/O
e requer espaço temporário disponível. Não há alegação de ganho nesses fluxos não medidos.

## Reproduzir

```powershell
./build.ps1 -OutputDirectory (Join-Path $PWD 'test/artifacts/app')
./test/performance.ps1 -Suite GifPerformance
./test/performance.ps1 -Suite PreviewPerformance
./test/performance.ps1 -Suite OcrSelectionSmoke
```

Os resultados gerados ficam em `test/artifacts`, ignorado pelo Git.
