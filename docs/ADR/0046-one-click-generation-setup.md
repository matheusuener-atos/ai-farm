# ADR 0046 — Ativação de geração em um clique (instala runtime + baixa modelo)

## Status

Aceito em 2026-09-05.

## Contexto

Farmar nunca exigiu Ollama (só a geração exige — ADR 0043/0044). Mas ativar
uma máquina para gerar pedia instalar o Ollama e baixar o modelo na mão. O
dono do produto escolheu a opção A: um clique no painel do Agent faz tudo.

## Decisão

- **Identidade carrega o modelo**: `GET /api/farm/agents/identity` passa a
  incluir `NetworkModel` (o modelo que a plataforma serve, `AIFarm:AI:NetworkModel`),
  para o Agent saber o que baixar.
- **Card "Geração de IA (opcional)"** no painel: mostra o estado (OCIOSO /
  INSTALANDO / BAIXANDO / PRONTO / ERRO) e um botão "Ativar geração neste
  computador".
- **Um clique orquestra** (`AgentUiHost.EnableGeneration`, em segundo plano):
  se o runtime falta, `AgentModelRuntime.InstallOllamaAsync` baixa o
  instalador oficial e roda `/VERYSILENT` (UAC é o consentimento); espera o
  serviço subir; então `PullAsync` baixa o modelo; ao terminar, o heartbeat
  reporta `SupportsGeneration=true` e a máquina entra na rota de geração.
- **Best-effort e honesto**: só Windows; falhas (UAC negado, rede, serviço
  que não sobe) viram estado ERRO com mensagem e o link oficial como plano B.
  Farmar segue intacto independentemente.

## Consequências

- o leigo ativa geração sem linha de comando; farmar continua exigindo zero
  configuração;
- a automação da instalação de terceiro é assumida como melhor-esforço, não
  garantia — o card comunica isso;
- o modelo da rede é definido num lugar só (plataforma) e propagado pela
  identidade; trocar o modelo é uma env var + novo pull nos nodes.
