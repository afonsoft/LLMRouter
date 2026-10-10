import { chromium } from 'playwright';
const pages = ['/home','/dashboard/combos','/dashboard/combos/live','/dashboard/providers','/dashboard/api-manager','/dashboard/quota','/dashboard/rate-limits','/dashboard/analytics/combo-health','/dashboard/analytics/utilization','/dashboard/analytics/compression','/dashboard/analytics/search','/dashboard/analytics/evals','/dashboard/context/lite','/dashboard/context/caveman','/dashboard/context/ultra','/dashboard/context/settings','/dashboard/discovery','/dashboard/settings/general','/dashboard/settings/routing','/dashboard/models','/dashboard/free-provider-rankings','/dashboard/provider-stats'];
const b = await chromium.launch(); const p = await b.newPage();
let cur='';
p.on('console', m => { if (m.type()==='error') console.log('ERR',cur,'::',m.text().split('\n').slice(0,2).join(' | ')); });
p.on('response', r => { if (r.status()>=400) console.log('HTTP'+r.status(),cur,'::',r.url().replace('http://localhost:20128','')); });
await p.goto('http://localhost:20128/',{waitUntil:'domcontentloaded'});
await p.waitForSelector('input[type="password"]',{timeout:120000});
for (const i of await p.$$('input[type="password"]')) await i.fill('e2e-test-pass');
await p.click('button[type="submit"]'); await p.waitForURL(/dashboard|home/,{timeout:60000});
for (const h of pages) {
  cur=h;
  await p.goto('http://localhost:20128'+h,{waitUntil:'domcontentloaded'});
  await p.waitForTimeout(2000);
  const info = await p.evaluate(()=>({t:document.querySelector('.page-title')?.textContent?.trim()||'',ph:document.body.innerText.includes('planned in a later spec'),len:document.body.innerText.length}));
  console.log(h, JSON.stringify(info));
}
console.log('DONE');
await b.close();
