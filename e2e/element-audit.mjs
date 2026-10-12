// Element-level fidelity audit: computed styles of key design elements
// on the top pages of both apps. Usage: node element-audit.mjs <app>
//   app = omni | llmr   → writes <tmpdir>/styles-<app>.json
import { chromium } from 'playwright';
import { writeFileSync, mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const app = process.argv[2];
const cfg = {
  omni: { base: 'https://omniroute.afonsoft.dev', settle: 5000 },
  llmr: { base: 'http://localhost:20128', settle: 3000 },
}[app];

const PAGES = [
  '/home', '/dashboard', '/dashboard/api-manager', '/dashboard/providers',
  '/dashboard/combos', '/dashboard/models', '/dashboard/logs/console',
  '/dashboard/logs', '/dashboard/analytics', '/dashboard/costs',
  '/dashboard/cli-agents', '/dashboard/settings', '/dashboard/audit',
  '/docs', '/dashboard/health', '/dashboard/leaderboard',
];

const outDir = mkdtempSync(join(tmpdir(), 'element-audit-'));
const browser = await chromium.launch();
const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 } });
const page = await ctx.newPage();
page.setDefaultTimeout(60_000);

// login
await page.goto(cfg.base + '/login', { waitUntil: 'domcontentloaded' });
await page.waitForTimeout(2500);
await page.waitForSelector('input[type="password"]', { timeout: 60_000 });
for (const pw of (process.env.E2E_PASS || 'e2e-test-pass').split(',')) {
  for (const i of await page.$$('input[type="password"]')) await i.fill(pw); // NOSONAR — credential candidates must be tried sequentially
  await page.keyboard.press('Enter');
  try { await page.waitForURL(/dashboard|home/, { timeout: 8000 }); break; }
  catch { /* next candidate */ }
}
await page.goto(cfg.base + '/home', { waitUntil: 'domcontentloaded' });
await page.waitForTimeout(3000);

const PROPS = ['fontFamily', 'fontSize', 'fontWeight', 'color', 'backgroundColor',
  'border', 'borderRadius', 'padding', 'height', 'boxShadow', 'letterSpacing',
  'textTransform', 'gap', 'lineHeight'];

const grab = (sel, extra) => page.evaluate(({ sel, PROPS, extra }) => {
  const els = [...document.querySelectorAll(sel)]
    .filter(e => e.offsetParent !== null && e.getBoundingClientRect().width > 0);
  return els.slice(0, 4).map(e => {
    const cs = getComputedStyle(e);
    const o = { tag: e.tagName.toLowerCase(), cls: (e.className || '').toString().slice(0, 80), text: (e.textContent || '').trim().slice(0, 40) };
    for (const p of PROPS) o[p] = cs[p];
    if (extra) o._note = extra;
    return o;
  });
}, { sel, PROPS, extra });

const out = {};
for (const route of PAGES) {
  try {
    const resp = await page.goto(cfg.base + route, { waitUntil: 'domcontentloaded', timeout: 60_000 }); // NOSONAR — pages audited sequentially to keep server load low
    await page.waitForTimeout(cfg.settle); // NOSONAR — settle time per page is intentional
    const keys = ['body', 'h1', 'h2', 'h3', 'inputs', 'selects', 'buttons', 'links', 'cards', 'tableHead', 'nav', 'sidebarItems'];
    const sels = ['body', 'h1', 'h2', 'h3',
      'input:not([type="hidden"]):not([type="checkbox"]):not([type="radio"])',
      'select', 'button', 'a', '[class*="card"], [class*="Card"]', 'th', 'nav',
      'nav a, aside a, [class*="sidebar"] a'];
    const vals = await Promise.all(sels.map(s => grab(s))); // NOSONAR — page loop stays sequential by design
    out[route] = { status: resp?.status(), ...Object.fromEntries(keys.map((k, i) => [k, vals[i]])) };
    console.log(`${route}: ok`);
  } catch (e) { out[route] = { error: e.message.slice(0, 120) }; console.log(`${route}: FAIL`); }
}
await browser.close();
const outFile = join(outDir, `styles-${app}.json`);
writeFileSync(outFile, JSON.stringify(out, null, 1));
console.log(`wrote ${outFile}`);
