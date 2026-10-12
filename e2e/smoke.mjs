// SPEC-030: Playwright smoke — login → dashboard, providers, playground.
// Run: BASE_URL=http://localhost:20128 node e2e/smoke.mjs
import { chromium } from 'playwright';

const base = process.env.BASE_URL || 'http://localhost:20128';
const browser = await chromium.launch();
const page = await browser.newPage();
const fail = (msg) => { console.error('E2E FAIL:', msg); process.exit(1); };

try {
  // 1. app boots → login (first-run accepts any password)
  await page.goto(base + '/', { waitUntil: 'domcontentloaded' });
  await page.waitForSelector('input[type="password"]', { timeout: 90_000 });
  for (const input of await page.$$('input[type="password"]'))
    await input.fill(process.env.E2E_PASS || 'e2e-test-pass');
  await page.click('button[type="submit"]');
  await page.waitForURL(/dashboard|home/, { timeout: 60_000 });

  // 2. dashboard shell renders (sidebar + page content)
  await page.waitForSelector('.sidebar, nav, aside', { timeout: 60_000 });
  await page.waitForSelector('.page-title, h1, main', { timeout: 30_000 });

  // 3. providers page lists the catalog
  await page.goto(base + '/dashboard/providers', { waitUntil: 'domcontentloaded' });
  await page.waitForSelector('.page-title', { timeout: 60_000 });
  if (!(await page.textContent('body'))?.match(/provider/i)) fail('providers page empty');

  // 4. playground renders
  await page.goto(base + '/dashboard/playground', { waitUntil: 'domcontentloaded' });
  await page.waitForSelector('.page-title', { timeout: 60_000 });

  // 5. SPEC-123: /dashboard/settings/security deep-links the Security tab
  await page.goto(base + '/dashboard/settings/security', { waitUntil: 'domcontentloaded' });
  await page.waitForSelector('.tab.on', { timeout: 60_000 });
  const onTab = (await page.textContent('.tab.on')) || '';
  if (!/security/i.test(onTab)) fail(`expected Security tab active, got "${onTab.trim()}"`);

  // 6. tab switch updates the URL (client-side nav)
  await page.click('a.tab[href="/dashboard/settings/cache"]');
  await page.waitForURL(/settings\/cache/, { timeout: 30_000 });
  const onCache = (await page.textContent('.tab.on')) || '';
  if (!/cache/i.test(onCache)) fail(`expected Cache tab active after nav, got "${onCache.trim()}"`);

  // 7. unknown section falls back to General
  await page.goto(base + '/dashboard/settings/bogus-section', { waitUntil: 'domcontentloaded' });
  await page.waitForSelector('.tab.on', { timeout: 60_000 });
  const onBogus = (await page.textContent('.tab.on')) || '';
  if (!/general/i.test(onBogus)) fail(`expected General fallback, got "${onBogus.trim()}"`);

  // 8. SPEC-124: home Quick Start — 4 steps linking to their pages + Full docs
  await page.goto(base + '/home', { waitUntil: 'domcontentloaded' });
  await page.waitForSelector('.page-title', { timeout: 60_000 });
  for (const href of ['/dashboard/api-manager', '/dashboard/providers', '/dashboard/endpoint', '/dashboard/analytics']) {
    if (!(await page.$(`a[href="${href}"]`))) fail(`home missing quick-start link ${href}`);
  }
  if (!(await page.$('a[href="/docs"]'))) fail('home missing Full docs link');

  // 9. Provider Topology card present
  if (!(await page.textContent('body'))?.match(/provider topology/i)) fail('home missing topology card');

  // 10. announcement banner: hidden by default, shown when settings.announcement set
  const ann = await page.$('text=/maintenance-window-test/i');
  if (ann) fail('announcement visible before being set');
  const setRes = await page.request.put(base + '/api/settings', {
    data: { announcement: 'maintenance-window-test' },
  });
  if (!setRes.ok()) fail('settings PUT failed: ' + setRes.status());
  await page.goto(base + '/home', { waitUntil: 'domcontentloaded' });
  await page.waitForSelector('text=maintenance-window-test', { timeout: 60_000 });
  await page.request.put(base + '/api/settings', { data: { announcement: '' } });
  await page.goto(base + '/home', { waitUntil: 'domcontentloaded' });
  await page.waitForSelector('.page-title', { timeout: 60_000 });
  if (await page.$('text=maintenance-window-test')) fail('announcement still visible after clear');

  // 11. SPEC-131: element-level token fidelity vs OmniRoute
  // .input = 14px + inset shadow; .btn = h36/font 14px; .header-title = 20px/600
  await page.goto(base + '/dashboard/settings', { waitUntil: 'domcontentloaded' });
  await page.waitForSelector('.input', { timeout: 60_000 });
  const inputCss = await page.$eval('.input', (e) => {
    const cs = getComputedStyle(e);
    return { fontSize: cs.fontSize, boxShadow: cs.boxShadow };
  });
  if (inputCss.fontSize !== '14px') fail(`input font ${inputCss.fontSize} != 14px`);
  if (!/inset/.test(inputCss.boxShadow)) fail('input missing inset shadow');
  await page.goto(base + '/dashboard/providers', { waitUntil: 'domcontentloaded' });
  await page.waitForSelector('.btn', { timeout: 60_000 });
  const btnCss = await page.$eval('.btn:not(.btn-sm):not(.btn-xs):not(.btn-lg)', (e) => {
    const cs = getComputedStyle(e);
    return { height: Number.parseFloat(cs.height), fontSize: cs.fontSize };
  });
  if (Math.abs(btnCss.height - 36) > 1) fail(`.btn height ${btnCss.height} != 36`);
  if (btnCss.fontSize !== '14px') fail(`.btn font ${btnCss.fontSize} != 14px`);
  const ht = await page.$eval('.header-title', (e) => {
    const cs = getComputedStyle(e);
    return { fontSize: cs.fontSize, fontWeight: cs.fontWeight };
  });
  if (ht.fontSize !== '20px' || ht.fontWeight !== '600')
    fail(`.header-title ${ht.fontSize}/${ht.fontWeight} != 20px/600`);

  // 12. SPEC-131: /docs renders in the standalone dark portal layout
  await page.goto(base + '/docs', { waitUntil: 'domcontentloaded' });
  await page.waitForSelector('.docs-portal', { timeout: 60_000 });
  const portalBg = await page.$eval('.docs-portal', (e) => getComputedStyle(e).backgroundColor);
  if (portalBg !== 'rgb(11, 14, 20)') fail(`docs portal bg ${portalBg} != #0b0e14`);
  if (await page.$('.sidebar')) fail('docs page leaked the dashboard sidebar');

  // 13. SPEC-135: /signup is a public page (auth-exempt) with the signup form
  await page.goto(base + '/signup', { waitUntil: 'domcontentloaded' });
  await page.waitForSelector('input[type="email"]', { timeout: 60_000 });
  const sgTxt = await page.evaluate(() => document.body.innerText);
  if (!sgTxt.includes('Create account')) fail('signup page missing create-account form');

  console.log('E2E smoke OK');
} catch (err) {
  fail(err.message);
} finally {
  await browser.close();
}
