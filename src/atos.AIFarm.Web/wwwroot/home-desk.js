// Início modular (spec §6): módulos flutuantes arrastáveis, com minimizar/fechar,
// abertos por ícones de mesa. Estado (posição/aberto/minimizado) por conta em
// localStorage. Puro JS — o Blazor renderiza o conteúdo; aqui só posicionamos.
(function () {
  'use strict';
  const NS = 'atos:home:';

  function loadState(key) {
    try { return JSON.parse(localStorage.getItem(key) || '{}') || {}; }
    catch (e) { return {}; }
  }
  function saveState(key, state) {
    try { localStorage.setItem(key, JSON.stringify(state)); } catch (e) { /* modo privado */ }
  }

  function init(root) {
    if (!root || root.dataset.deskReady === '1') return;
    root.dataset.deskReady = '1';
    const account = root.dataset.account || 'anon';
    const key = NS + account;
    const state = loadState(key);
    const modules = Array.from(root.querySelectorAll('.home-module'));
    const persist = () => saveState(key, state);

    function apply(mod) {
      const id = mod.dataset.module;
      const s = state[id] || {};
      const open = s.open !== false; // aberto por padrão
      mod.hidden = !open;
      mod.classList.toggle('minimized', !!s.min);
      if (typeof s.x === 'number' && typeof s.y === 'number') {
        mod.style.left = s.x + 'px';
        mod.style.top = s.y + 'px';
      }
      // ícone de mesa reflete o estado
      const icon = root.querySelector('.desk-icon[data-open="' + id + '"]');
      if (icon) icon.classList.toggle('active', open);
    }

    // ---- posição inicial em grade escalonada, se ainda não houver estado ----
    const area = root.querySelector('.home-modules');
    modules.forEach((mod, i) => {
      const id = mod.dataset.module;
      if (!state[id]) state[id] = {};
      const s = state[id];
      if (typeof s.x !== 'number') {
        const col = i % 3, rowIdx = Math.floor(i / 3);
        s.x = 16 + col * 320;
        s.y = 16 + rowIdx * 250;
      }
      apply(mod);
    });
    persist();

    // ---- arrastar pela barra de título ----
    let drag = null;
    function onDown(e, mod) {
      if (e.button !== 0) return;
      const id = mod.dataset.module;
      const rect = mod.getBoundingClientRect();
      const areaRect = area.getBoundingClientRect();
      drag = { mod, id, dx: e.clientX - rect.left, dy: e.clientY - rect.top, areaRect };
      mod.classList.add('dragging');
      document.body.classList.add('desk-dragging');
      e.preventDefault();
    }
    function onMove(e) {
      if (!drag) return;
      let x = e.clientX - drag.areaRect.left - drag.dx;
      let y = e.clientY - drag.areaRect.top - drag.dy;
      x = Math.max(0, Math.min(x, drag.areaRect.width - 60));
      y = Math.max(0, y);
      drag.mod.style.left = x + 'px';
      drag.mod.style.top = y + 'px';
      state[drag.id].x = Math.round(x);
      state[drag.id].y = Math.round(y);
    }
    function onUp() {
      if (!drag) return;
      drag.mod.classList.remove('dragging');
      document.body.classList.remove('desk-dragging');
      drag = null;
      persist();
    }

    modules.forEach(mod => {
      const bar = mod.querySelector('.home-module-bar');
      if (bar) bar.addEventListener('mousedown', e => {
        if (e.target.closest('button')) return; // botões da barra não arrastam
        onDown(e, mod);
      });
      const min = mod.querySelector('[data-act="min"]');
      if (min) min.addEventListener('click', () => {
        const s = state[mod.dataset.module];
        s.min = !s.min;
        mod.classList.toggle('minimized', !!s.min);
        persist();
      });
      const close = mod.querySelector('[data-act="close"]');
      if (close) close.addEventListener('click', () => {
        state[mod.dataset.module].open = false;
        apply(mod);
        persist();
      });
    });
    document.addEventListener('mousemove', onMove);
    document.addEventListener('mouseup', onUp);

    // ---- ícones de mesa abrem/trazem à frente ----
    root.querySelectorAll('.desk-icon[data-open]').forEach(icon => {
      icon.addEventListener('click', e => {
        e.preventDefault();
        const id = icon.dataset.open;
        const mod = root.querySelector('.home-module[data-module="' + id + '"]');
        if (!mod) return;
        const s = state[id] || (state[id] = {});
        s.open = s.open === false ? true : (mod.hidden ? true : true);
        s.min = false;
        apply(mod);
        // traz à frente
        modules.forEach(m => (m.style.zIndex = ''));
        mod.style.zIndex = '30';
        persist();
      });
    });
  }

  window.atosDesk = {
    init() {
      document.querySelectorAll('.home-modular').forEach(init);
    }
  };
  // primeira carga (SSR) e após navegação do Blazor
  document.addEventListener('DOMContentLoaded', () => window.atosDesk.init());
})();
