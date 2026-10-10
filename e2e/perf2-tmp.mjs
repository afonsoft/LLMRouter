import { chromium } from 'playwright';
const B='http://localhost:20128';
const b=await chromium.launch(); const p=await b.newPage();
await p.goto(B+'/login'); await p.waitForSelector('input[type=password]',{timeout:20000});
await p.fill('input[type=password]','e2e-test-pass'); await p.keyboard.press('Enter'); await p.waitForURL('**/home',{timeout:20000});
const pages=['/home','/dashboard/combos','/dashboard/providers','/dashboard/api-manager','/dashboard/quota','/dashboard/context/caveman','/dashboard/analytics/combo-health','/dashboard/discovery','/dashboard/models'];
for (const u of pages){
  const t0=Date.now(); await p.goto(B+u); 
  // wait until some meaningful content present
  await p.waitForSelector('main h1, main h3, main .page-title, main table, main form',{timeout:15000}).catch(()=>{});
  console.log('PAGE',u,Date.now()-t0,'ms');
}
const apis=['api/health/connections','api/combos','api/quota','api/usage/history','api/sessions','api/policies'];
for (const a of apis){
  const r=await p.evaluate(async(a)=>{const t0=performance.now();const res=await fetch(a,{credentials:'same-origin'});await res.text();return {ms:Math.round(performance.now()-t0),s:res.status}},a);
  console.log('API',a,r.s,r.ms,'ms');
}
await b.close();
