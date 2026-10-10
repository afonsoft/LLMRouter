import { chromium } from 'playwright';
const pages = ['aggressive','ccr','headroom','lite','llmlingua','omniglyph','session-dedup','ultra','caveman','combos','rtk','settings'];
const b = await chromium.launch(); const p = await b.newPage();
let cur='';
p.on('console', m => { if (m.type()==='error') console.log('ERR',cur,'::',m.text().split('\n').slice(0,3).join(' | ')); });
await p.goto('http://localhost:20128/',{waitUntil:'domcontentloaded'});
await p.waitForSelector('input[type="password"]',{timeout:120000});
for (const i of await p.$$('input[type="password"]')) await i.fill('e2e-test-pass');
await p.click('button[type="submit"]'); await p.waitForURL(/dashboard|home/,{timeout:60000});
for (const pg of pages) {
  cur = '/dashboard/context/'+pg;
  await p.goto('http://localhost:20128'+cur,{waitUntil:'domcontentloaded'});
  await p.waitForTimeout(2500);
  const info = await p.evaluate(() => ({
    title: document.querySelector('.page-title')?.textContent?.trim()||'',
    inputs: document.querySelectorAll('input,select,textarea').length,
    buttons: document.querySelectorAll('button').length,
    stuckEllipsis: document.body.innerText.includes('\u2026\n') || /^\s*…\s*$/m.test(document.body.innerText),
    rulesCount: (document.body.innerText.match(/Rules \(\s*(\d+)\s*\)/)||[])[1] || null,
    text: document.body.innerText.replace(/\s+/g,' ').slice(0,200)
  }));
  console.log(pg, JSON.stringify(info));
}
await b.close();
