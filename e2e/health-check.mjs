import { chromium } from 'playwright';
const base = 'http://localhost:20128';
const b = await chromium.launch();
const pg = await (await b.newContext({ viewport: { width: 1440, height: 1000 } })).newPage();
await pg.goto(base + '/login'); await pg.waitForSelector('input[type=password]', { timeout: 20000 });
await pg.fill('input[type=password]', process.env.E2E_PASS || 'test1234');
await pg.click('button[type=submit], .btn');
await pg.waitForURL(/dashboard|home/, { timeout: 15000 }).catch(() => {});
await pg.goto(base + '/dashboard/health');
await pg.waitForTimeout(2500);
await pg.screenshot({ path: '/tmp/health-page.png', fullPage: true });
const text = await pg.textContent('body');
for (const s of ['Concurrency queues','Admission','Advanced diagnostics','Database health','Uptime','Provider health','Rate limit status','Active lockouts','Session Activity','Quota Monitors'])
  console.log(text.includes(s) ? 'OK  ' + s : 'MISS ' + s);
// open advanced
await pg.click('text=Advanced diagnostics');
await pg.waitForTimeout(2000);
await pg.screenshot({ path: '/tmp/health-adv.png', fullPage: true });
const t2 = await pg.textContent('body');
for (const s of ['Telemetry','Provider health autopilot','Provider health matrix'])
  console.log(t2.includes(s) ? 'OK  ' + s : 'MISS ' + s);
await b.close();
