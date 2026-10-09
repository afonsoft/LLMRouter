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
    await input.fill('e2e-test-pass');
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

  console.log('E2E smoke OK');
} catch (err) {
  fail(err.message);
} finally {
  await browser.close();
}
