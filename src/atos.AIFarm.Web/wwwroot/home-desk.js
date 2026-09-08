// Início modular — grid de janelas com abrir/fechar/minimizar e arrasto persistente por conta.
// Chamado pelo Home.razor via JS interop: atosDesk.init() (a cada render; re-aplica o layout salvo).
(function () {
  const KEY = 'aifarm.inicio-modular.layout.v1';
  const FIXED = ['creditos', 'extrato', 'maquinas'];

  function load(account) {
    try {
      const s = JSON.parse(localStorage.getItem(KEY + ':' + account) || 'null');
      if (Array.isArray(s)) return s.filter(function (m) { return m && m.type; });
    } catch (e) { /* layout salvo inválido */ }
    return null;
  }
  function save(account, mods) {
    try {
      localStorage.setItem(KEY + ':' + account, JSON.stringify(mods.map(function (m) { return { type: m.type, open: m.open !== false }; })));
    } catch (e) { /* armazenamento indisponível */ }
  }

  window.atosDesk = {
    init: function () {
      const root = document.querySelector('.home-modular');
      if (!root) return;
      if (root.__deskApply) { root.__deskApply(); return; }

      const account = root.getAttribute('data-account') || 'conta';
      const grid = root.querySelector('[data-mod-grid]');
      if (!grid) return;

      let mods = load(account);
      if (!mods) mods = [{ type: 'creditos', open: true }, { type: 'extrato', open: true }, { type: 'maquinas', open: true }];
      FIXED.forEach(function (t) { if (!mods.some(function (m) { return m.type === t; })) mods.unshift({ type: t, open: true }); });
      mods = mods.filter(function (m) { return grid.querySelector('[data-mod="' + m.type + '"]'); });

      const apply = function () {
        const byType = {};
        Array.prototype.forEach.call(grid.querySelectorAll('[data-mod]'), function (c) { byType[c.getAttribute('data-mod')] = c; });
        mods.forEach(function (m) {
          const el = byType[m.type];
          if (!el) return;
          el.hidden = false;
          el.classList.toggle('mod-min', m.open === false);
          grid.appendChild(el);
          delete byType[m.type];
        });
        Object.keys(byType).forEach(function (t) { byType[t].hidden = true; });
        save(account, mods);
      };
      root.__deskApply = apply;

      root.querySelectorAll('[data-open]').forEach(function (btn) {
        btn.addEventListener('click', function () {
          const t = btn.getAttribute('data-open');
          const cur = mods.find(function (m) { return m.type === t; });
          if (cur) cur.open = true; else mods.push({ type: t, open: true });
          apply();
        });
      });

      grid.addEventListener('click', function (e) {
        const b = e.target.closest('[data-act]');
        if (!b) return;
        const card = b.closest('[data-mod]');
        const t = card.getAttribute('data-mod');
        if (b.getAttribute('data-act') === 'close') {
          mods = mods.filter(function (m) { return m.type !== t; });
        } else {
          const m = mods.find(function (x) { return x.type === t; });
          if (m) m.open = m.open === false;
        }
        apply();
      });

      grid.addEventListener('pointerdown', function (e) {
        const bar = e.target.closest('.mod-title');
        if (!bar || e.target.closest('button')) return;
        const card = bar.closest('[data-mod]');
        const type = card.getAttribute('data-mod');
        if (FIXED.indexOf(type) >= 0) return;
        e.preventDefault();
        let sx = e.clientX, sy = e.clientY;
        card.classList.add('mod-dragging');
        bar.style.cursor = 'grabbing';
        const move = function (ev) {
          card.style.transform = 'translate(' + (ev.clientX - sx) + 'px,' + (ev.clientY - sy) + 'px)';
          const under = document.elementFromPoint(ev.clientX, ev.clientY);
          const target = under && under.closest('[data-mod]');
          if (!target || target === card || target.hidden || FIXED.indexOf(target.getAttribute('data-mod')) >= 0) return;
          const from = mods.findIndex(function (m) { return m.type === type; });
          const to = mods.findIndex(function (m) { return m.type === target.getAttribute('data-mod'); });
          if (from < 0 || to < 0) return;
          const before = card.getBoundingClientRect();
          const item = mods.splice(from, 1)[0];
          mods.splice(to, 0, item);
          apply();
          // compensa o salto do slot para o card continuar sob o cursor
          const after = card.getBoundingClientRect();
          sx += after.left - before.left;
          sy += after.top - before.top;
          card.style.transform = 'translate(' + (ev.clientX - sx) + 'px,' + (ev.clientY - sy) + 'px)';
        };
        const up = function () {
          card.classList.remove('mod-dragging');
          card.style.transform = '';
          bar.style.cursor = 'grab';
          save(account, mods);
          window.removeEventListener('pointermove', move);
          window.removeEventListener('pointerup', up);
        };
        window.addEventListener('pointermove', move);
        window.addEventListener('pointerup', up);
      });

      grid.querySelectorAll('[data-mod] .mod-title').forEach(function (bar) {
        const t = bar.closest('[data-mod]').getAttribute('data-mod');
        if (FIXED.indexOf(t) < 0) bar.style.cursor = 'grab';
      });

      apply();
    }
  };
})();
