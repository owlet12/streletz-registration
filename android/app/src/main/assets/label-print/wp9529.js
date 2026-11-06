// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
// Protocol adaptation of amahlaka/WP9529, Copyright (c) 2026 Arttu Mahlakaarto.
// MIT; see WP9529-LICENSE.txt. Upstream: 5096f206ac20103089a512204425ccccdc9c802e.
(function(root){'use strict';
const sleep=ms=>new Promise(resolve=>setTimeout(resolve,ms));
const fail=message=>{throw Error(message);};
// Shared wire format; keep model identity, firmware and feed policy explicit.
const profiles=Object.freeze({
 wp9529:Object.freeze({id:'wp9529',name:'WP9529',prefix:'9529#',firmware:'V50',feed:0}),
 wp9539:Object.freeze({id:'wp9539',name:'WP9539',prefix:'9539#',firmware:'V31',feed:32})
});
function profile(id='wp9529'){if(!Object.hasOwn(profiles,id))fail('Неизвестный профиль VSON.');return profiles[id];}
function matches(id,name){const p=profile(id);return typeof name==='string'&&name.startsWith(p.prefix)&&name.length>p.prefix.length;}
function firmware(id,value){const p=profile(id);if(value!==p.firmware)fail(p.name+': обнаружена '+(value||'неизвестная прошивка')+'. Профиль рассчитан на '+p.firmware+'.');}

function info(raw){
 if(!(raw instanceof Uint8Array)||raw.length<10||raw[0]!==1)fail('Неполный ответ состояния VSON.');
 const result={widthMm:raw[1]|raw[2]<<8,widthDots:raw[3]|raw[4]<<8,state:raw[5],errors:Array.from(raw.slice(6,10))};
 if(result.widthMm!==12||result.widthDots!==96)fail('Принтер не подтвердил головку VSON: 12 мм / 96 точек.');
 return result;
}
function ready(value){
 if(value.errors.some(Boolean))fail('VSON сообщил ошибку ('+value.errors.join(', ')+'). Проверьте бумагу, крышку и температуру головки.');
 if(value.state!==0)fail('VSON занят. Дождитесь окончания текущей печати.');
 return value;
}
function media(value){
 const length=Number(value.length),gap=Number(value.gap),copies=Number(value.copies),density=Number(value.density);
 if(!Number.isInteger(length)||length<10||length>100)fail('Длина этикетки VSON: от 10 до 100 мм.');
 if(!['gap','continuous'].includes(value.paper))fail('Выберите тип ленты VSON.');
 if(value.paper==='gap'&&(!Number.isFinite(gap)||gap<0.1||gap>25.5||Math.abs(gap*10-Math.round(gap*10))>1e-6))fail('Зазор между этикетками: 0,1–25,5 мм, шаг 0,1 мм.');
 if(!Number.isInteger(copies)||copies<1||copies>20)fail('Количество копий: от 1 до 20.');
 if(![24,32,39].includes(density))fail('Некорректная плотность печати VSON.');
 return {length,gap:value.paper==='gap'?gap:0,copies,density,paper:value.paper};
}
function raster(rgba,width,height){
 // Preview is landscape; rotate 90 degrees CCW so its right edge prints first.
 if(!Number.isInteger(width)||width<80||width>800||height!==96||rgba.length!==width*height*4)fail('Для VSON нужен растр шириной 96 точек.');
 const rows=new Uint8Array(width*12);
 for(let y=0;y<width;y++)for(let x=0;x<96;x++){
  const i=(x*width+(width-1-y))*4;
  if(rgba[i+3]>=128&&rgba[i]*.299+rgba[i+1]*.587+rgba[i+2]*.114<128)rows[y*12+(x>>3)]|=0x80>>(x&7);
 }
 return rows;
}
function header(lines,options,id='wp9529'){
 const o=media(options);
 if(lines!==o.length*8+(o.paper==='continuous'?profile(id).feed:0))fail('Высота растра не совпадает с длиной установленной этикетки.');
 const result=[2,lines&255,lines>>8,0,0,0,o.paper==='gap'?2:1,o.density];
 if(o.paper==='gap')result.push(o.length,Math.round(o.gap*10),1);
 return Uint8Array.from(result);
}
function packet(rows,start,maximum){
 if(!(rows instanceof Uint8Array)||rows.length%12||!Number.isInteger(start)||start<0||start>=rows.length/12||!Number.isInteger(maximum)||maximum<20)fail('Некорректный пакет VSON.');
 const count=Math.min(4,Math.floor((maximum-4)/12),rows.length/12-start);
 const out=new Uint8Array(4+count*12);out.set([3,start&255,start>>8,count]);out.set(rows.subarray(start*12,(start+count)*12),4);return out;
}
class Driver{
 constructor(transport,options={}){
  this.model=profile(options.profile);this.transport=transport;this.pause=false;this.failure=null;this.waiter=null;this.job=false;this.firmware=null;
  this.sleep=options.sleep||sleep;this.now=options.now||Date.now;this.requestMs=options.requestMs||5000;
  this.flowMs=options.flowMs||30000;this.doneMs=options.doneMs||60000;
 }
 check(){if(this.failure)throw this.failure;if(!this.transport.connected)fail('Связь с VSON потеряна.');}
 stop(error){this.failure=error instanceof Error?error:Error(String(error));if(this.waiter){const w=this.waiter;this.waiter=null;w.reject(this.failure);}}
 receive(raw){
  if(!(raw instanceof Uint8Array)||!raw.length){this.stop(Error('Пустой ответ VSON.'));return;}
  if(raw[0]===0xa7||raw[0]===0xa8){
   if(raw.length!==1){this.stop(Error('Некорректный ответ управления потоком VSON.'));return;}
   this.pause=raw[0]===0xa7;return;
  }
  if(this.job&&raw[0]===1){try{const s=info(raw);if(s.errors.some(Boolean))ready(s);}catch(e){this.stop(e);return;}}
  if(this.waiter&&this.waiter.opcodes.includes(raw[0])){const w=this.waiter;this.waiter=null;w.resolve(raw.slice());}
 }
 async write(raw){this.check();if(raw.length>this.transport.maxPayload)fail('Команда VSON превышает согласованный размер BLE-пакета.');await this.transport.write(raw);this.check();}
 async request(raw,opcodes){
  this.check();if(this.waiter)fail('Предыдущий запрос VSON ещё не завершён.');
  let timer;
  const response=new Promise((resolve,reject)=>{
   const w={resolve,reject,opcodes};this.waiter=w;
   timer=setTimeout(()=>{if(this.waiter===w)this.stop(Error('VSON не ответил на запрос. Повтор автоматически не выполняется.'));},this.requestMs);
  });
  response.catch(()=>{});
  try{await this.write(raw);const result=await response;this.check();return result;}
  catch(e){this.stop(e);throw e;}
  finally{clearTimeout(timer);}
 }
 async status(){return info(await this.request(Uint8Array.of(1),[1]));}
 async probe(){
  const status=await this.status(),fw=await this.request(Uint8Array.of(0xb2),[0xb2]);
  if(fw.length<2)fail('Принтер не сообщил версию прошивки.');
  this.firmware='V'+fw[1].toString(16).toUpperCase();return {status,firmware:this.firmware};
 }
 async preflight(){
  this.check();firmware(this.model.id,this.firmware);
  ready(await this.status());
 }
 async waitFlow(){const limit=this.now()+this.flowMs;while(this.pause){this.check();if(this.now()>=limit)fail('VSON не освободил буфер. Результат печати не подтверждён.');await this.sleep(50);}this.check();}
 async printRows(rows,options,onProgress=()=>{}){
  const o=media(options);header(rows.length/12,o);
  if(o.paper==='continuous'&&this.model.feed){const padded=new Uint8Array(rows.length+this.model.feed*12);padded.set(rows);rows=padded;}
  const h=header(rows.length/12,o,this.model.id);if(this.job)fail('Задание VSON уже выполняется.');
  this.job=true;
  try{
   for(let copy=1;copy<=o.copies;copy++){
    this.check();ready(await this.status());
    const reset=await this.request(Uint8Array.of(4),[4]);
    if(reset.length!==2||reset[1]!==0)fail('VSON не подтвердил подготовку задания.');
    await this.sleep(300);this.check();ready(await this.status());
    const ack=await this.request(h,[0xa4,0xa5]);
    if(ack[0]!==0xa5||ack.length<6||ack[3]!==0||ack[4]!==0||ack[5]!==0)fail('VSON запросил неподтверждённый режим возобновления. Проверьте этикетку; повтор не выполнялся.');
    if((ack[1]|ack[2]<<8)===0)fail('Принтер не подтвердил свободный буфер. Результат печати не подтверждён.');
    await this.sleep(2000);this.check();let line=0,nextPause=500;
    while(line<rows.length/12){
     await this.waitFlow();const p=packet(rows,line,this.transport.maxPayload);await this.write(p);line+=p[3];
     onProgress({copy,copies:o.copies,phase:'sending',percent:Math.floor(line*12/rows.length*100)});
     if(line>=nextPause&&line<rows.length/12){await this.sleep(2000);nextPause+=500;}
    }
    onProgress({copy,copies:o.copies,phase:'waiting',percent:100});
    await this.sleep(2000);this.check();const limit=this.now()+this.doneMs;
    while(true){
     this.check();if(this.now()>=limit)fail('VSON не подтвердил завершение печати.');
     const s=await this.status();if(s.errors.some(Boolean))ready(s);
     if(s.state===0)break;
     await this.sleep(1000);
    }
    onProgress({copy,copies:o.copies,phase:'done',percent:100});
   }
  }catch(e){this.stop(e);throw e;}finally{this.job=false;}
 }
}
async function run(driver,rows,options,hooks){
 if(!(rows instanceof Uint8Array)||rows.length%12)fail('Некорректный растр VSON.');
 const o=media(options);header(rows.length/12,o);await driver.preflight();
 if(!hooks.persist(true))fail('Не удалось сохранить состояние задания. Печать не запускалась.');
 let timer;
 const deadline=new Promise((resolve,reject)=>{timer=setTimeout(()=>{
  const error=Error('Превышено время задания VSON. Проверьте этикетки.');driver.stop(error);try{hooks.abort();}catch{}reject(error);
 // Allow each copy its own status/flow budget, including slow MTU23 transport.
 },hooks.timeoutMs||(90000+rows.length/12*50)*o.copies);});
 try{
  await Promise.race([driver.printRows(rows,o,hooks.progress),deadline]);driver.check();
  if(!hooks.persist(false))fail('Принтер закончил задание, но состояние не сохранено. Проверьте этикетку.');
 }catch(e){driver.stop(e);try{hooks.abort();}catch{}throw e;}finally{clearTimeout(timer);}
}
const api={profile,matches,firmware,info,ready,media,raster,header,packet,Driver,run};
if(typeof module!=='undefined'&&module.exports)module.exports=api;root.WP9529=api;
})(typeof globalThis!=='undefined'?globalThis:this);
