// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const root=path.join(__dirname,'../app/src/main/assets/label-print'),core=require(path.join(root,'print-core.js'));
let checks=0;function check(value,message){assert.ok(value,message);checks++;}
(async()=>{
 check(core.options({width:30,height:12,copies:1}).width===30,'12x30 defaults accepted');
 for(const bad of [{width:0,height:12,copies:1},{width:30,height:13,copies:1},{width:30,height:12,copies:0},{width:30,height:12,copies:21},{width:NaN,height:12,copies:1},{width:30.5,height:12,copies:1}]){assert.throws(()=>core.options(bad));checks++;}
 for(const value of ['', ' ', 'x'.repeat(2001)]){assert.throws(()=>core.text(value));checks++;}
 const description='Пожарный извещатель\nПомещение 201';
 check(core.text(description)===description,'Cyrillic and newlines retained');
 const layout=core.layout(description,230,86,(s,size)=>Array.from(s).length*size*.6);
 check(layout.lines.length>1&&layout.size>=8&&layout.lines.length*layout.lineHeight<=86,'text fits inside margins');
 const token='АБВГДЕЖЗ'.repeat(5);check(core.wrap(token,s=>s.length,8).join('')===token,'long identifier split without character loss');
 assert.throws(()=>core.layout('слово '.repeat(300),80,30,(s,size)=>s.length*size*.6));checks++;
 const context={console,setTimeout,clearTimeout,setInterval,clearInterval,TextEncoder,TextDecoder,Uint8Array,DataView};vm.createContext(context);vm.runInContext(fs.readFileSync(path.join(root,'niimbluelib-0.48.0.min.js'),'utf8'),context);const lib=context.niimbluelib;
 for(const id of [2304,2305]){const meta=lib.getPrinterMetaById(id);check(core.profile({modelId:id},meta,lib.findPrintTask(meta.model,1)).task==='D110','real bundled library D110 profile');}
 assert.throws(()=>core.profile({modelId:99},{model:'D110',dpi:203,printheadPixels:96,printDirection:'left'},'D110'));checks++;
 assert.throws(()=>core.profile({modelId:2304},lib.getPrinterMetaById(2304),null));checks++;
 for(const failure of [null,'printInit','printPage','waitForPageFinished','waitForFinished','printEnd']){
  const calls=[],states=[],task={};for(const name of ['printInit','printPage','waitForPageFinished','waitForFinished','printEnd'])task[name]=async(...args)=>{calls.push(name);if(name==='printPage')assert.equal(args[1],3);if(name===failure)throw Error('synthetic');};
  const hooks={persist:value=>{states.push(value);return true;},abort(){}};
  if(failure){await assert.rejects(core.run(task,{},3,hooks));check(states.length===1&&states[0]===true,'unknown kept after '+failure);check(calls.filter(n=>n==='printPage').length<=1,'no retry after '+failure);check(calls.filter(n=>n==='printEnd').length<=1,'no repeated end after '+failure);}
  else {await core.run(task,{},3,hooks);check(calls.join(',')==='printInit,printPage,waitForPageFinished,waitForFinished,printEnd','printer confirmation precedes completion');check(states.join(',')==='true,false','pending persisted before dispatch and cleared after confirmation');}
 }
 let called=false;await assert.rejects(core.run({printInit:async()=>{called=true;}},{},1,{persist:()=>false,abort(){}}));check(!called,'failed persistence blocks all printer calls');
 let aborted=false,unblock;const calls=[];await assert.rejects(core.run({printInit:()=>new Promise(r=>unblock=r),printPage:async()=>calls.push('page'),printEnd:async()=>calls.push('end')},{},1,{persist:()=>true,timeoutMs:15,abort:()=>{aborted=true;}}));unblock();await new Promise(r=>setTimeout(r,10));check(aborted&&calls.length===0,'timeout disconnects and late init cannot send page');
 const states=[];await assert.rejects(core.run({printInit:async()=>{},printPage:async()=>{},waitForPageFinished:async()=>{},waitForFinished:async()=>{},printEnd:async()=>{}},{},1,{persist:v=>{states.push(v);return v;},abort(){}}));check(states.join(',')==='true,false','failed completion persistence is not reported as success');
 console.log('PASS '+checks+' label-print assertions; real library profile, synthetic jobs; no printer.');
})().catch(e=>{console.error(e);process.exit(1);});
