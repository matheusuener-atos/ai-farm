# ADR 0044 — Capacidade de geração por node e o cérebro central

## Status

Aceito em 2026-09-05.

## Contexto

Decisão do dono do produto (Significado B): a **plataforma é o cérebro
único** e a **rede é o músculo compartilhado**. O usuário conversa com uma
Iris só; por baixo, cada pedido de geração é executado por uma máquina da
rede. A restrição física é inescapável: uma inferência roda onde os pesos
estão — não se divide uma resposta única entre máquinas domésticas pela
internet. Logo "compartilhar poder" = distribuir *requisições* entre
máquinas que replicam o modelo, não fatiar uma inferência.

## Decisão

1. **Cérebro central (o "lugar só" real)**: o web service é o ponto único
   que possui a identidade do modelo (`AIFarm:AI:NetworkModel`), recebe toda
   conversa, enfileira e roteia. O usuário nunca fala com uma máquina — fala
   com a plataforma.
2. **Capacidade por node**: `farm_nodes.SupportsGeneration`, detectada pelo
   Agent (sonda o runtime local com cache de 30s) e reportada no heartbeat.
   O seletor de execução só entrega `IRIS_GENERATE` a Agents com a
   capacidade — sem desperdício de tentativas em pool mista.
3. **Modelo fácil no leigo**: o Agent baixa o modelo da plataforma sob
   demanda (`/api/pull` do Ollama) a partir do painel — sem linha de comando.
   Instalar o Ollama uma vez continua sendo o pré-requisito de cada máquina
   que quer *contribuir com geração* (é a física: sem o modelo local, não há
   potência de inferência a doar).
4. **Sem node apto**: a plataforma responde honestamente orientando ativar a
   geração numa máquina da pool; nenhuma cobrança, nenhuma resposta falsa.

## Consequências

- experiência centralizada + compute distribuído: N conversas = N máquinas
  em paralelo; a capacidade da rede cresce com cada máquina que ativa a
  geração;
- a plataforma escolhe e versiona o modelo de um lugar só; trocar o modelo
  da rede é uma variável de ambiente + o pull nos nodes;
- limite honesto declarado: latência é de uma máquina (não somável); mais
  máquinas dão mais vazão, não respostas mais rápidas;
- caminho de verificação (quórum por par) continua sendo o próximo passo
  para geração valer CP — hoje é CP=0, rotulado.
