// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
const {chromium}=require('playwright'),fs=require('fs'),path=require('path'),assert=require('node:assert/strict');
(async()=>{
 const assets=path.resolve(__dirname,'../app/src/main/assets/label-print');
 const browser=await chromium.launch({headless:true,...(process.env.CHROME_PATH?{executablePath:process.env.CHROME_PATH}:{})});
 try{
 const page=await browser.newPage({viewport:{width:390,height:844}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.route('**/*',route=>{const u=new URL(route.request().url()),n=u.pathname.slice(1);if(u.hostname==='streletz-print.invalid'&&['index.html','print.js','print-core.js','wp9529.js','niimbluelib-0.48.0.min.js'].includes(n))route.fulfill({contentType:n.endsWith('.js')?'application/javascript':'text/html',body:fs.readFileSync(path.join(assets,n))});else route.abort();});
 await page.addInitScript(()=>{
  let saved={};try{saved=JSON.parse(window.name||'{}');}catch{}
  window.fixture={saved,writes:[],rows:0,hold:false,failSave:false,awake:[]};
  const notify=p=>onBleData(btoa(String.fromCharCode(...p)));
  window.LabelNative={config:()=>JSON.stringify({width:40,height:12,copies:1,profile:'wp9539',...saved}),setPrinting:v=>fixture.awake.push(v),saveConfig:()=>true,savePrinterConfig:()=>true,pending:()=>true,
   saveTextConfig:value=>{if(fixture.failSave)return false;fixture.saved=JSON.parse(value);window.name=value;return true;},
   request:(id,action,payload)=>{let result={};
    if(action==='prepare')result={profile:'wp9539',name:'9539#TEST',maxPayload:182};
    if(action==='write'){
     const p=Array.from(atob(payload),c=>c.charCodeAt(0));fixture.writes.push(p);
     if(p[0]===1)notify([1,12,0,96,0,fixture.hold&&fixture.rows?1:0,0,0,0,0]);
     else if(p[0]===4)notify([4,0]);
     else if(p[0]===2)notify([0xa5,0x20,3,0,0,0]);
     else if(p[0]===3)fixture.rows+=p[3];
     else throw Error('Unexpected opcode');
    }
    setTimeout(()=>nativeReply(id,null,result),0);
   }};
 });
 const init=async()=>page.evaluate(()=>{setDescription('3R.BTM9');const Driver=WP9529.Driver;WP9529.Driver=class extends Driver{constructor(t,o){super(t,{...o,sleep:()=>new Promise(r=>setTimeout(r,2))});}};});
 const ink=async()=>page.evaluate(()=>{const c=document.querySelector('#preview'),p=c.getContext('2d').getImageData(0,0,c.width,c.height).data;let black=0;for(let i=0;i<p.length;i+=4)if(p[i]===0)black++;return black;});
 await page.goto('https://streletz-print.invalid/index.html');await init();
 assert.equal(await page.locator('#fontAuto').isChecked(),true);assert.ok(Number(await page.locator('#fontSize').inputValue())>13);
 await page.locator('#fontSize').fill('13');const small=await ink();await page.locator('#fontPlus').click();
 assert.equal(await page.locator('#fontSize').inputValue(),'14');assert.equal(await page.locator('#fontAuto').isChecked(),false);assert.ok(await ink()>small);
 assert.equal(await page.evaluate(()=>fixture.saved.fontSize),14);assert.equal(await page.evaluate(()=>fixture.saved.fontAuto),false);assert.equal(await page.evaluate(()=>fixture.writes.length),0);
 await page.reload();await init();assert.equal(await page.locator('#fontSize').inputValue(),'14');assert.equal(await page.locator('#fontAuto').isChecked(),false);

 await page.locator('#fontSize').fill('36');assert.match(await page.locator('#previewNote').textContent(),/не помещается/);assert.equal(await page.locator('#print').isDisabled(),true);assert.equal(await ink(),0);
 await page.locator('#fontSize').fill('37');assert.match(await page.locator('#previewNote').textContent(),/от 3 до 36/);assert.equal(await page.locator('#print').isDisabled(),true);
 await page.locator('#fontAuto').check();assert.equal(await page.locator('#print').isDisabled(),false); // Auto mode recovers from invalid manual input.
 await page.locator('#fontSize').fill('18');await page.locator('#fontAuto').check();assert.equal(await page.locator('#print').isDisabled(),false);
 await page.locator('#rotation').selectOption('90');assert.doesNotMatch(await page.locator('#previewNote').textContent(),/не помещается/);await page.locator('#rotation').selectOption('0');
 await page.screenshot({path:path.resolve('font-preview-0526.png'),fullPage:true});
 const previewInk=await ink();await page.evaluate(()=>fixture.hold=true);await page.locator('#print').click();await page.waitForFunction(()=>fixture.rows===320);
 for(const id of ['fontSize','fontMinus','fontPlus','fontAuto'])assert.equal(await page.locator('#'+id).isDisabled(),true);
 const sentInk=await page.evaluate(()=>fixture.writes.filter(p=>p[0]===3).flatMap(p=>p.slice(4)).reduce((n,v)=>n+v.toString(2).replace(/0/g,'').length,0));assert.equal(sentInk,previewInk);
 await page.evaluate(()=>fixture.hold=false);await page.waitForFunction(()=>document.querySelector('#status').textContent.includes('подтвердил завершение'));
 assert.equal(await page.locator('#fontSize').isDisabled(),false);assert.deepEqual(await page.evaluate(()=>fixture.awake),[true,false]);
 await page.evaluate(()=>{fixture.failSave=true;fixture.writes=[];});await page.locator('#print').click();await page.waitForFunction(()=>document.querySelector('#status').textContent.includes('Не удалось сохранить размер'));
 assert.equal(await page.evaluate(()=>fixture.writes.length),0);assert.deepEqual(errors,[]);
 console.log('PASS font UI: actual larger raster, auto maximum, manual persistence/reload, overflow blocks print, rotation, preview equals transmitted raster, controls locked during print, failed save sends nothing. BLE simulated.');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
