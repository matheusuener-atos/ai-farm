namespace atos.AIFarm.Agent;

/// <summary>
/// Página única do painel local. Mesma linguagem visual da mesa Macintosh do
/// site (tokens de app.css), sem dependências externas: tudo inline para
/// funcionar offline e dentro do executável único.
/// </summary>
internal static class AgentUiPage
{
    public const string Html = """
<!doctype html>
<html lang="pt-BR">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Compute Agent — AI Farm</title>
<link rel="icon" href="data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 20 20' fill='none' stroke='%23FF4F1F' stroke-width='1.6' stroke-linecap='round' stroke-linejoin='round'%3E%3Cpath d='M4 16c0-7 4-11 12-12-1 8-5 12-12 12z'/%3E%3Cpath d='M4 16l7-7'/%3E%3C/svg%3E">
<style>
:root {
  --desk:#E3E1DA; --desk-dot:#B8B6AF; --surface:#FFFFFF; --surface-2:#F5F4F0;
  --ink:#111111; --muted:#6B6B66; --line:#E6E4DE;
  --orange:#FF4F1F; --orange-soft:#FFF1EB;
  --green:#1C7A36; --green-deep:#EEF7EE; --amber:#8A5A00; --amber-deep:#FFF8E1;
  --red:#C6281C;
}
* { box-sizing:border-box; }
body {
  margin:0; min-width:320px; color:var(--ink); font-size:13px; line-height:1.5;
  font-family:"Instrument Sans","Helvetica Neue",Helvetica,Arial,sans-serif;
  -webkit-font-smoothing:antialiased; background-color:var(--desk);
  background-image:url("data:image/svg+xml;utf8,<svg xmlns='http://www.w3.org/2000/svg' width='2' height='2'><rect width='1' height='1' fill='%23B8B6AF'/><rect x='1' y='1' width='1' height='1' fill='%23B8B6AF'/></svg>");
}
.menubar {
  position:sticky; top:0; z-index:5; display:flex; align-items:center; gap:12px;
  height:30px; padding:0 12px; background:var(--surface);
  border-bottom:1.5px solid var(--ink); font-weight:700; font-size:12px;
}
.menubar .right { margin-left:auto; display:flex; align-items:center; gap:8px; font-weight:400; }
.brand { display:flex; align-items:center; gap:8px; }
.brand svg { width:15px; height:15px; flex-shrink:0; }
.dot { width:9px; height:9px; border:1.5px solid var(--ink); border-radius:50%; background:var(--muted); }
.dot.on { background:var(--orange); }
.dot.ok { background:var(--green); }
.dot.err { background:var(--red); }
main { max-width:980px; margin:0 auto; padding:20px 14px 48px; display:grid; gap:14px; }
.win { background:var(--surface); border:1.5px solid var(--ink); box-shadow:2px 2px 0 var(--ink); }
.win-title {
  display:flex; align-items:center; gap:10px; min-height:26px; padding:0 8px;
  border-bottom:1.5px solid var(--ink); font-size:12px; font-weight:700;
  background:repeating-linear-gradient(to bottom, var(--ink) 0 1px, #FFFFFF 1px 3px);
}
.win-title::before, .win-title::after { content:""; width:12px; height:12px; border:1.5px solid var(--ink); background:#FFFFFF; flex-shrink:0; }
.win-title::after { margin-left:auto; }
.win-title span { background:#FFFFFF; padding:2px 8px; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
.win-body { padding:14px; }
.grid2 { display:grid; grid-template-columns:1fr 1fr; gap:14px; }
@media (max-width:760px){ .grid2 { grid-template-columns:1fr; } }
.farm-row { display:flex; align-items:center; gap:18px; flex-wrap:wrap; }
button {
  font:inherit; color:var(--ink); background:var(--surface); cursor:pointer;
  border:1.5px solid var(--ink); padding:8px 18px; font-weight:700;
  box-shadow:2px 2px 0 var(--ink);
}
button:active { transform:translate(2px,2px); box-shadow:none; }
button:disabled { opacity:.45; cursor:default; }
button.big { font-size:16px; padding:14px 34px; letter-spacing:.02em; }
button.primary { background:var(--orange); color:#FFFFFF; }
button.primary-quiet { background:var(--orange-soft); color:var(--orange); }
button.danger { background:var(--surface); color:var(--red); }
.phase { font-weight:700; }
.phase small { display:block; font-weight:400; color:var(--muted); }
.kpis { display:grid; grid-template-columns:repeat(3,minmax(0,1fr)); gap:1px; background:var(--ink); border:1px solid var(--ink); }
@media (max-width:560px){ .kpis { grid-template-columns:repeat(2,minmax(0,1fr)); } }
.kpi { background:var(--surface); padding:10px 12px; min-height:72px; }
.kpi small { display:block; color:var(--muted); font-size:.68rem; text-transform:uppercase; letter-spacing:.04em; }
.kpi strong { font-size:19px; font-variant-numeric:tabular-nums; }
.kpi span { color:var(--muted); font-size:.72rem; }
table { width:100%; border-collapse:collapse; font-variant-numeric:tabular-nums; }
th, td { text-align:left; padding:6px 8px; border-bottom:1px solid var(--line); font-size:12px; }
th { color:var(--muted); font-size:.66rem; text-transform:uppercase; letter-spacing:.04em; }
.empty { color:var(--muted); padding:8px; }
dl { display:grid; grid-template-columns:auto 1fr; gap:4px 14px; margin:0; }
dt { color:var(--muted); }
dd { margin:0; overflow-wrap:anywhere; }
form.stack { display:grid; gap:10px; max-width:420px; }
label { display:grid; gap:4px; color:var(--muted); font-size:.72rem; }
label > span { display:block; }
input {
  font:inherit; border:1.5px solid var(--ink); background:var(--surface);
  padding:8px 10px; width:100%;
}
input:focus, button:focus-visible { outline:2px solid var(--orange); outline-offset:2px; }
input[type=range] { -webkit-appearance:none; appearance:none; height:14px; padding:0; background:var(--surface-2); cursor:pointer; }
input[type=range]::-webkit-slider-thumb { -webkit-appearance:none; appearance:none; width:16px; height:24px; background:var(--orange); border:1.5px solid var(--ink); box-shadow:1px 1px 0 var(--ink); }
input[type=range]::-moz-range-thumb { width:16px; height:24px; background:var(--orange); border:1.5px solid var(--ink); border-radius:0; box-shadow:1px 1px 0 var(--ink); }
label b { color:var(--ink); font-variant-numeric:tabular-nums; }
.perf-head { display:flex; align-items:baseline; gap:14px; flex-wrap:wrap; }
.perf-head strong { font-size:15px; letter-spacing:.01em; }
.perf-head strong.on { color:var(--orange); }
.perf-util { color:var(--muted); font-size:12px; font-variant-numeric:tabular-nums; }
#perf-chart { display:block; width:100%; height:150px; margin-top:10px; border:1.5px solid var(--ink); background:var(--surface-2); }
.confirm-key { margin-top:12px; padding:12px 14px; border:1.5px solid var(--ink); background:var(--amber-deep); font-size:13px; }
.confirm-key b { font-size:22px; letter-spacing:.08em; font-variant-numeric:tabular-nums; display:inline-block; margin:0 6px; }
.confirm-key small { display:block; color:var(--muted); margin-top:4px; }
.gen-row { display:flex; align-items:center; gap:14px; flex-wrap:wrap; }
.gen-row span { flex:1 1 240px; }
.bars { display:grid; gap:10px; }
.bar-row { display:grid; grid-template-columns:150px 1fr 150px; gap:12px; align-items:center; }
@media (max-width:560px){ .bar-row { grid-template-columns:110px 1fr 110px; } }
.bar-label { color:var(--muted); font-size:.72rem; text-transform:uppercase; letter-spacing:.04em; }
.bar-track { height:14px; border:1.5px solid var(--ink); background:var(--surface-2); overflow:hidden; }
.bar-fill { height:100%; width:0%; background:var(--orange); transition:width .6s ease; }
.bar-fill.auth { background:var(--ink); }
.bar-value { font-size:12px; font-variant-numeric:tabular-nums; text-align:right; }
.log {
  background:var(--surface-2); border-top:1px solid var(--line);
  font-family:ui-monospace,Consolas,monospace; font-size:11px; line-height:1.6;
  max-height:220px; overflow:auto; padding:10px 12px; white-space:pre-wrap; overflow-wrap:anywhere;
}
.note { color:var(--muted); font-size:.74rem; margin-top:8px; }
.banner { border:1.5px solid var(--ink); background:var(--amber-deep); padding:10px 12px; margin-bottom:12px; }
.banner.err { background:#FBEAE8; color:var(--red); }
[hidden] { display:none !important; }
.footer { display:flex; align-items:center; gap:12px; }
</style>
</head>
<body>
<header class="menubar">
  <span class="brand"><svg viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M4 16c0-7 4-11 12-12-1 8-5 12-12 12z"/><path d="M4 16l7-7"/></svg>AI Farm — Compute Agent</span>
  <span class="right"><span id="phase-dot" class="dot"></span><span id="phase-short">carregando…</span></span>
</header>
<main>
  <section class="win">
    <div class="win-title"><span>MEU COMPUTADOR</span></div>
    <div class="win-body">
      <div id="error-banner" class="banner err" hidden></div>
      <div class="farm-row">
        <button id="farm-toggle" class="big primary" disabled>FARMAR</button>
        <div class="phase"><span id="phase-text">Conectando ao painel…</span><small id="phase-detail"></small></div>
      </div>
      <div id="confirm-key" class="confirm-key" hidden>
        CHAVE DE CONFIRMAÇÃO: <b id="confirm-key-value">—</b>
        <small>Digite no card deste computador no site. Ela não será exibida de novo — para refazer, exclua a máquina da rede e adicione novamente.</small>
      </div>
      <p class="note">Farmar deixa este computador à disposição da rede: ele executa trabalhos verificados,
      acumula CP e a rede nunca acessa seus arquivos — só os workloads do catálogo autorizado.</p>
    </div>
  </section>

  <section class="win">
    <div class="win-title"><span>DESEMPENHO</span></div>
    <div class="win-body">
      <div class="perf-head">
        <strong id="perf-status">—</strong>
        <span id="perf-util" class="perf-util">0% da capacidade autorizada</span>
      </div>
      <canvas id="perf-chart" width="1000" height="150" aria-label="Utilização da capacidade autorizada nos últimos 3 minutos"></canvas>
      <p class="note">Últimos 3 minutos · área laranja = poder computacional sendo entregue à rede.</p>
    </div>
  </section>

  <section class="win">
    <div class="win-title"><span>PODER COMPUTACIONAL</span></div>
    <div class="win-body">
      <div class="bars">
        <div class="bar-row"><span class="bar-label">CPU autorizada</span>
          <div class="bar-track"><div id="bar-cpu-auth" class="bar-fill auth"></div></div>
          <span id="bar-cpu-auth-text" class="bar-value">—</span></div>
        <div class="bar-row"><span class="bar-label">CPU em uso agora</span>
          <div class="bar-track"><div id="bar-cpu-live" class="bar-fill"></div></div>
          <span id="bar-cpu-live-text" class="bar-value">—</span></div>
        <div class="bar-row"><span class="bar-label">Memória autorizada</span>
          <div class="bar-track"><div id="bar-mem-auth" class="bar-fill auth"></div></div>
          <span id="bar-mem-auth-text" class="bar-value">—</span></div>
        <div class="bar-row"><span class="bar-label">Memória em uso agora</span>
          <div class="bar-track"><div id="bar-mem-live" class="bar-fill"></div></div>
          <span id="bar-mem-live-text" class="bar-value">—</span></div>
      </div>
      <p id="potential" class="note">—</p>
    </div>
  </section>

  <section class="win">
    <div class="win-title"><span>MEDIÇÕES EM TEMPO REAL</span></div>
    <div class="win-body">
      <div class="kpis">
        <div class="kpi"><small>Trabalhos agora</small><strong id="kpi-active">0</strong></div>
        <div class="kpi"><small>CP verificado na sessão</small><strong id="kpi-cp">0,000</strong></div>
        <div class="kpi"><small>Ritmo</small><strong id="kpi-cph">0,0</strong><span> CP/hora</span></div>
        <div class="kpi"><small>CPU dos trabalhos</small><strong id="kpi-cpu">0%</strong><span id="kpi-cpu-total"></span></div>
        <div class="kpi"><small>Memória dos trabalhos</small><strong id="kpi-mem">0 MB</strong><span id="kpi-mem-peak"></span></div>
        <div class="kpi"><small>Concluídos / falhas</small><strong id="kpi-done">0 / 0</strong></div>
        <div class="kpi"><small>Ganhos da sessão</small><strong id="kpi-earn">—</strong><span id="kpi-earn-note"></span></div>
        <div class="kpi"><small>CP total deste computador</small><strong id="kpi-lifetime">—</strong><span> resgate em Farm → Recompensas</span></div>
      </div>
      <div id="jobs-wrap">
        <table id="jobs" hidden>
          <thead><tr><th>Trabalho</th><th>Início</th><th>Decorrido</th><th>CPU</th><th>Memória</th></tr></thead>
          <tbody></tbody>
        </table>
        <p id="jobs-empty" class="empty">Nenhum trabalho em execução neste momento.</p>
      </div>
    </div>
  </section>

  <div class="grid2">
    <section class="win">
      <div class="win-title"><span>CONTA E VÍNCULO</span></div>
      <div class="win-body">
        <div id="identity-block" hidden>
          <dl>
            <dt>Conta</dt><dd id="id-account">—</dd>
            <dt>Computador</dt><dd id="id-node">—</dd>
            <dt>Servidor</dt><dd id="id-server">—</dd>
            <dt>Ajudar a plataforma</dt><dd id="id-share">—</dd>
            <dt>Situação no servidor</dt><dd id="id-status">—</dd>
          </dl>
          <p class="note">Para mudar “Ajudar a plataforma”, use a página Meu computador no site.</p>
        </div>
        <form id="enroll-form" class="stack" hidden>
          <p id="enroll-hint">Este computador ainda não está vinculado. Gere um código na página
          <strong>Meu computador</strong> do site e cole aqui.</p>
          <label>URL da AI Farm<input id="enroll-url" placeholder="https://ai-farm-z5yp.onrender.com" required></label>
          <label>E-mail da conta<input id="enroll-email" type="email" autocomplete="off" required></label>
          <label>Código de vínculo<input id="enroll-code" autocomplete="off" required></label>
          <button type="submit" class="primary">Vincular este computador</button>
        </form>
      </div>
    </section>

    <section class="win">
      <div class="win-title"><span>CREDENCIAL</span></div>
      <div class="win-body">
        <dl>
          <dt>Guardada em</dt><dd>Cofre de credenciais do Windows</dd>
          <dt>Emitida em</dt><dd id="cred-issued">—</dd>
          <dt>Node</dt><dd id="cred-node">—</dd>
        </dl>
        <p class="note">Rotacionar cria uma credencial nova e invalida a anterior no servidor,
        sem interromper trabalhos em andamento.</p>
        <button id="rotate" disabled>Rotacionar credencial</button>
      </div>
    </section>
  </div>

  <section class="win">
    <div class="win-title"><span>FARMAR NO MODO FORTE</span></div>
    <div class="win-body">
      <div class="gen-row">
        <span id="gen-status">Modo forte desligado — esta máquina ainda não roda a IA da rede.</span>
        <button id="gen-enable" class="primary" disabled>Ativar o modo forte</button>
      </div>
      <p class="note">É o mesmo Farmar, no grau mais forte: além de emprestar poder para tarefas conferidas, esta máquina passa a <b>rodar a IA</b> da rede. Um clique instala o runtime e baixa o modelo (alguns GB; o Windows pode pedir confirmação). Por enquanto o modo forte <b>ainda não paga CP</b> — a verificação que vai permitir pagar com segurança está em construção (ADR 0047).</p>
      <p id="gen-detail" class="note" hidden></p>
    </div>
  </section>

  <section class="win">
    <div class="win-title"><span>LIMITES DO MEU COMPUTADOR</span></div>
    <div class="win-body">
      <form id="limits-form" class="stack">
        <label><span>Processadores lógicos para a rede: <b id="limit-cpu-value">?</b> de <span id="cpu-max">?</span></span>
          <input id="limit-cpu" type="range" min="1" step="1" required></label>
        <label><span>Memória para a rede: <b id="limit-mem-value">?</b> MB de <span id="mem-max">?</span> MB</span>
          <input id="limit-mem" type="range" min="64" step="64" required></label>
        <button type="submit">Aplicar limites</button>
      </form>
      <p class="note">Os limites valem a partir do próximo trabalho aceito.</p>
    </div>
  </section>

  <section class="win">
    <div class="win-title"><span>DIÁRIO DA SESSÃO</span></div>
    <div id="log" class="log">—</div>
  </section>

  <section class="win">
    <div class="win-body footer">
      <button id="exit" class="danger">Sair e parar de farmar</button>
      <span class="note">Fechar só esta janela mantém o Agent farmando em segundo plano.</span>
    </div>
  </section>
</main>
<script>
const base = location.pathname.endsWith('/') ? location.pathname : location.pathname + '/';
const $ = id => document.getElementById(id);
const fmtMb = bytes => (bytes / 1048576).toLocaleString('pt-BR', {maximumFractionDigits: 1}) + ' MB';
const fmtCp = milli => (milli / 1000).toLocaleString('pt-BR', {minimumFractionDigits: 3, maximumFractionDigits: 3});
let busy = false;

const PHASES = {
  FARMANDO:            {dot:'ok',  text:'Farmando — seu computador está à disposição da rede.'},
  EM_ESPERA:           {dot:'on',  text:'Pronto — aguardando a rede enviar trabalhos.'},
  PAUSADO:             {dot:'',    text:'Pausado — nenhum trabalho novo será aceito.'},
  CONECTANDO:          {dot:'on',  text:'Conectando ao servidor…'},
  RECONECTANDO:        {dot:'on',  text:'Reconectando ao servidor…'},
  SEM_VINCULO:         {dot:'',    text:'Sem vínculo — vincule este computador para começar.'},
  AGUARDANDO_CONFIRMACAO: {dot:'on', text:'Vinculado — aguardando você digitar a chave de confirmação no site.'},
  CREDENCIAL_RECUSADA: {dot:'err', text:'Credencial recusada — rotacione ou refaça o vínculo.'},
  ERRO:                {dot:'err', text:'Erro na sessão — veja o diário.'},
  PARADO:              {dot:'',    text:'Parado.'}
};

async function post(path, body) {
  if (busy) return null;
  busy = true;
  try {
    const response = await fetch(base + 'api/' + path, {
      method: 'POST',
      headers: {'Content-Type': 'application/json'},
      body: JSON.stringify(body || {})
    });
    const payload = await response.json().catch(() => ({}));
    if (!response.ok || payload.ok === false) {
      showError(payload.error || ('Falha na ação (' + response.status + ').'));
      return null;
    }
    showError(null);
    return payload;
  } catch (e) {
    showError('O painel perdeu contato com o Agent. Ele ainda está aberto?');
    return null;
  } finally {
    busy = false;
    refresh();
  }
}

function showError(message) {
  const banner = $('error-banner');
  banner.hidden = !message;
  banner.textContent = message || '';
}

let limitsDirty = false;
function syncLimitLabels() {
  $('limit-cpu-value').textContent = $('limit-cpu').value;
  $('limit-mem-value').textContent = parseInt($('limit-mem').value, 10).toLocaleString('pt-BR');
}
['limit-cpu', 'limit-mem'].forEach(id => $(id).addEventListener('input', () => {
  limitsDirty = true;
  syncLimitLabels();
}));

function drawChart(samples, capacityPct) {
  const canvas = $('perf-chart');
  const ctx = canvas.getContext('2d');
  const w = canvas.width, h = canvas.height;
  ctx.clearRect(0, 0, w, h);
  // Grade horizontal a cada 25% da capacidade autorizada.
  ctx.strokeStyle = '#D8D6CF';
  ctx.lineWidth = 1;
  for (let i = 1; i < 4; i++) {
    const y = h - (h * i / 4);
    ctx.beginPath(); ctx.moveTo(0, y); ctx.lineTo(w, y); ctx.stroke();
  }
  if (!samples.length) return;
  const span = 180; // segundos visíveis
  const nowSec = Math.floor(Date.now() / 1000);
  const x = t => w - ((nowSec - t) / span) * w;
  const y = pct => h - Math.min(100, pct / capacityPct * 100) / 100 * h;
  ctx.beginPath();
  ctx.moveTo(x(samples[0].unixSeconds), h);
  for (const sample of samples) ctx.lineTo(x(sample.unixSeconds), y(sample.workerCpuPercent));
  ctx.lineTo(x(samples[samples.length - 1].unixSeconds), h);
  ctx.closePath();
  ctx.fillStyle = 'rgba(255, 79, 31, .35)';
  ctx.fill();
  ctx.beginPath();
  for (let i = 0; i < samples.length; i++) {
    const px = x(samples[i].unixSeconds), py = y(samples[i].workerCpuPercent);
    if (i === 0) ctx.moveTo(px, py); else ctx.lineTo(px, py);
  }
  ctx.strokeStyle = '#FF4F1F';
  ctx.lineWidth = 2;
  ctx.stroke();
}

function render(s) {
  const phase = PHASES[s.phase] || PHASES.PARADO;
  $('phase-dot').className = 'dot ' + phase.dot;
  $('phase-short').textContent = s.phase.replaceAll('_', ' ').toLowerCase();
  $('phase-text').textContent = phase.text;
  $('phase-detail').textContent = s.phaseDetail || '';

  const bound = !!s.nodeId;
  const farming = s.acceptJobs;
  const toggle = $('farm-toggle');
  toggle.disabled = !bound;
  toggle.textContent = farming ? 'PAUSAR' : 'FARMAR';
  toggle.className = farming ? 'big primary-quiet' : 'big primary';

  $('kpi-active').textContent = s.activeJobs.length;
  $('kpi-cp').textContent = fmtCp(s.verifiedCpMilli);
  const hours = Math.max((Date.now() - Date.parse(s.sessionStartedAtUtc)) / 3600000, 1 / 60);
  $('kpi-cph').textContent = (s.verifiedCpMilli / 1000 / hours).toLocaleString('pt-BR', {maximumFractionDigits: 1});
  $('kpi-cpu').textContent = s.liveWorkerCpuPercent.toLocaleString('pt-BR', {maximumFractionDigits: 1}) + '%';
  $('kpi-cpu-total').textContent = ' total ' + (s.workerCpuMilliseconds / 1000).toLocaleString('pt-BR', {maximumFractionDigits: 1}) + 's';
  $('kpi-mem').textContent = fmtMb(s.liveWorkerMemoryBytes);
  $('kpi-mem-peak').textContent = s.peakWorkerMemoryBytes ? ' pico ' + fmtMb(s.peakWorkerMemoryBytes) : '';
  $('kpi-done').textContent = s.jobsCompleted + ' / ' + s.jobsFailed;

  const capacityPct = Math.max(1, s.maxLogicalProcessors * 100);
  const utilNow = Math.min(100, s.liveWorkerCpuPercent / capacityPct * 100);
  const delivering = s.activeJobs.length > 0 || utilNow >= 2;
  const perfStatus = $('perf-status');
  perfStatus.textContent = !bound
    ? 'SEM VÍNCULO'
    : delivering ? 'ENTREGANDO PODER COMPUTACIONAL' : (farming ? 'OCIOSA — AGUARDANDO TRABALHOS' : 'OCIOSA — FARM PAUSADO');
  perfStatus.className = delivering ? 'on' : '';
  $('perf-util').textContent = utilNow.toLocaleString('pt-BR', {maximumFractionDigits: 0}) + '% da capacidade autorizada';
  drawChart(s.utilization || [], capacityPct);

  const rewardRate = s.identity ? s.identity.rewardRatePerThousandCpMinor : 0;
  if (s.identity && s.identity.rewardPolicyEnabled && rewardRate > 0) {
    const sessionMinor = Math.floor(s.verifiedCpMilli * rewardRate / 1000000);
    $('kpi-earn').textContent = '≈ ' + (sessionMinor / 1000).toLocaleString('pt-BR', {minimumFractionDigits: 3, maximumFractionDigits: 3}) + ' AIC';
    $('kpi-earn-note').textContent = ' pela política atual (' + (rewardRate / 1000).toLocaleString('pt-BR') + ' AIC / 1.000 CP)';
  } else {
    $('kpi-earn').textContent = '—';
    $('kpi-earn-note').textContent = s.identity ? ' política de rewards desativada' : '';
  }
  $('kpi-lifetime').textContent = s.identity
    ? fmtCp(s.identity.lifetimeComputeUnitsMilli) + ' CP'
    : '—';

  const tbody = $('jobs').querySelector('tbody');
  tbody.innerHTML = '';
  for (const job of s.activeJobs) {
    const started = new Date(job.startedAtUtc);
    const elapsed = Math.max(0, (Date.now() - started.getTime()) / 1000);
    const row = document.createElement('tr');
    for (const cell of [
      job.workloadVersion,
      started.toLocaleTimeString('pt-BR'),
      elapsed.toLocaleString('pt-BR', {maximumFractionDigits: 0}) + 's',
      job.logicalProcessors + ' CPU',
      fmtMb(job.memoryBytes)
    ]) {
      const td = document.createElement('td');
      td.textContent = cell;
      row.appendChild(td);
    }
    tbody.appendChild(row);
  }
  $('jobs').hidden = s.activeJobs.length === 0;
  $('jobs-empty').hidden = s.activeJobs.length !== 0;

  const cpuAuthPct = Math.min(100, s.maxLogicalProcessors / s.hardwareLogicalProcessors * 100);
  const cpuLivePct = Math.min(100, s.liveWorkerCpuPercent / (s.maxLogicalProcessors * 100) * 100);
  const memAuthPct = Math.min(100, s.maxMemoryBytes / s.hardwareMemoryBytes * 100);
  const memLivePct = s.maxMemoryBytes > 0
    ? Math.min(100, s.liveWorkerMemoryBytes / s.maxMemoryBytes * 100) : 0;
  $('bar-cpu-auth').style.width = cpuAuthPct + '%';
  $('bar-cpu-auth-text').textContent = s.maxLogicalProcessors + ' de ' + s.hardwareLogicalProcessors + ' núcleos';
  $('bar-cpu-live').style.width = cpuLivePct + '%';
  $('bar-cpu-live-text').textContent = s.liveWorkerCpuPercent.toLocaleString('pt-BR', {maximumFractionDigits: 0}) + '% de ' + (s.maxLogicalProcessors * 100) + '%';
  $('bar-mem-auth').style.width = memAuthPct + '%';
  $('bar-mem-auth-text').textContent = fmtMb(s.maxMemoryBytes) + ' de ' + fmtMb(s.hardwareMemoryBytes);
  $('bar-mem-live').style.width = memLivePct + '%';
  $('bar-mem-live-text').textContent = fmtMb(s.liveWorkerMemoryBytes);

  const cph = s.verifiedCpMilli > 0 ? s.verifiedCpMilli / 1000 / hours : null;
  $('potential').textContent = !bound
    ? 'Vincule este computador para medir o que ele pode gerar.'
    : farming
      ? (cph
          ? 'Ritmo medido: ' + cph.toLocaleString('pt-BR', {maximumFractionDigits: 1}) + ' CP/hora com os limites atuais.'
          : 'Farmando — o ritmo aparece aqui depois do primeiro trabalho concluído.')
      : (cph
          ? 'Prévia: se estivesse farmando, este computador manteria ~' + cph.toLocaleString('pt-BR', {maximumFractionDigits: 1}) + ' CP/hora (ritmo medido nesta sessão).'
          : 'Prévia indisponível: aperte FARMAR e conclua o primeiro trabalho para calibrar o ritmo desta máquina.');

  const keyBox = $('confirm-key');
  keyBox.hidden = !(s.phase === 'AGUARDANDO_CONFIRMACAO' && s.pendingConfirmationKey);
  if (!keyBox.hidden) $('confirm-key-value').textContent = s.pendingConfirmationKey;

  const rebindNeeded = bound &&
    ['CREDENCIAL_RECUSADA', 'ERRO', 'RECONECTANDO'].includes(s.phase);
  $('identity-block').hidden = !bound || rebindNeeded;
  $('enroll-form').hidden = bound && !rebindNeeded;
  $('enroll-hint').textContent = rebindNeeded
    ? 'O vínculo atual não está funcionando (' + (s.baseUrl || 'servidor desconhecido') + '). Gere um código novo no site e refaça o vínculo aqui — o anterior será substituído.'
    : 'Este computador ainda não está vinculado. Gere um código na página Meu computador do site e cole aqui.';
  if (s.identity) {
    $('id-account').textContent = s.identity.accountDisplayName;
    $('id-node').textContent = s.identity.nodeName;
    $('id-share').textContent = s.identity.sharePlatformWork ? 'Sim — este computador aceita trabalhos da plataforma' : 'Não';
    $('id-status').textContent = s.identity.status + ' // ' + s.identity.verificationLevel;
    $('cred-issued').textContent = s.identity.credentialIssuedAtUtc
      ? new Date(s.identity.credentialIssuedAtUtc).toLocaleString('pt-BR') : '—';
  } else if (bound) {
    $('id-account').textContent = 'consultando…';
  }
  $('id-server').textContent = s.baseUrl || '—';
  $('cred-node').textContent = s.nodeId || '—';
  $('rotate').disabled = !bound;

  const setup = s.generationSetupPhase || 'OCIOSO';
  const generating = s.identity && s.identity.networkModel && setup === 'PRONTO';
  const busy = setup === 'INSTALANDO' || setup === 'BAIXANDO';
  const genStatus = {
    OCIOSO: 'Modo forte desligado — esta máquina ainda não roda a IA da rede.',
    INSTALANDO: 'Instalando o runtime de IA…',
    BAIXANDO: 'Baixando o modelo da rede…',
    PRONTO: 'Modo forte ativo — rodando a IA da rede ✓',
    ERRO: 'Não foi possível ativar o modo forte.'
  };
  $('gen-status').textContent = genStatus[setup] || genStatus.OCIOSO;
  $('gen-detail').hidden = !s.generationSetupDetail;
  $('gen-detail').textContent = s.generationSetupDetail || '';
  const genBtn = $('gen-enable');
  genBtn.disabled = !bound || busy || generating;
  genBtn.textContent = busy ? 'Ativando…' : generating ? 'Modo forte ativo' : 'Ativar o modo forte';

  $('cpu-max').textContent = s.hardwareLogicalProcessors;
  $('mem-max').textContent = Math.floor(s.hardwareMemoryBytes / 1048576).toLocaleString('pt-BR');
  if (!limitsDirty) {
    $('limit-cpu').max = s.hardwareLogicalProcessors;
    $('limit-cpu').value = s.maxLogicalProcessors;
    $('limit-mem').max = Math.floor(s.hardwareMemoryBytes / 1048576);
    $('limit-mem').value = Math.floor(s.maxMemoryBytes / 1048576);
    syncLimitLabels();
  }

  $('log').textContent = s.log.length
    ? s.log.slice().reverse().map(entry =>
        new Date(entry.atUtc).toLocaleTimeString('pt-BR') + ' // ' + entry.status + ' // ' + entry.message
      ).join('\n')
    : '—';
}

let lostContact = 0;
async function refresh() {
  try {
    const response = await fetch(base + 'api/state');
    if (!response.ok) return;
    lostContact = 0;
    render(await response.json());
  } catch (e) {
    // Duas falhas seguidas = o Agent desta janela foi encerrado (janela órfã).
    if (++lostContact < 2) return;
    $('phase-dot').className = 'dot err';
    $('phase-short').textContent = 'painel desconectado';
    $('phase-text').textContent = 'Esta janela ficou para trás: o Agent dela foi encerrado.';
    $('phase-detail').textContent = 'Feche esta janela e abra o programa de novo (duplo clique no executável).';
    $('farm-toggle').disabled = true;
    $('rotate').disabled = true;
    showError('O painel perdeu contato com o Agent. Feche esta janela e abra o programa novamente.');
  }
}

$('farm-toggle').addEventListener('click', () =>
  post('farm', {acceptJobs: $('farm-toggle').textContent === 'FARMAR'}));
$('limits-form').addEventListener('submit', event => {
  event.preventDefault();
  limitsDirty = false;
  post('limits', {
    logicalProcessors: parseInt($('limit-cpu').value, 10),
    memoryMegabytes: parseInt($('limit-mem').value, 10)
  });
});
$('enroll-form').addEventListener('submit', event => {
  event.preventDefault();
  post('enroll', {
    url: $('enroll-url').value.trim(),
    email: $('enroll-email').value.trim(),
    code: $('enroll-code').value.trim()
  });
});
$('rotate').addEventListener('click', () => {
  if (confirm('Rotacionar a credencial? A anterior deixa de funcionar na hora, inclusive em outros atalhos desta máquina.'))
    post('rotate');
});
$('gen-enable').addEventListener('click', () => {
  if (confirm('Ativar geração instala o runtime de IA e baixa o modelo (alguns GB). O Windows pode pedir confirmação. Continuar?'))
    post('generation/enable');
});
$('exit').addEventListener('click', () => {
  if (confirm('Sair encerra o Agent e este computador para de farmar. Confirmar?')) {
    post('exit');
    document.body.innerHTML = '<main><section class="win"><div class="win-title"><span>ATÉ LOGO</span></div><div class="win-body">O Agent foi encerrado. Pode fechar esta janela.</div></section></main>';
  }
});

refresh();
setInterval(refresh, 2000);
</script>
</body>
</html>
""";
}
