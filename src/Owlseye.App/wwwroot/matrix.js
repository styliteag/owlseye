// Matrix: keyboard input like a spreadsheet.
// Arrows move, R = Read, W/M = Write, L or | = R| (list this folder only), Shift+W = W|,
// Del/Backspace/0/- = no entry, Enter/Space = details (the button's own click).
window.owlseye = (function () {
  'use strict';

  const RIGHT_KEYS = { r: 'R', w: 'W', m: 'W', f: 'F', l: 'R|', '|': 'R|', W: 'W|', Delete: '', Backspace: '', '0': '', '-': '' };
  let dotnet = null;
  let focusKey = null;

  function cells() {
    return Array.from(document.querySelectorAll('#grid tbody tr:not(.hidden)')).map((tr) =>
      Array.from(tr.querySelectorAll('button.c')),
    );
  }

  function keyOf(btn) {
    return btn.dataset.sid + '\n' + btn.dataset.path;
  }

  function move(btn, dr, dc) {
    const grid = cells();
    const r = grid.findIndex((row) => row.includes(btn));
    if (r < 0) return;
    const c = grid[r].indexOf(btn);
    const nr = Math.max(0, Math.min(grid.length - 1, r + dr));
    const target = grid[nr][Math.max(0, Math.min(grid[nr].length - 1, c + dc))];
    if (target) { target.focus(); focusKey = keyOf(target); }
  }

  const busy = () => !!document.querySelector('.busy');

  document.addEventListener('keydown', (ev) => {
    const btn = ev.target.closest && ev.target.closest('#grid button.c');
    if (!btn || ev.ctrlKey || ev.metaKey || ev.altKey) return;
    if (busy()) { ev.preventDefault(); return; } // a scan or apply is running (overlay)
    const arrows = { ArrowUp: [-1, 0], ArrowDown: [1, 0], ArrowLeft: [0, -1], ArrowRight: [0, 1] };
    if (arrows[ev.key]) {
      ev.preventDefault();
      move(btn, ...arrows[ev.key]);
      return;
    }
    const key = ev.key === 'W' || ev.key.length !== 1 ? ev.key : ev.key.toLowerCase();
    if (key in RIGHT_KEYS && dotnet) {
      ev.preventDefault();
      focusKey = keyOf(btn);
      dotnet.invokeMethodAsync('SetRight', btn.dataset.sid, btn.dataset.path, RIGHT_KEYS[key]);
    }
  });

  // one listener for all cells instead of a Blazor handler per button (thousands of them on a large share)
  document.addEventListener('click', (ev) => {
    const btn = ev.target.closest && ev.target.closest('#grid button.c');
    if (!btn || !dotnet) return;
    focusKey = keyOf(btn);
    dotnet.invokeMethodAsync('OpenCell', btn.dataset.sid, btn.dataset.path);
  });

  document.addEventListener('focusin', (ev) => {
    const btn = ev.target.closest && ev.target.closest('#grid button.c');
    if (btn) focusKey = keyOf(btn);
  });

  return {
    init(ref) { dotnet = ref; },
    dispose() { dotnet = null; },
    // after a re-render: keep the keyboard focus on the same cell (sid + path)
    refocus() {
      if (!focusKey) return;
      const active = document.activeElement;
      if (active && active.matches && active.matches('#grid button.c') && keyOf(active) === focusKey) return;
      // after a click in the panel (a right, break/restore inheritance) the keys go on working on the cell, as in the
      // web version; text fields (filter, new folder, group search) keep their focus
      const inPanelButton = active && active.closest && active.closest('#panel') && active.tagName === 'BUTTON';
      if (active && active !== document.body && !inPanelButton && !(active.matches && active.matches('#grid button.c'))) return;
      const target = Array.from(document.querySelectorAll('#grid button.c')).find((b) => keyOf(b) === focusKey);
      if (target) target.focus({ preventScroll: true });
    },
    focusInput(id) { const el = document.getElementById(id); if (el) el.focus(); },
  };
})();
