// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
(()=>{'use strict';
const $=id=>document.getElementById(id),lib=niimbluelib,core=LabelPrintCore;
let sequence=0,client=null,busy=false,printing=false,unknown=false,prepared=null,detected=null,job=0,profile='d110',printerName='',descriptionLoaded=false,descriptionEdited=false;
const pending=new Map();
const isWp=()=>['wp9529','wp9539'].includes(profile);
const wpProfile=()=>WP9529.profile(profile);
function rpc(action,payload=''){return new Promise((resolve,reject)=>{const id=++sequence;const timer=setTimeout(()=>{pending.delete(id);if(action==='prepare')rpc('disconnect').catch(()=>{});reject(Error('Телефон не завершил операцию Bluetooth'));},action==='write'?7000:action==='prepare'?125000:40000);pending.set(id,{resolve,reject,timer});LabelNative.request(id,action,payload);});}
window.nativeReply=(id,error,result)=>{const item=pending.get(id);if(!item)return;pending.delete(id);clearTimeout(item.timer);error?item.reject(Error(error)):item.resolve(result);};
function bytes(value){return Uint8Array.from(atob(value),c=>c.charCodeAt(0));}
class AndroidClient extends lib.NiimbotAbstractClient {
 constructor(){super();this.connected=false;this.setHeartbeatAutoStart(false);}
 isConnected(){return this.connected;}
 getType(){return 'bluetooth';}
 async attach(result){if(result.profile!=='d110')throw Error('Модель принтера не подтверждена');this.connected=true;try{await this.negotiateAndGetPrinterInfo(()=>this.disconnect());return this.getPrinterInfo();}catch(e){await this.disconnect();throw e;}}
 async disconnect(){this.stopHeartbeat();this.connected=false;await rpc('disconnect');}
 async sendRaw(data,force){const send=async()=>{if(!this.connected)throw Error('Нет связи с принтером');await lib.Utils.sleep(this.packetIntervalMs);await rpc('write',btoa(String.fromCharCode(...data)));};if(force)await send();else await this.mutex.runExclusive(send);}
 // Subscribe before BLE write: Android can deliver a reply before the write callback.
 async sendPacketWaitResponse(packet,timeout=1000){return this.mutex.runExclusive(async()=>{
  if(packet.oneWay){await this.sendPacket(packet,true);return new lib.NiimbotPacket(lib.ResponseCommandId.In_Invalid,[]);}
  const reply=this.waitForPacket(packet.validResponseIds,true,timeout);reply.catch(()=>{});await this.sendPacket(packet,true);return reply;
 });}
}
class WPClient {
 constructor(id){this.profile=id;this.model=WP9529.profile(id);this.connected=false;this.maxPayload=20;this.driver=new WP9529.Driver(this,{profile:id});}
 async attach(result){
  if(result.profile!==this.profile||!WP9529.matches(this.profile,result.name)||!Number.isInteger(result.maxPayload)||result.maxPayload<20)throw Error('Подключение не соответствует выбранному '+this.model.name+'.');
  this.maxPayload=result.maxPayload;this.connected=true;return this.driver.probe();
 }
 async write(data){await rpc('write',btoa(String.fromCharCode(...data)));}
 async disconnect(){this.connected=false;this.driver.stop(Error('Связь с VSON закрыта.'));await rpc('disconnect');}
 processRawPacket(data){this.driver.receive(data);}
}
window.onBleData=value=>{if(client?.connected)try{client.processRawPacket(bytes(value));}catch(e){status('Некорректный ответ принтера: '+e.message);}};
window.onBleDisconnected=()=>{if(client){client.connected=false;if(client instanceof WPClient)client.driver.stop(Error('Связь с VSON потеряна.'));else client.emit('disconnect',new lib.DisconnectEvent());}detected=null;if(unknown){status('Связь потеряна. Результат печати не подтверждён. Проверьте этикетку.');}else status('Принтер отключён. При следующей печати подключение восстановится.');controls();};
function status(value){$('status').textContent=value;}
function controls(){for(const id of ['description','width','height','copies','rotation','paper','gap','density','fontSize','fontAuto','fontMinus','fontPlus'])$(id).disabled=busy||printing;$('print').disabled=busy||printing||unknown||!prepared;$('review').hidden=!unknown;$('review').disabled=printing||busy;$('height').disabled=busy||printing||isWp();$('gap').disabled=busy||printing||$('paper').value==='continuous';$('fontMinus').disabled=busy||printing||Number($('fontSize').value)<=3;$('fontPlus').disabled=busy||printing||Number($('fontSize').value)>=36;}
function paint(){prepared=null;try{
 const options=core.options({width:$('width').value,height:$('height').value,copies:$('copies').value,rotate:$('rotation').value==='90'});
 const wp=isWp()?WP9529.media({length:options.width,paper:$('paper').value,gap:$('gap').value,density:$('density').value,copies:options.copies}):null;
 const font=core.font({auto:$('fontAuto').checked,points:$('fontSize').value});
 const value=core.text($('description').value),canvas=$('preview');canvas.width=wp?options.width*8:Math.round(options.width*203/25.4);canvas.height=wp?96:Math.round(options.height*203/25.4);
 const ctx=canvas.getContext('2d');ctx.fillStyle='white';ctx.fillRect(0,0,canvas.width,canvas.height);ctx.fillStyle='black';ctx.save();
 if(options.rotate){ctx.translate(canvas.width,0);ctx.rotate(Math.PI/2);}
 const width=options.rotate?canvas.height:canvas.width,height=options.rotate?canvas.width:canvas.height,margin=5;
 const layout=core.layout(value,width-margin*2,height-margin*2,(s,size)=>{ctx.font=`bold ${size}px sans-serif`;return ctx.measureText(s).width;},font);
 if(font.auto)$('fontSize').value=String(layout.points);
 ctx.font=`bold ${layout.size}px sans-serif`;ctx.textAlign='center';ctx.textBaseline='middle';
 let y=(height-layout.lines.length*layout.lineHeight)/2+layout.lineHeight/2;
 for(const line of layout.lines){ctx.fillText(line,width/2,y);y+=layout.lineHeight;}ctx.restore();
 const pixels=ctx.getImageData(0,0,canvas.width,canvas.height);for(let n=0;n<pixels.data.length;n+=4){const value=pixels.data[n]<160?0:255;pixels.data[n]=pixels.data[n+1]=pixels.data[n+2]=value;pixels.data[n+3]=255;}ctx.putImageData(pixels,0,0);
 $('previewNote').textContent=`${options.height}×${options.width} мм · ${options.copies} экз. · ${layout.points} пт · перенос строк автоматический`;
 prepared={canvas,options,wp};
 }catch(e){const c=$('preview'),ctx=c.getContext('2d');ctx.setTransform(1,0,0,1,0,0);ctx.fillStyle='white';ctx.fillRect(0,0,c.width,c.height);$('previewNote').textContent=e.message;}controls();}
window.setDescription=value=>{if(descriptionLoaded||descriptionEdited)return;descriptionLoaded=true;$('description').value=String(value??'');paint();};
$('description').oninput=()=>{descriptionEdited=true;paint();};
window.onPrintScreenStopped=()=>{job++;if(client)client.connected=false;detected=null;};
for(const id of ['width','height','copies','rotation','paper','gap','density'])$(id).oninput=paint;
function saveFont(){
 try{const f=core.font({auto:$('fontAuto').checked,points:$('fontSize').value});if(!LabelNative.saveTextConfig(JSON.stringify({fontAuto:f.auto,fontSize:f.points})))status('Не удалось сохранить размер шрифта.');}catch{}
}
$('fontSize').oninput=()=>{$('fontAuto').checked=false;paint();};
$('fontSize').onchange=saveFont;
$('fontAuto').onchange=()=>{if($('fontAuto').checked){const size=Number($('fontSize').value);if(!Number.isInteger(size)||size<3||size>36)$('fontSize').value='13';}paint();saveFont();};
for(const [id,delta] of [['fontMinus',-1],['fontPlus',1]])$(id).onclick=()=>{if(busy||printing)return;const value=Number($('fontSize').value);$('fontSize').value=String(Math.min(36,Math.max(3,(Number.isFinite(value)?value:13)+delta)));$('fontAuto').checked=false;paint();saveFont();};
function profileUi(){
 $('wpMedia').hidden=!isWp();if(isWp())$('height').value='12';paint();
}
function checkJob(current){if(current!==job)throw Error('Операция отменена: экран печати закрыт.');}
async function ensureConnected(current,choose=false){
 if(!choose&&client?.connected&&detected)return;
 client=null;detected=null;status(choose?'Выберите новый принтер…':'Подключение принтера…');
 try{
  const result=await rpc('prepare',choose?'choose':'');checkJob(current);
  if(!result||!['wp9529','wp9539','d110'].includes(result.profile))throw Error('Модель принтера не подтверждена');
  const active=result.profile==='d110'?new AndroidClient():new WPClient(result.profile);client=active;
  const info=await active.attach(result);checkJob(current);
  let next;
  if(active instanceof WPClient){WP9529.ready(info.status);next={head:96};}
  else {next=core.profile(info,active.getModelMetadata(),active.getPrintTaskType());active.on('printprogress',event=>{if(printing)status(`Печать: ${event.page} / ${event.pagesTotal} · ${event.pagePrintProgress}%`);});}
  await rpc('remember');checkJob(current);
  profile=result.profile;printerName=result.name||profile.toUpperCase();detected=next;profileUi();
 }catch(e){detected=null;try{if(client)await client.disconnect();else await rpc('disconnect');}catch{}throw e;}
}
async function changePrinter(){
 if(busy||printing)return;const current=++job;busy=true;controls();LabelNative.setPrinting(true);
 try{await ensureConnected(current,true);status('Принтер сохранён: '+printerName+'. Нажмите «Печатать».');}
 catch(e){status(e.message);}finally{busy=false;LabelNative.setPrinting(false);controls();}
}
let holdTimer=null,suppressClick=false;
function clearHold(){clearTimeout(holdTimer);holdTimer=null;}
$('print').onpointerdown=event=>{if(event.button!==0||busy||printing)return;clearHold();suppressClick=false;holdTimer=setTimeout(()=>{suppressClick=true;changePrinter();},650);};
for(const event of ['pointerup','pointercancel','pointerleave'])$('print').addEventListener(event,clearHold);
$('print').oncontextmenu=event=>event.preventDefault();
$('print').onkeydown=event=>{if(event.key==='ContextMenu'||event.shiftKey&&event.key==='F10'){event.preventDefault();changePrinter();}};
$('print').onclick=async()=>{if(suppressClick){suppressClick=false;return;}if(busy||printing||unknown||!prepared)return;
 const current=++job;busy=true;controls();LabelNative.setPrinting(true);let task;
 try{
  await ensureConnected(current);checkJob(current);paint();if(!prepared)throw Error($('previewNote').textContent);
  const {canvas,options,wp}=prepared,selected=detected,active=client;
  if(!LabelNative.saveConfig(options.width,options.height,options.copies)||!LabelNative.savePrinterConfig(JSON.stringify({profile,paper:$('paper').value,gap:isWp()&&$('paper').value==='gap'?Number($('gap').value):8.5,density:Number($('density').value)})))throw Error('Не удалось сохранить параметры печати.');
  if(!LabelNative.saveTextConfig(JSON.stringify({fontAuto:$('fontAuto').checked,fontSize:Number($('fontSize').value)})))throw Error('Не удалось сохранить размер шрифта.');
  const hooks={persist:value=>{const ok=LabelNative.pending(value);if(ok)unknown=value;return ok;},abort:()=>{active.disconnect().catch(()=>{});}};
  if(wp){
   const pixels=canvas.getContext('2d').getImageData(0,0,canvas.width,canvas.height);
   const rows=WP9529.raster(pixels.data,canvas.width,canvas.height);
   printing=true;busy=false;controls();status('Проверка '+active.model.name+' перед печатью…');
   await WP9529.run(active.driver,rows,wp,{...hooks,progress:p=>status(p.phase==='sending'?`Передача: ${p.percent}% · копия ${p.copy} / ${p.copies}`:p.phase==='waiting'?`Ожидание завершения · копия ${p.copy} / ${p.copies}`:`Подтверждено копий: ${p.copy} / ${p.copies}`)});
  }else{
  core.profile(client.getPrinterInfo(),client.getModelMetadata(),client.getPrintTaskType());
  if(canvas.height>selected.head)throw Error('Этикетка шире печатающей головки D110.');
  const image=lib.ImageEncoder.encodeCanvas(canvas,lib.PageColorType.SingleColor,selected.direction);
  task=client.protocol.newPrintTask(selected.task,{totalPages:options.copies,statusPollIntervalMs:200,statusTimeoutMs:15000,pageColor:lib.PageColorType.SingleColor,density:selected.density});
  printing=true;busy=false;controls();status('Передача этикетки на принтер…');
  await core.run(task,image,options.copies,hooks);}
  if(job===current){unknown=false;status('Принтер подтвердил завершение. Проверьте отпечаток.');}
 }catch(e){status((unknown?'Результат печати не подтверждён. Проверьте этикетку. ':'')+e.message);}finally{printing=false;busy=false;LabelNative.setPrinting(false);controls();}};
$('review').onclick=()=>{if(printing)return;if(LabelNative.pending(false)){unknown=false;status('Можно начать новое задание вручную.');controls();}else status('Не удалось сохранить состояние.');};
try{const config=JSON.parse(LabelNative.config());for(const id of ['width','height','copies','paper','gap','density','fontSize'])if(config[id]!=null)$(id).value=config[id];if(['d110','wp9529','wp9539'].includes(config.profile))profile=config.profile;printerName=String(config.printerName||'');$('fontAuto').checked=config.fontAuto!==false;unknown=!!config.pending;if(unknown)status('Предыдущее задание не подтверждено. Проверьте этикетку перед новой печатью.');}catch(e){status('Не удалось загрузить параметры печати.');}
profileUi();
})();
