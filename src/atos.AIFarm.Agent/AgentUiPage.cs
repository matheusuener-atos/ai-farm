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
button.primary { background:var(--orange); color:#FFFFFF; font-size:16px; padding:14px 34px; letter-spacing:.02em; }
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
  <span>AI Farm — Compute Agent</span>
  <span class="right"><span id="phase-dot" class="dot"></span><span id="phase-short">carregando…</span></span>
</header>
<main>
  <section class="win">
    <div class="win-title"><span>MEU COMPUTADOR</span></div>
    <div class="win-body">
      <div id="error-banner" class="banner err" hidden></div>
      <div class="farm-row">
        <button id="farm-toggle" class="primary" disabled>FARMAR</button>
        <div class="phase"><span id="phase-text">Conectando ao painel…</span><small id="phase-detail"></small></div>
      </div>
      <p class="note">Farmar deixa este computador à disposição da rede: ele executa trabalhos verificados,
      acumula CP e a rede nunca acessa seus arquivos — só os workloads do catálogo autorizado.</p>
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
          <p>Este computador ainda não está vinculado. Gere um código na página
          <strong>Meu computador</strong> do site e cole aqui.</p>
          <label>URL da AI Farm<input id="enroll-url" placeholder="https://ai-farm-z5yp.onrender.com" required></label>
          <label>Código de vínculo<input id="enroll-code" autocomplete="off" required></label>
          <button type="submit">Vincular este computador</button>
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
    <div class="win-title"><span>LIMITES DO MEU COMPUTADOR</span></div>
    <div class="win-body">
      <form id="limits-form" class="stack">
        <label><span>Processadores lógicos para a rede (máx. <span id="cpu-max">?</span>)</span>
          <input id="limit-cpu" type="number" min="1" step="1" required></label>
        <label><span>Memória para a rede, em MB (máx. <span id="mem-max">?</span>)</span>
          <input id="limit-mem" type="number" min="64" step="64" required></label>
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
['limit-cpu', 'limit-mem'].forEach(id => $(id).addEventListener('input', () => { limitsDirty = true; }));

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
  toggle.className = farming ? '' : 'primary';

  $('kpi-active').textContent = s.activeJobs.length;
  $('kpi-cp').textContent = fmtCp(s.verifiedCpMilli);
  const hours = Math.max((Date.now() - Date.parse(s.sessionStartedAtUtc)) / 3600000, 1 / 60);
  $('kpi-cph').textContent = (s.verifiedCpMilli / 1000 / hours).toLocaleString('pt-BR', {maximumFractionDigits: 1});
  $('kpi-cpu').textContent = s.liveWorkerCpuPercent.toLocaleString('pt-BR', {maximumFractionDigits: 1}) + '%';
  $('kpi-cpu-total').textContent = ' total ' + (s.workerCpuMilliseconds / 1000).toLocaleString('pt-BR', {maximumFractionDigits: 1}) + 's';
  $('kpi-mem').textContent = fmtMb(s.liveWorkerMemoryBytes);
  $('kpi-mem-peak').textContent = s.peakWorkerMemoryBytes ? ' pico ' + fmtMb(s.peakWorkerMemoryBytes) : '';
  $('kpi-done').textContent = s.jobsCompleted + ' / ' + s.jobsFailed;

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

  $('identity-block').hidden = !bound;
  $('enroll-form').hidden = bound;
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

  $('cpu-max').textContent = s.hardwareLogicalProcessors;
  $('mem-max').textContent = Math.floor(s.hardwareMemoryBytes / 1048576).toLocaleString('pt-BR');
  if (!limitsDirty) {
    $('limit-cpu').value = s.maxLogicalProcessors;
    $('limit-cpu').max = s.hardwareLogicalProcessors;
    $('limit-mem').value = Math.floor(s.maxMemoryBytes / 1048576);
    $('limit-mem').max = Math.floor(s.hardwareMemoryBytes / 1048576);
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
  post('enroll', {url: $('enroll-url').value.trim(), code: $('enroll-code').value.trim()});
});
$('rotate').addEventListener('click', () => {
  if (confirm('Rotacionar a credencial? A anterior deixa de funcionar na hora, inclusive em outros atalhos desta máquina.'))
    post('rotate');
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
