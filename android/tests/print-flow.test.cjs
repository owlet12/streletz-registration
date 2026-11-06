// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
const {chromium}=require('playwright'),fs=require('fs'),path=require('path'),assert=require('node:assert/strict');
(async()=>{
 const assets=path.resolve(__dirname,'../app/src/main/assets/label-print');
 const browser=await chromium.launch({headless:true,...(process.env.CHROME_PATH?{executablePath:process.env.CHROME_PATH}:{})});
 try{
  const page=await browser.newPage({viewport:{width:390,height:844}}),errors=[];
  page.on('pageerror',e=>errors.push(e.message));
  await page.route('**/*',route=>{const u=new URL(route.request().url()),n=u.pathname.slice(1);if(u.hostname==='streletz-print.invalid'&&['index.html','print.js','print-core.js','wp9529.js','niimbluelib-0.48.0.min.js'].includes(n))route.fulfill({contentType:n.endsWith('.js')?'application/javascript':'text/html',body:fs.readFileSync(path.join(assets,n))});else route.abort();});
  await page.addInitScript(()=>{
   let saved={};try{saved=JSON.parse(window.name||'{}');}catch{}
   const persist=()=>window.name=JSON.stringify(fixture.saved);
   window.fixture={saved,calls:[],writes:[],saves:[],headers:0,rows:0,hold:false,delay:false,fail:'',failRemember:false,prepareReply:null};
   const notify=p=>onBleData(btoa(String.fromCharCode(...p)));
   window.LabelNative={
    config:()=>JSON.stringify({width:40,height:12,copies:1,profile:'wp9539',...saved}),setPrinting:()=>{},
    saveConfig:(...v)=>{fixture.saves.push(v);return true;},
    saveTextConfig:v=>{fixture.saves.push(JSON.parse(v));Object.assign(fixture.saved,JSON.parse(v));persist();return true;},
    savePrinterConfig:v=>{fixture.saves.push(JSON.parse(v));Object.assign(fixture.saved,JSON.parse(v));persist();return true;},
    pending:v=>{fixture.saved.pending=v;persist();return true;},
    request:(id,action,payload)=>{
     fixture.calls.push([action,payload]);let result={};
     if(action==='prepare'){
      result={profile:'wp9539',name:'9539#TEST',maxPayload:182};
      const finish=()=>nativeReply(id,fixture.fail||null,result);
      if(fixture.delay){fixture.prepareReply=finish;return;}
      setTimeout(finish,0);return;
     }
     if(action==='remember'){
      if(fixture.failRemember){setTimeout(()=>nativeReply(id,'Не удалось сохранить принтер',null),0);return;}
      fixture.saved.printerName='9539#TEST';persist();
     }
     if(action==='write'){
      const p=Array.from(atob(payload),c=>c.charCodeAt(0));fixture.writes.push(p);
      if(p[0]===1)notify([1,12,0,96,0,fixture.hold&&fixture.rows?1:0,0,0,0,0]);
      else if(p[0]===4)notify([4,0]);
      else if(p[0]===2){fixture.headers++;fixture.rows=0;notify([0xa5,0x20,3,0,0,0]);}
      else if(p[0]===3){fixture.rows+=p[3];if(fixture.drop){onBleDisconnected();setTimeout(()=>nativeReply(id,'Тестовый обрыв',null),0);return;}}
      else throw Error('Unexpected opcode');
     }
     setTimeout(()=>nativeReply(id,null,result),0);
    }
   };
  });
  async function init(value='Исходное описание'){await page.evaluate(value=>{setDescription(value);const Driver=WP9529.Driver;WP9529.Driver=class extends Driver{constructor(t,o){super(t,{...o,sleep:()=>new Promise(r=>setTimeout(r,2))});}};},value);}
  async function fresh(value){await page.reload();await init(value);}
  const done=()=>page.waitForFunction(()=>document.querySelector('#status').textContent.includes('подтвердил завершение'));
  const prepareCount=()=>page.evaluate(()=>fixture.calls.filter(c=>c[0]==='prepare').length);
  const noJob=async()=>assert.equal(await page.evaluate(()=>fixture.writes.some(p=>p[0]!==1)),false);
  const idle=()=>page.waitForFunction(()=>!document.querySelector('#description').disabled);
  await page.goto('https://streletz-print.invalid/index.html');await init();
  assert.equal(await page.locator('#scan,#connect,#devices,#profile').count(),0);
  assert.equal(await page.locator('#description').inputValue(),'Исходное описание');
  assert.equal(await page.locator('#description').getAttribute('readonly'),null);
  assert.equal(await prepareCount(),0);assert.equal(await page.evaluate(()=>fixture.saves.length),0);
  await page.locator('#description').fill('Дополнение\nПомещение 201');
  await page.evaluate(()=>setDescription('Исходное описание'));
  assert.equal(await page.locator('#description').inputValue(),'Дополнение\nПомещение 201');
  assert.equal(await prepareCount(),0);assert.equal(await page.evaluate(()=>fixture.saves.length),0);
  await page.screenshot({path:path.resolve('print-flow-0526.png'),fullPage:true});
  // One press includes prepare, validation, remembering and one job. Duplicate presses are ignored.
  await page.evaluate(()=>fixture.hold=true);await page.locator('#print').click();await page.waitForFunction(()=>fixture.rows===320);
  assert.equal(await page.locator('#description').isDisabled(),true);assert.equal(await page.locator('#print').isDisabled(),true);
  await page.locator('#print').dispatchEvent('click');assert.equal(await page.evaluate(()=>fixture.headers),1);
  assert.equal(await prepareCount(),1);assert.deepEqual(await page.evaluate(()=>fixture.calls.filter(c=>c[0]==='prepare')),[['prepare','']]);
  assert.equal(await page.evaluate(()=>fixture.calls.filter(c=>c[0]==='remember').length),1);
  await page.evaluate(()=>fixture.hold=false);await done();
  assert.equal(await page.evaluate(()=>fixture.saved.pending),false);
  assert.doesNotMatch(await page.evaluate(()=>JSON.stringify(fixture.saves)+window.name),/Дополнение|Помещение|description|text/);
  // An already connected printer is reused on the same screen.
  await page.locator('#description').fill('Вторая этикетка');await page.locator('#print').click();await page.waitForFunction(()=>fixture.headers===2);await done();
  assert.equal(await prepareCount(),1);
  // Reopening starts from the project description, then reconnects with no selection action.
  await fresh();assert.equal(await page.locator('#description').inputValue(),'Исходное описание');assert.equal(await prepareCount(),0);
  assert.equal(await page.evaluate(()=>fixture.saved.printerName),'9539#TEST');
  await page.locator('#print').click();await done();assert.deepEqual(await page.evaluate(()=>fixture.calls.filter(c=>c[0]==='prepare')),[['prepare','']]);
  // Empty descriptions can be replaced for printing without a project edit.
  await fresh('');assert.equal(await page.locator('#print').isDisabled(),true);
  await page.locator('#description').fill('Новая этикетка');assert.equal(await page.locator('#print').isDisabled(),false);
  // Long press selects a printer without printing on release.
  await page.locator('#print').scrollIntoViewIfNeeded();const box=await page.locator('#print').boundingBox();
  await page.mouse.move(box.x+box.width/2,box.y+box.height/2);await page.mouse.down();
  await page.waitForFunction(()=>document.querySelector('#status').textContent.includes('Принтер сохранён'));await page.mouse.up();await idle();
  assert.deepEqual(await page.evaluate(()=>fixture.calls.filter(c=>c[0]==='prepare')),[['prepare','choose']]);await noJob();
  await page.locator('#print').click();await done();assert.equal(await page.evaluate(()=>fixture.headers),1);
  // A rejected connection is not a dispatched or unknown job and is never retried automatically.
  await fresh();await page.evaluate(()=>fixture.fail='Выбор принтера отменён');await page.locator('#print').click();
  await page.waitForFunction(()=>document.querySelector('#status').textContent.includes('отменён'));await idle();await noJob();
  assert.equal(await prepareCount(),1);assert.equal(await page.locator('#review').isVisible(),false);
  assert.equal(await page.evaluate(()=>!!fixture.saved.pending),false);
  await page.evaluate(()=>{fixture.fail='';fixture.failRemember=true;});await page.locator('#print').click();
  await page.waitForFunction(()=>document.querySelector('#status').textContent.includes('Не удалось сохранить принтер'));await idle();await noJob();
  // Closing the screen while a native picker is outstanding cancels late continuation.
  await fresh();await page.evaluate(()=>fixture.delay=true);await page.locator('#print').click();await page.waitForFunction(()=>fixture.prepareReply!==null);
  await page.evaluate(()=>{onPrintScreenStopped();onBleDisconnected();fixture.prepareReply();});await idle();await noJob();
  assert.equal(await page.evaluate(()=>fixture.calls.some(c=>c[0]==='remember')),false);
  // Failure after data dispatch remains durable and requires an explicit inspection, never replay.
  await fresh();await page.evaluate(()=>fixture.drop=true);await page.locator('#print').click();
  await page.waitForFunction(()=>!document.querySelector('#review').hidden&&!document.querySelector('#review').disabled);
  assert.equal(await page.evaluate(()=>fixture.saved.pending),true);assert.equal(await page.locator('#print').isDisabled(),true);
  const headers=await page.evaluate(()=>fixture.headers);await page.locator('#print').dispatchEvent('click');assert.equal(await page.evaluate(()=>fixture.headers),headers);
  await fresh();assert.equal(await page.locator('#review').isVisible(),true);assert.equal(await prepareCount(),0);
  await page.locator('#review').click();assert.equal(await page.evaluate(()=>fixture.saved.pending),false);assert.equal(await page.evaluate(()=>fixture.writes.length),0);
  assert.deepEqual(errors,[]);
  console.log('PASS print flow: transient editable text, blank description, one-button connect and print, remembered printer across screens, connection reuse, long-press change without print, duplicate press guard, no save of label text, cancellation/late callback guard, durable unknown and no replay. Android BLE and picker mocked.');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
