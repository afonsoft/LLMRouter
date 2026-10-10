import { chromium } from 'playwright';
const b = await chromium.launch(); const p = await b.newPage();
await p.goto('http://localhost:20128/',{waitUntil:'domcontentloaded'});
await p.waitForSelector('input[type="password"]',{timeout:120000});
for (const i of await p.$$('input[type="password"]')) await i.fill('e2e-test-pass');
await p.click('button[type="submit"]'); await p.waitForURL(/dashboard|home/,{timeout:60000});
const exp = new Date(Date.now()+3*86400e3).toISOString();
const r = await p.evaluate(async ({exp}) => {
  const res = await fetch('/api/credentials/expiration',{method:'POST',credentials:'same-origin',headers:{'Content-Type':'application/json'},body:JSON.stringify({connectionId:'2b544e599fdb',expiresAt:exp,warnDays:7})});
  const g = await fetch('/api/credentials/expiring',{credentials:'same-origin'});
  return [res.status, await res.text(), g.status, await g.text()];
}, {exp});
console.log(r.join('\n'));
await b.close();
