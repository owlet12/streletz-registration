// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
const assert=require('node:assert/strict');
const wp=require('../app/src/main/assets/label-print/wp9529.js');
const tests=[];const test=(name,fn)=>tests.push([name,fn]);
const opts={length:30,gap:8.5,paper:'gap',density:32,copies:1};
const rows=new Uint8Array(240*12).fill(0x81);
const status=(state=0,error=0)=>Uint8Array.from([1,12,0,96,0,state,error,0,0,0,1,0x53,1]);
function fixture(options={}){
 let now=0,driver;const f={writes:[],persisted:[],aborts:0,progress:[],headers:0,lines:0,polls:0,finished:0,unknown:false};
 const transport={connected:true,maxPayload:options.maximum||182,write:async raw=>{
  const p=Uint8Array.from(raw);f.writes.push(p);if(options.write)await options.write(p,f,driver,transport);
  if(options.silent===p[0])return;
  if(p[0]===1){let s=0;if(f.headers&&f.lines===240&&f.finished<f.headers){f.polls++;s=options.busyForever||f.polls<=2?1:0;if(!s)f.finished++;}
   driver.receive(options.status?options.status(s,f):status(s));
  }else if(p[0]===0xb2)driver.receive(Uint8Array.from([0xb2,options.fw??0x50]));
  else if(p[0]===4)driver.receive(Uint8Array.of(4,0));
  else if(p[0]===2){f.headers++;f.lines=0;f.polls=0;if(options.header)options.header(driver,f);driver.receive(Uint8Array.from(options.ack||[0xa5,0x20,3,0,0,0]));}
  else if(p[0]===3){assert.equal(p[1]|p[2]<<8,f.lines);assert.ok(p.length<=transport.maxPayload);assert.equal(p.length,4+p[3]*12);f.lines+=p[3];}
 }};
 driver=new wp.Driver(transport,{requestMs:options.requestMs||20,flowMs:200,doneMs:4000,now:()=>now,sleep:async ms=>{now+=ms;if(options.sleep)await options.sleep(ms,f,driver,transport);}});
 f.driver=driver;f.transport=transport;
 f.hooks={persist:value=>{if(options.persist&&options.persist(value,f)===false)return false;f.unknown=value;f.persisted.push(value);if(!value)assert.equal(f.finished,options.copies||1);return true;},abort:()=>{f.aborts++;transport.connected=false;},progress:p=>f.progress.push(p)};
 f.run=async extra=>{await driver.probe();await wp.run(driver,rows,{...opts,...extra},f.hooks);};return f;
}
test('documented gap header golden bytes',()=>assert.equal(Buffer.from(wp.header(240,opts)).toString('hex'),'02f00000000002201e5501'));
test('continuous header omits gap and copy bytes',()=>assert.equal(Buffer.from(wp.header(240,{...opts,paper:'continuous'})).toString('hex'),'02f0000000000120'));
test('rotation, MSB black packing, white and alpha',()=>{
 const p=new Uint8Array(80*96*4).fill(255);for(const [x,y] of [[79,0],[79,7],[79,8],[0,95]]){const n=(y*80+x)*4;p[n]=p[n+1]=p[n+2]=0;}
 p[0]=p[1]=p[2]=p[3]=0;const r=wp.raster(p,80,96);assert.equal(r[0],0x81);assert.equal(r[1],0x80);assert.equal(r[79*12+11],1);assert.equal(r.reduce((s,x)=>s+(x!==0),0),3);
});
test('raster dimensions cannot silently mismatch',()=>assert.throws(()=>wp.raster(new Uint8Array(240*96*4),240,95)));
test('media and header bounds',()=>{for(const v of [{length:0},{length:30.5},{length:101},{gap:0},{gap:25.6},{gap:1.11},{density:0},{paper:'guess'},{copies:21},{copies:1.5}])assert.throws(()=>wp.media({...opts,...v}));assert.throws(()=>wp.header(239,opts));});
test('status refuses truncated or different print head',()=>{assert.throws(()=>wp.info(Uint8Array.of(1)));const s=status();s[3]=128;assert.throws(()=>wp.info(s));});
test('every error flag blocks readiness',()=>{for(let i=6;i<10;i++){const s=status();s[i]=1;assert.throws(()=>wp.ready(wp.info(s)));}assert.throws(()=>wp.ready(wp.info(status(1))));});
test('one row at MTU23, four at MTU185, tail exact',()=>{assert.equal(wp.packet(rows,0,20).length,16);assert.equal(wp.packet(rows,0,182).length,52);assert.equal(wp.packet(rows,239,182)[3],1);assert.throws(()=>wp.packet(rows,240,20));});
test('success only after busy to idle; default MTU and no replay',async()=>{const f=fixture();await f.run();assert.deepEqual(f.persisted,[true,false]);assert.equal(f.polls,3);assert.equal(f.headers,1);assert.equal(f.writes.filter(x=>x[0]===3).length,60);assert.equal(f.aborts,0);});
test('MTU23 sends 240 whole single-row commands',async()=>{const f=fixture({maximum:20});await f.run();assert.equal(f.writes.filter(x=>x[0]===3).length,240);});
test('copies are separate confirmed jobs',async()=>{const f=fixture({copies:2});await f.run({copies:2});assert.equal(f.headers,2);assert.equal(f.finished,2);assert.deepEqual(f.progress.filter(x=>x.phase==='done').map(x=>x.copy),[1,2]);});
test('pause during header survives ACK until resume',async()=>{
 let waits=0;const f=fixture({header:d=>d.receive(Uint8Array.of(0xa7)),sleep:(ms,f,d)=>{if(d.pause){assert.equal(f.lines,0);if(ms===50&&++waits===3)d.receive(Uint8Array.of(0xa8));}}});await f.run();assert.equal(waits,3);
});
test('mid-stream pause stops raster until resume',async()=>{
 let resumed=false;const f=fixture({write:(p,f,d)=>{if(p[0]===3&&(p[1]|p[2]<<8)===4)d.receive(Uint8Array.of(0xa7));if(p[0]===3&&(p[1]|p[2]<<8)>4)assert.ok(resumed);},sleep:(ms,f,d)=>{if(ms===50){assert.equal(f.lines,8);resumed=true;d.receive(Uint8Array.of(0xa8));}}});await f.run();assert.ok(resumed);
});
test('stalled buffer leaves unknown, no raster and no second copy',async()=>{const f=fixture({header:d=>d.receive(Uint8Array.of(0xa7))});await assert.rejects(f.run({copies:2}),/буфер/);assert.deepEqual(f.persisted,[true]);assert.equal(f.headers,1);assert.equal(f.lines,0);assert.equal(f.aborts,1);});
test('resume ACK is not replayed',async()=>{for(const ack of [[0xa4,0,0,0],[0xa5,0,0,4,0,0],[0xa5,0,0,0,0,1],[0xa5]]){const f=fixture({ack});await assert.rejects(f.run(),/возобновления/);assert.equal(f.lines,0);assert.equal(f.headers,1);assert.ok(f.unknown);}});
test('paper error after data keeps unknown',async()=>{const f=fixture({status:(s,f)=>status(s,f.lines===240?1:0)});await assert.rejects(f.run({copies:2}),/ошибку/);assert.equal(f.headers,1);assert.ok(f.unknown);});
test('busy timeout keeps unknown, no retry',async()=>{const f=fixture({busyForever:true});await assert.rejects(f.run(),/завершение/);assert.equal(f.headers,1);assert.ok(f.unknown);});
test('failed pending persistence sends no reset/header/raster',async()=>{const f=fixture({persist:()=>false});await assert.rejects(f.run(),/сохранить/);assert.ok(f.writes.every(x=>[1,0xb2].includes(x[0])));});
test('failed completion persistence leaves unknown',async()=>{const f=fixture({persist:value=>value});await assert.rejects(f.run(),/состояние не сохранено/);assert.ok(f.unknown);assert.equal(f.finished,1);});
test('unsupported firmware sends only read queries',async()=>{const f=fixture({fw:0x51});await assert.rejects(f.run(),/V51/);assert.ok(f.writes.every(x=>[1,0xb2].includes(x[0])));assert.deepEqual(f.persisted,[]);});
test('busy preflight does not cancel another print',async()=>{const f=fixture({status:()=>status(1)});await assert.rejects(f.run(),/занят/);assert.ok(f.writes.every(x=>[1,0xb2].includes(x[0])));assert.deepEqual(f.persisted,[]);});
test('disconnect after first packet stops additional copies',async()=>{const f=fixture({write:(p,f,d,t)=>{if(p[0]===3){t.connected=false;d.stop(Error('test disconnect'));}}});await assert.rejects(f.run({copies:2}),/disconnect/);assert.equal(f.writes.filter(x=>x[0]===3).length,1);assert.equal(f.headers,1);assert.ok(f.unknown);});
test('missing ACK times out without retry',async()=>{const f=fixture({silent:2});await assert.rejects(f.run(),/не ответил/);assert.equal(f.headers,0);assert.equal(f.writes.filter(x=>x[0]===2).length,1);assert.ok(f.unknown);});
test('malformed flow signal stops job',async()=>{const f=fixture({header:d=>d.receive(Uint8Array.of(0xa7,1))});await assert.rejects(f.run(),/управления потоком/);assert.equal(f.lines,0);});
test('deadline aborts sleeping task, late continuation cannot write',async()=>{
 const f=fixture({sleep:ms=>new Promise(resolve=>setTimeout(resolve,ms===300?40:0))});f.hooks.timeoutMs=5;await assert.rejects(f.run(),/время задания/);const count=f.writes.length;await new Promise(r=>setTimeout(r,55));assert.equal(f.writes.length,count);assert.ok(f.unknown);assert.equal(f.headers,0);
});
test('transport failure before ACK cannot become success',async()=>{const f=fixture({write:p=>{if(p[0]===2)throw Error('test write failure');}});await assert.rejects(f.run(),/write failure/);assert.ok(f.unknown);assert.equal(f.finished,0);});
test('unsolicited error while streaming stops transmission',async()=>{const f=fixture({write:(p,f,d)=>{if(p[0]===3)d.receive(status(1,1));}});await assert.rejects(f.run(),/ошибку/);assert.equal(f.writes.filter(x=>x[0]===3).length,1);});
(async()=>{for(const [name,fn]of tests){await fn();console.log('PASS '+name);}console.log('PASS '+tests.length+' WP9529 protocol scenarios (transport simulated; no physical print).');})().catch(e=>{console.error(e);process.exitCode=1;});
