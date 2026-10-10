import { chromium } from 'playwright';
const B='http://localhost:20128';
const b=await chromium.launch(); const p=await b.newPage();
await p.goto(B+'/login'); await p.waitForSelector('input[type=password]',{timeout:20000});
await p.fill('input[type=password]','e2e-test-pass'); await p.keyboard.press('Enter'); await p.waitForURL('**/home',{timeout:20000});
for (let i=0;i<3;i++){
  const r=await p.evaluate(async()=>{const t0=performance.now();const res=await fetch('api/usage/history',{credentials:'same-origin'});const t=await res.text();return {ms:Math.round(performance.now()-t0),s:res.status,len:t.length}});
  console.log('usage/history',i,r.s,r.ms,'ms len',r.len);
}
await b.close();
