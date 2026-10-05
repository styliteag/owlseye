// UI smoke test: starts the real exe in demo mode with WebView2 remote debugging and clicks through the core flow
// (matrix, keyboard, panel, preview, apply, log, undo, other pages). Needs Node 22+ (built-in WebSocket).
//   node tests/e2e/smoke.mjs publish/owlseye.exe
import { execFileSync, spawn } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';

const exe = resolve(process.argv[2] || 'publish/owlseye.exe');
const port = 9300 + Math.floor(Math.random() * 500);
const data = mkdtempSync(join(tmpdir(), 'owlseye-e2e-'));
const app = spawn(exe, ['--demo'], {
  env: { ...process.env, LOCALAPPDATA: data, WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: `--remote-debugging-port=${port} --enable-logging --v=0` },
  stdio: 'ignore',
});

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
let ws;
let id = 0;
const pending = new Map();

async function connect() {
  let seen = 'nothing answered on the debugging port';
  const end = Date.now() + 120000; // the first start of a new user (as in CI) takes a while
  while (Date.now() < end) {
    try {
      const list = await (await fetch(`http://127.0.0.1:${port}/json`)).json();
      seen = `the debugging port lists ${list.map((t) => `${t.type} ${t.url}`).join(', ') || 'no targets'}`;
      const page = list.find((t) => t.type === 'page' && t.url.startsWith('https://0.0.0.'));
      if (page) {
        ws = new WebSocket(page.webSocketDebuggerUrl);
        await new Promise((r, j) => { ws.onopen = r; ws.onerror = j; });
        ws.onmessage = (m) => {
          const msg = JSON.parse(m.data);
          if (pending.has(msg.id)) { pending.get(msg.id)(msg); pending.delete(msg.id); }
        };
        return;
      }
    } catch { /* not up yet */ }
    await sleep(200);
  }
  throw new Error(`the app window did not come up within 120 s (${seen}; port ${port})`);
}

const send = (method, params = {}) => new Promise((r) => { const i = ++id; pending.set(i, r); ws.send(JSON.stringify({ id: i, method, params })); });

async function js(expr) {
  const r = await send('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true });
  if (r.result?.exceptionDetails) throw new Error(`${expr}\n  -> ${r.result.exceptionDetails.exception?.description || r.result.exceptionDetails.text}`);
  return r.result?.result?.value;
}

async function until(expr, what, ms = 15000) {
  const end = Date.now() + ms;
  while (Date.now() < end) {
    try {
      const v = await js(expr);
      if (v) return v;
    } catch { /* page in between */ }
    await sleep(150);
  }
  throw new Error(`timeout: ${what}`);
}

async function key(selector, k) {
  await js(`document.querySelector(${JSON.stringify(selector)}).focus()`);
  await send('Input.dispatchKeyEvent', { type: 'keyDown', key: k, text: k, windowsVirtualKeyCode: k.toUpperCase().charCodeAt(0) });
  await send('Input.dispatchKeyEvent', { type: 'keyUp', key: k, windowsVirtualKeyCode: k.toUpperCase().charCodeAt(0) });
}

const click = (selector) => js(`(() => { const e = document.querySelector(${JSON.stringify(selector)}); if (!e) throw new Error('missing ${selector.replace(/'/g, '')}'); e.click(); return true; })()`);
const clickText = (selector, text) => js(`(() => { const e = [...document.querySelectorAll(${JSON.stringify(selector)})].find(x => x.innerText.trim().startsWith(${JSON.stringify(text)})); if (!e) throw new Error('missing ${text}'); e.click(); return true; })()`);

const steps = [];
async function step(name, fn) {
  await fn();
  steps.push(name);
  console.log(`ok  ${name}`);
}

let failed = false;
try {
  await connect();
  await step('matrix shows the demo share', () => until(`document.querySelectorAll('#grid tbody tr').length > 10`, 'matrix rows'));
  await step('click on a cell opens its panel', async () => {
    await click(`#grid button.c[data-path='HR']`);
    await until(`document.querySelector('#panel h3')`, 'cell panel');
  });
  await step('keyboard sets a right and the parent folders get R| automatically', async () => {
    await key(`#grid button.c[data-path='Public\\\\Transfer'][data-sid='S-1-5-21-1-2-3-2011']`, 'w');
    await until(`document.querySelector('.pendingbar')`, 'pending bar');
  });
  await step('preview lists the change and the effect on users', async () => {
    await click('.pendingbar a.button');
    await until(`document.querySelector('h1')?.innerText === 'Preview' && document.querySelector('main table')`, 'preview');
  });
  await step('apply writes and comes back with a message', async () => {
    await click('.applyform button.primary');
    await until(`location.pathname === '/matrix' && document.querySelector('main .flash')?.innerText.includes('changes applied')`, 'apply message');
  });
  await step('log shows the change; undo leads to a preview', async () => {
    await click(`a[href='/audit']`);
    await until(`location.pathname === '/audit' && document.querySelector('h1')?.innerText === 'Log' && document.querySelector('main table')?.innerText.includes('G-HR')`, 'log entry');
    await clickText('main table button', 'Undo');
    await until(`location.pathname === '/preview' && document.querySelector('main table')`, 'undo preview');
  });
  for (const [href, check] of [
    ['/users', `document.querySelectorAll('.userlist a').length > 5`],
    ['/findings', `document.querySelector('main table')?.innerText.includes('Unresolved SID')`],
    ['/drift', `document.querySelector('h1')?.innerText.includes('outside owlseye')`],
    ['/share', `document.querySelector('main')?.innerText.includes('the share is fixed')`],
    ['/report', `document.querySelector('.report table.rm') && document.querySelector('.report')?.innerText.includes('Changes in the last')`],
  ]) {
    await step(`page ${href}`, async () => {
      await click(`a[href='${href}']`);
      await until(check, href);
    });
  }
  await step('folder page from a finding', async () => {
    await click(`a[href='/findings']`);
    await until(`location.pathname === '/findings' && document.querySelector('main table a')`, 'findings links');
    await click('main table a');
    await until(`location.pathname === '/folder' && document.querySelector('main h1')`, 'folder page');
  });
  await step('the matrix shows the hidden accounts on request', async () => {
    await click(`nav a[href='/matrix']`);
    await until(`location.pathname === '/matrix' && !!document.querySelector('.toolbar label.showhidden input')`, 'matrix');
    await click('.toolbar label.showhidden input');
    await until(`[...document.querySelectorAll('#grid th.gcol.hiddenacct')].some(th => th.innerText.trim() === 'SYSTEM')`, 'SYSTEM column');
    await click('.toolbar label.showhidden input');
    await until(`!!document.querySelector('#grid th.gcol') && !document.querySelector('#grid th.gcol.hiddenacct')`, 'hidden columns gone');
  });
  await step('no error boundary was hit', async () => {
    const err = await js(`document.body.innerText.includes('Something went wrong')`);
    if (err) throw new Error('error boundary shown');
  });
} catch (e) {
  failed = true;
  console.error(`FAIL after ${steps.length} steps: ${e.message}`);
  try { // what the app logged (e.g. WebView2 errors), so that a failed CI run says why
    console.error(`--- error.log of the app:\n${readFileSync(join(data, 'owlseye', 'error.log'), 'utf8').slice(-4000)}`);
  } catch { console.error('--- the app wrote no error.log'); }
  try { // which processes and windows are there (a message box shows up as a window title)
    console.error(`--- processes:\n${execFileSync('tasklist', ['/v', '/fo', 'list', '/fi', 'imagename eq owlseye.exe'], { encoding: 'utf8' })}`);
    console.error(execFileSync('tasklist', ['/fi', 'imagename eq msedgewebview2.exe'], { encoding: 'utf8' }));
  } catch (t) { console.error(`--- tasklist failed: ${t.message}`); }
  try { // did WebView2 get the debugging flag, does it listen, does a machine policy interfere
    console.error(execFileSync('powershell', ['-NoProfile', '-Command',
      "$w = @(Get-CimInstance Win32_Process -Filter \"Name='msedgewebview2.exe'\"); "
      + "$w | Where-Object { $_.CommandLine -notmatch '--type=' } | ForEach-Object { '--- browser process: ' + $_.CommandLine }; "
      + "$ids = $w.ProcessId; Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | "
      + "Where-Object { $ids -contains $_.OwningProcess } | ForEach-Object { '--- listening: ' + $_.LocalAddress + ':' + $_.LocalPort }; "
      + "'--- Edge/WebView2 policies (HKLM):'; reg query HKLM\\SOFTWARE\\Policies\\Microsoft\\Edge /s 2>$null | Select-Object -First 40; exit 0"],
    { encoding: 'utf8', timeout: 30000 }));
  } catch (d) { console.error(`--- WebView2 diagnostics failed: ${d.message}`); }
  const udf = join(data, 'owlseye', 'WebView2', 'EBWebView');
  try { // Chromium writes this file once its DevTools server listens
    console.error(`--- DevToolsActivePort: ${readFileSync(join(udf, 'DevToolsActivePort'), 'utf8').replace(/\s+/g, ' ')}`);
  } catch { console.error('--- no DevToolsActivePort: the DevTools server did not start'); }
  try {
    const lines = readFileSync(join(udf, 'chrome_debug.log'), 'utf8').split(/\r?\n/);
    const hits = lines.filter((l) => /devtools|remote.debug|bind|listen|socket|http server/i.test(l));
    console.error(`--- chrome_debug.log (${lines.length} lines), matching:\n${hits.slice(-30).join('\n') || '(none)'}`);
    const noise = /oneauth_config|edge_auth/;
    console.error(`--- chrome_debug.log, all but sign-in noise:\n${lines.filter((l) => !noise.test(l)).slice(0, 120).join('\n')}`);
  } catch { console.error('--- no chrome_debug.log'); }
  try {
    const net = execFileSync('netstat', ['-ano', '-p', 'tcp'], { encoding: 'utf8' }).split(/\r?\n/);
    console.error(`--- netstat for port ${port}:\n${net.filter((l) => l.includes(`:${port} `)).join('\n') || '(nothing)'}`);
  } catch (n) { console.error(`--- netstat failed: ${n.message}`); }
  if (process.env.SMOKE_ARTIFACTS) { // CI: a screenshot of the desktop, uploaded as an artifact
    try {
      mkdirSync(process.env.SMOKE_ARTIFACTS, { recursive: true });
      const png = join(process.env.SMOKE_ARTIFACTS, 'desktop.png');
      execFileSync('powershell', ['-NoProfile', '-Command',
        'Add-Type -AssemblyName System.Windows.Forms, System.Drawing; $b = [System.Windows.Forms.SystemInformation]::VirtualScreen; '
        + '$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height; $g = [System.Drawing.Graphics]::FromImage($bmp); '
        + `$g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size); $bmp.Save('${png.replace(/'/g, "''")}')`], { timeout: 30000 });
      console.error(`--- screenshot: ${png}`);
    } catch (s) { console.error(`--- screenshot failed: ${s.message}`); }
  }
} finally {
  try { ws?.close(); } catch { /* closed */ }
  app.kill();
  await sleep(1500);
  try { rmSync(data, { recursive: true, force: true }); } catch { /* WebView2 may still hold files */ }
}
console.log(failed ? 'SMOKE TEST FAILED' : `SMOKE TEST PASSED (${steps.length} steps)`);
process.exit(failed ? 1 : 0);
