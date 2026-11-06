// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
const {chromium}=require('playwright');const fs=require('fs'),path=require('path'),assert=require('assert/strict');
(async()=>{const root=path.resolve(__dirname,'../app/src/main/assets/label-print');
const browser=await chromium.launch({headless:true,...(process.env.CHROME_PATH ? {executablePath:process.env.CHROME_PATH} : {})});const page=await browser.newPage({viewport:{width:390,height:844},deviceScaleFactor:1});const errors=[];page.on('pageerror',e=>errors.push(e.message));
await page.route('**/*',route=>{const url=new URL(route.request().url());const name=url.pathname.slice(1);if(url.hostname==='streletz-print.invalid'&&['index.html','print.js','print-core.js','wp9529.js','niimbluelib-0.48.0.min.js'].includes(name))route.fulfill({contentType:name.endsWith('.js')?'application/javascript':'text/html',body:fs.readFileSync(path.join(root,name))});else route.abort();});
await page.addInitScript(()=>{window.fixture={pending:false,calls:[]};window.LabelNative={config:()=>JSON.stringify({width:30,height:12,copies:1,pending:false}),pending:v=>{fixture.pending=v;return true;},setPrinting:()=>{},saveConfig:()=>true,savePrinterConfig:()=>true,request:(id,action,payload)=>{fixture.calls.push(action);setTimeout(()=>nativeReply(id,null,action==='scan'?[{id:'TEST',name:'D110 fixture'}]:{}),0);}};});
await page.goto('https://streletz-print.invalid/index.html');await page.evaluate(()=>setDescription('3R.BTM207'));
assert.equal(await page.locator('#print').isDisabled(),true);assert.equal(await page.evaluate(()=>fixture.calls.length),0);
await page.screenshot({path:path.resolve('print-preview-test.png'),fullPage:true});
await page.evaluate(()=>setDescription('Пожарный извещатель\nПомещение 201'));assert.match(await page.locator('#previewNote').textContent(),/12×30/);
await page.locator('#rotation').selectOption('90');assert.match(await page.locator('#previewNote').textContent(),/12×30/);await page.locator('#rotation').selectOption('0');
await page.locator('#height').fill('13');assert.match(await page.locator('#previewNote').textContent(),/ширина 6–12/);assert.equal(await page.locator('#print').isDisabled(),true);await page.locator('#height').fill('12');
await page.evaluate(()=>{niimbluelib.NiimbotAbstractClient.prototype.negotiateAndGetPrinterInfo=async function(){this.printerInfo={modelId:2304,protocolVersion:1,softwareVersion:13.14,hardwareVersion:13.1};};niimbluelib.NiimbotProtocol.prototype.newPrintTask=()=>Object.fromEntries(['printInit','printPage','waitForPageFinished','waitForFinished','printEnd'].map(n=>[n,async()=>{fixture.calls.push(n);} ]));});
await page.locator('#scan').click();await page.waitForFunction(()=>document.querySelector('#devices').value==='TEST');await page.locator('#connect').click();await page.waitForFunction(()=>!document.querySelector('#print').disabled);
await page.locator('#print').click();await page.waitForFunction(()=>document.querySelector('#status').textContent.includes('подтвердил завершение'));
assert.equal(await page.evaluate(()=>fixture.pending),false);assert.equal(await page.evaluate(()=>fixture.calls.filter(x=>x==='printPage').length),1);
await page.evaluate(()=>{niimbluelib.NiimbotProtocol.prototype.newPrintTask=()=>({printInit:async()=>{},printPage:async()=>{throw Error('Тестовый обрыв');},printEnd:async()=>{}});});
await page.locator('#print').click();await page.waitForFunction(()=>!document.querySelector('#review').hidden);assert.equal(await page.locator('#print').isDisabled(),true);assert.equal(await page.evaluate(()=>fixture.pending),true);
await page.locator('#review').click();assert.equal(await page.evaluate(()=>fixture.pending),false);assert.equal(await page.locator('#print').isDisabled(),false);
assert.deepEqual(errors,[]);console.log('PASS browser label UI: Cyrillic/rotation/bounds, no automatic connection, success/unknown/manual release, no JavaScript errors. Native BLE mocked.');await browser.close();
})().catch(e=>{console.error(e);process.exit(1);});
