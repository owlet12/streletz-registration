// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
const {chromium}=require('playwright'),fs=require('fs'),path=require('path'),assert=require('node:assert/strict');
const model=process.env.PRINTER_PROFILE==='wp9539'?{id:'wp9539',name:'WP9539',prefix:'9539#',fw:0x31,version:'V31'}:{id:'wp9529',name:'WP9529',prefix:'9529#',fw:0x50,version:'V50'};
(async()=>{
 const assets=path.resolve(__dirname,'../app/src/main/assets/label-print');
 const browser=await chromium.launch({headless:true,...(process.env.CHROME_PATH?{executablePath:process.env.CHROME_PATH}:{})});
 try{
 const page=await browser.newPage({viewport:{width:390,height:844}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.route('**/*',route=>{const u=new URL(route.request().url()),n=u.pathname.slice(1);if(u.hostname==='streletz-print.invalid'&&['index.html','print.js','print-core.js','wp9529.js','niimbluelib-0.48.0.min.js'].includes(n))route.fulfill({contentType:n.endsWith('.js')?'application/javascript':'text/html',body:fs.readFileSync(path.join(assets,n))});else route.abort();});
 await page.addInitScript(model=>{
  window.fixture={profile:'d110',fw:model.fw,calls:[],writes:[],headers:0,lines:0,polls:0,hold:false,disconnect:false,pending:window.name==='pending'};
  const notify=data=>onBleData(btoa(String.fromCharCode(...data)));
  window.LabelNative={config:()=>JSON.stringify({width:30,height:12,copies:1,profile:model.id,pending:fixture.pending}),setPrinting:()=>{},saveConfig:()=>true,saveTextConfig:()=>true,savePrinterConfig:()=>true,pending:value=>{fixture.pending=value;window.name=value?'pending':'';return true;},request:(id,action,payload)=>{
   fixture.calls.push([action,payload]);
   let result={};if(action==='prepare')result={profile:model.id,name:fixture.wrongName?'95391#FIXTURE':model.prefix+'FIXTURE',maxPayload:20,mtu:23};
   if(action==='write'){
    const p=Array.from(atob(payload),c=>c.charCodeAt(0));fixture.writes.push(p);
    if(p[0]===1){let state=0;if(fixture.headers&&fixture.lines===240)state=(fixture.hold||++fixture.polls<3)?1:0;notify([1,12,0,96,0,state,0,0,0,0]);}
    else if(p[0]===0xb2)notify([0xb2,fixture.fw]);
    else if(p[0]===4)notify([4,0]);
    else if(p[0]===2){fixture.headers++;fixture.lines=0;fixture.polls=0;notify([0xa5,0x20,3,0,0,0]);}
    else if(p[0]===3){fixture.lines+=p[3];if(fixture.disconnect){onBleDisconnected();setTimeout(()=>nativeReply(id,'Тестовый обрыв',null),0);return;}}
   }
   setTimeout(()=>nativeReply(id,null,result),0);
  }};
 },model);
 async function init(){await page.evaluate(()=>{setDescription('Кириллица\nПомещение 201');const Driver=WP9529.Driver;WP9529.Driver=class extends Driver{constructor(t,o){super(t,{...o,sleep:()=>new Promise(r=>setTimeout(r,5)),doneMs:5000});}};});}
 async function connect(){await page.locator('#print').press('Shift+F10');await page.waitForFunction(()=>document.querySelector('#status').textContent.includes('Принтер сохранён'));}
 await page.goto('https://streletz-print.invalid/index.html');await init();
 assert.equal(await page.locator('#scan,#connect,#devices,#profile').count(),0);assert.equal(await page.locator('#height').isDisabled(),true);assert.equal(await page.evaluate(()=>fixture.calls.length),0);
 await page.locator('#width').fill('100');assert.equal(await page.locator('#preview').getAttribute('width'),'800');await page.locator('#width').fill('30');
 await page.locator('#paper').selectOption('continuous');assert.equal(await page.locator('#gap').isDisabled(),true);await page.locator('#paper').selectOption('gap');
 await page.locator('#gap').fill('0');assert.match(await page.locator('#previewNote').textContent(),/Зазор/);await page.locator('#gap').fill('8.5');
 await connect();assert.equal(await page.evaluate(()=>fixture.writes.some(p=>p[0]===0xb2)),false);
 await page.screenshot({path:path.resolve(model.id+'-preview-test.png'),fullPage:true});
 await page.evaluate(()=>fixture.hold=true);await page.locator('#print').click();await page.waitForFunction(()=>fixture.lines===240);
 assert.equal(await page.evaluate(()=>fixture.pending),true);assert.equal(await page.locator('#description').isDisabled(),true);assert.equal(await page.locator('#print').isDisabled(),true);assert.doesNotMatch(await page.locator('#status').textContent(),/подтвердил завершение/);
 await page.evaluate(()=>fixture.hold=false);await page.waitForFunction(()=>document.querySelector('#status').textContent.includes('подтвердил завершение'));
 assert.equal(await page.evaluate(()=>fixture.pending),false);assert.equal(await page.evaluate(()=>fixture.writes.filter(p=>p[0]===3).length),240);
 assert.equal(await page.evaluate(()=>fixture.writes.some(p=>p[0]===3&&p.slice(4).some(Boolean))),true);
 assert.deepEqual(await page.evaluate(()=>fixture.writes.find(p=>p[0]===2)),[2,240,0,0,0,0,2,32,30,85,1]);
 await page.evaluate(()=>fixture.disconnect=true);await page.locator('#print').click();await page.waitForFunction(()=>!document.querySelector('#review').hidden&&!document.querySelector('#review').disabled);
 assert.equal(await page.evaluate(()=>fixture.pending),true);assert.equal(await page.locator('#print').isDisabled(),true);
 await page.reload();await init();assert.equal(await page.locator('#review').isVisible(),true);assert.equal(await page.evaluate(()=>fixture.calls.length),0);
 await page.locator('#review').click();assert.equal(await page.evaluate(()=>fixture.pending),false);assert.equal(await page.evaluate(()=>fixture.writes.length),0);
 // A printer that never provides its firmware can connect using status only.
 await page.evaluate(()=>fixture.fw=0x51);await connect();
 assert.equal(await page.evaluate(()=>fixture.writes.every(p=>p[0]===1)),true);
 // Even the same service and firmware cannot identify a different model.
 await page.evaluate(fw=>{fixture.fw=fw;fixture.wrongName=true;fixture.writes=[];},model.fw);
 await page.locator('#print').press('Shift+F10');await page.waitForFunction(()=>document.querySelector('#status').textContent.includes('не соответствует'));
 assert.equal(await page.locator('#print').isDisabled(),false);assert.equal(await page.evaluate(()=>fixture.writes.length),0);
 assert.deepEqual(errors,[]);console.log('PASS '+model.name+' browser UI: automatic profile/media, Cyrillic raster, MTU23 whole commands, idle-only success, disconnect, persistent unknown, wrong-model guard, no firmware dependency. Native BLE simulated.');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
