// Screenshots for the documentation: starts the exe with the demo data in sim mode (so an outside change can be
// simulated for the desired-state page), drives it through WebView2 and saves PNGs. Needs Node 22+.
//   node tools/screenshots.mjs publish/owlseye.exe docs/screenshots
import { spawn } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';

const exe = resolve(process.argv[2] || 'publish/owlseye.exe');
const out = resolve(process.argv[3] || 'docs/screenshots');
mkdirSync(out, { recursive: true });
const port = 9300 + Math.floor(Math.random() * 500);
const data = mkdtempSync(join(tmpdir(), 'owlseye-shots-'));
const sim = join(data, 'sim');
const app = spawn(exe, ['--sim', sim], {
  env: { ...process.env, LOCALAPPDATA: data, WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: `--remote-debugging-port=${port}` },
  stdio: 'ignore',
});

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
let ws;
let id = 0;
const pending = new Map();
const send = (method, params = {}) => new Promise((r) => { const i = ++id; pending.set(i, r); ws.send(JSON.stringify({ id: i, method, params })); });

async function connect() {
  for (let i = 0; i < 150; i++) {
    try {
      const page = (await (await fetch(`http://127.0.0.1:${port}/json`)).json()).find((t) => t.type === 'page' && t.url.startsWith('https://0.0.0.'));
      if (page) {
        ws = new WebSocket(page.webSocketDebuggerUrl);
        await new Promise((r, j) => { ws.onopen = r; ws.onerror = j; });
        ws.onmessage = (m) => { const msg = JSON.parse(m.data); if (pending.has(msg.id)) { pending.get(msg.id)(msg); pending.delete(msg.id); } };
        return;
      }
    } catch { /* not up yet */ }
    await sleep(200);
  }
  throw new Error('the app window did not come up');
}

async function js(expr) {
  const r = await send('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true });
  if (r.result?.exceptionDetails) throw new Error(`${expr}\n  -> ${r.result.exceptionDetails.exception?.description || r.result.exceptionDetails.text}`);
  return r.result?.result?.value;
}

async function until(expr, what, ms = 15000) {
  const end = Date.now() + ms;
  while (Date.now() < end) {
    try { if (await js(expr)) return; } catch { /* page in between */ }
    await sleep(150);
  }
  throw new Error(`timeout: ${what}`);
}

const click = (sel) => js(`(() => { const e = document.querySelector(${JSON.stringify(sel)}); if (!e) throw new Error('missing'); e.click(); return 1; })()`);
const clickText = (sel, text) => js(`(() => { const e = [...document.querySelectorAll(${JSON.stringify(sel)})].find(x => x.innerText.trim().startsWith(${JSON.stringify(text)})); if (!e) throw new Error('missing ${text}'); e.click(); return 1; })()`);
const go = async (href, check) => { await click(`a[href='${href}']`); await until(`location.pathname === '${href.split('?')[0]}' && (${check})`, href); await sleep(300); };

async function key(sel, k) {
  await js(`document.querySelector(${JSON.stringify(sel)}).focus()`);
  await send('Input.dispatchKeyEvent', { type: 'keyDown', key: k, text: k, windowsVirtualKeyCode: k.toUpperCase().charCodeAt(0) });
  await send('Input.dispatchKeyEvent', { type: 'keyUp', key: k, windowsVirtualKeyCode: k.toUpperCase().charCodeAt(0) });
}

async function shot(name, scheme = 'light') {
  await send('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-color-scheme', value: scheme }] });
  await js(`document.activeElement?.blur?.(), 1`);
  await sleep(250);
  const r = await send('Page.captureScreenshot', { format: 'png' });
  writeFileSync(join(out, name), Buffer.from(r.result.data, 'base64'));
  console.log('saved', name);
}

const G = (n) => `S-1-5-21-1-2-3-${2000 + n}`; // demo group SIDs in the order of Demo.Groups
let failed = false;
try {
  await connect();
  await send('Emulation.setDeviceMetricsOverride', { width: 1440, height: 900, deviceScaleFactor: 1, mobile: false });
  await until(`document.querySelectorAll('#grid tbody tr').length > 10`, 'matrix');
  await go('/findings', `document.querySelector('main table')`); // away and back: the start notice goes
  await go('/matrix', `document.querySelectorAll('#grid tbody tr').length > 10 && !document.querySelector('main .flash')`);

  // 1 matrix with the panel of an inherited right
  await click(`#grid button.c[data-path='Operations\\\\Service-Staff'][data-sid='${G(1)}']`);
  await until(`document.querySelector('#panel h3')?.innerText === 'G-Management'`, 'cell panel');
  await shot('matrix.png');

  // 2 a right set by keyboard (G-HR: W on Sales-Staff): pending, with the automatic R| on the parent folder
  await key(`#grid button.c[data-path='Operations\\\\Sales-Staff'][data-sid='${G(11)}']`, 'w');
  await until(`document.querySelector('.pendingbar')`, 'pending');
  await sleep(300);
  await shot('matrix-pending.png');

  // 3 folder panel: inheritance, new subfolder, groups with access
  await clickText('#grid th.folder .fname', 'Service-Staff');
  await until(`document.querySelector('#panel h3')?.innerText === 'Service-Staff'`, 'folder panel');
  await shot('folder-panel.png');

  // 4 preview
  await click('.pendingbar a.button');
  await until(`document.querySelector('h1')?.innerText === 'Preview' && document.querySelector('main table')`, 'preview');
  await js(`(() => { const i = document.querySelector('.applyform input'); i.value = 'Ticket 4711: HR needs the sales team folder'; i.dispatchEvent(new Event('change', { bubbles: true })); return 1; })()`);
  await shot('preview.png');

  // 5 apply, then the log
  await click('.applyform button.primary');
  await until(`location.pathname === '/matrix' && document.querySelector('main .flash')`, 'applied');
  await go('/audit', `document.querySelector('h1')?.innerText === 'Log' && document.querySelector('main table')`);
  await shot('log.png');

  // 6 desired state: two changes made outside owlseye (as in Explorer), then Rescan
  const p = join(sim, 'state.json');
  const s = JSON.parse(readFileSync(p, 'utf8'));
  s.acls['Public'].aces.push([0, 3, 1180095, G(9)]);
  s.acls['Programs\\CRM'].protected = false;
  writeFileSync(p, JSON.stringify(s, null, 1));
  await clickText('.who button', 'Rescan');
  await until(`document.querySelector('main .flash')?.innerText === 'Rescanned.'`, 'rescan');
  await go('/drift', `document.querySelector('main table')`);
  await shot('drift.png');

  // 7 users: effective rights of one user and through which groups
  await go('/users', `document.querySelectorAll('.userlist a').length > 5`);
  await clickText('.userlist a', 'Emma Evans');
  await until(`document.querySelector('main h1')?.innerText.startsWith('Emma Evans')`, 'user');
  await shot('users.png');

  // 7b groups: one group with its rights, members and nesting; the membership matrix
  await go('/groups', `document.querySelectorAll('.userlist a').length > 5`);
  await clickText('.userlist a', 'G-Operations');
  await until(`document.querySelector('main h1')?.innerText.startsWith('G-Operations')`, 'group');
  await shot('groups.png');
  await go('/groups?view=matrix', `document.querySelector('table.mship td.m')`);
  await shot('groups-matrix.png');

  // 8 findings and 9 a folder page
  await go('/findings', `document.querySelector('main table')`);
  await shot('findings.png');
  await clickText('main table a', 'Public\\Transfer');
  await until(`location.pathname === '/folder' && document.querySelector('main h1')`, 'folder page');
  await shot('folder.png');

  // 10 access rights report (top of the page, then the print layout as in the PDF)
  await go('/report', `document.querySelector('.report table.rm')`);
  await shot('report.png');
  await send('Emulation.setEmulatedMedia', { media: 'print', features: [{ name: 'prefers-color-scheme', value: 'light' }] });
  await sleep(300);
  const pr = await send('Page.captureScreenshot', { format: 'png' });
  writeFileSync(join(out, 'report-print.png'), Buffer.from(pr.result.data, 'base64'));
  console.log('saved report-print.png');
  await send('Emulation.setEmulatedMedia', { media: '' });

  // 11 dark mode
  await go('/matrix', `document.querySelector('#grid')`);
  await shot('matrix-dark.png', 'dark');
} catch (e) {
  failed = true;
  console.error('FAILED: ' + e.message);
} finally {
  try { ws?.close(); } catch { /* closed */ }
  app.kill();
  await sleep(1500);
  try { rmSync(data, { recursive: true, force: true }); } catch { /* WebView2 may still hold files */ }
}
process.exit(failed ? 1 : 0);
