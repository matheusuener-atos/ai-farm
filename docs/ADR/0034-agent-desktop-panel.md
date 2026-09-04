# ADR 0034 — Painel local do Compute Agent

## Status

Aceito em 2026-09-04.

## Contexto

O Compute Agent era operado apenas por linha de comando (`enroll`, `run`,
`pause`, `limits`), o que exclui o público do Farmer Lite (ADR 0030). O dono
do produto pediu uma interface com a identidade Macintosh do site: controle
de "Farmar", medições de poder computacional em tempo real, identificação da
conta vinculada e rotação amigável de credenciais.

## Decisão

O Agent ganha um modo `ui`, que passa a ser o padrão quando o executável é
aberto sem argumentos (duplo clique). Nele:

- um Kestrel embutido (`FrameworkReference Microsoft.AspNetCore.App`) serve o
  painel em `http://127.0.0.1:{porta aleatória}/{token de sessão}/` — só
  loopback, com token imprevisível no caminho; WinForms/WPF foram descartados
  porque o publish do Agent acontece no Docker Linux do Render, onde o SDK
  Desktop do Windows não compila, e HttpListener exigiria ACL de URL;
- a janela abre via Edge `--app=` (app window, sem barra de endereço), com
  fallback para o navegador padrão;
- um singleton `AgentTelemetry` observa a sessão (fase, jobs ativos, CP
  verificado, CPU/memória vivas dos workers amostradas no loop de 25ms do
  runner, diário de eventos) e alimenta o painel por polling de 2s;
- os controles reusam os mecanismos existentes: Farmar/Pausar e limites
  gravam a política local que o loop de heartbeats já relê a cada iteração;
- a credencial vive num porta-credencial mutável (`AgentCredentialHandle`)
  lido a cada chamada, então a rotação no meio da sessão — inclusive com job
  em andamento — não interrompe nada;
- o servidor expõe dois endpoints novos autenticados pela credencial do
  Agent: `GET /api/farm/agents/identity` (conta, node, opt-in de plataforma,
  emissão da credencial) e `POST /api/farm/agents/credential/rotate` (novo
  segredo para o mesmo node, hash anterior invalidado atomicamente).

## Consequências

- o leigo baixa o executável, dá dois cliques e opera tudo pela janela:
  vincular, farmar, pausar, limitar, rotacionar e acompanhar as medições;
- `run` permanece headless para terminal e automação; os comandos de CLI
  continuam funcionando;
- a rotação amigável reduz o custo de higiene de segurança e mantém o mesmo
  NodeId — histórico, quarentena e verificação de perfil intactos;
- o painel nunca sai do loopback e não abre porta externa; outra conta local
  precisaria adivinhar o token de 128 bits da sessão;
- o executável cresce (~ASP.NET embarcado) — aceito em troca de robustez de
  binding sem privilégios de administrador.
