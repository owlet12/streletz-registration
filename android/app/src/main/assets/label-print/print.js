// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
(()=>{'use strict';
const $=id=>document.getElementById(id),lib=niimbluelib,core=LabelPrintCore;
let sequence=0,client=null,busy=false,printing=false,unknown=false,prepared=null,detected=null,job=0;
const pending=new Map();
function rpc(action,payload=''){return new Promise((resolve,reject)=>{const id=++sequence;const timer=setTimeout(()=>{pending.delete(id);reject(Error('Телефон не завершил операцию Bluetooth'));},action==='write'?7000:40000);pending.set(id,{resolve,reject,timer});LabelNative.request(id,action,payload);});}
window.nativeReply=(id,error,result)=>{const item=pending.get(id);if(!item)return;pending.delete(id);clearTimeout(item.timer);error?item.reject(Error(error)):item.resolve(result);};
function bytes(value){return Uint8Array.from(atob(value),c=>c.charCodeAt(0));}
class AndroidClient extends lib.NiimbotAbstractClient {
 constructor(){super();this.connected=false;this.setHeartbeatAutoStart(false);}
 isConnected(){return this.connected;}
 getType(){return 'bluetooth';}
 async connect(address){await rpc('connect',address);this.connected=true;try{await this.negotiateAndGetPrinterInfo(()=>this.disconnect());return this.getPrinterInfo();}catch(e){await this.disconnect();throw e;}}
 async disconnect(){this.stopHeartbeat();this.connected=false;await rpc('disconnect');}
 async sendRaw(data,force){const send=async()=>{if(!this.connected)throw Error('Нет связи с принтером');await lib.Utils.sleep(this.packetIntervalMs);await rpc('write',btoa(String.fromCharCode(...data)));};if(force)await send();else await this.mutex.runExclusive(send);}
 // Subscribe before BLE write: Android can deliver a reply before the write callback.
 async sendPacketWaitResponse(packet,timeout=1000){return this.mutex.runExclusive(async()=>{
  if(packet.oneWay){await this.sendPacket(packet,true);return new lib.NiimbotPacket(lib.ResponseCommandId.In_Invalid,[]);}
  const reply=this.waitForPacket(packet.validResponseIds,true,timeout);reply.catch(()=>{});await this.sendPacket(packet,true);return reply;
 });}
}
window.onBleData=value=>{if(client?.connected)try{client.processRawPacket(bytes(value));}catch(e){status('Некорректный ответ принтера: '+e.message);}};
window.onBleDisconnected=()=>{if(client){client.connected=false;client.emit('disconnect',new lib.DisconnectEvent());}detected=null;if(printing||unknown){unknown=true;status('Связь потеряна. Результат печати не подтверждён. Проверьте этикетку.');}else status('Принтер отключён.');controls();};
function status(value){$('status').textContent=value;}
function controls(){for(const id of ['width','height','copies','rotation','scan','devices','connect'])$(id).disabled=busy||printing;$('connect').disabled=busy||printing||!$('devices').value;$('print').disabled=busy||printing||unknown||!prepared||!detected||!client?.connected;$('review').hidden=!unknown;$('review').disabled=printing||busy;}
function paint(){prepared=null;try{
 const options=core.options({width:$('width').value,height:$('height').value,copies:$('copies').value,rotate:$('rotation').value==='90'});
 const value=core.text($('description').value),canvas=$('preview');canvas.width=Math.round(options.width*203/25.4);canvas.height=Math.round(options.height*203/25.4);
 const ctx=canvas.getContext('2d');ctx.fillStyle='white';ctx.fillRect(0,0,canvas.width,canvas.height);ctx.fillStyle='black';ctx.save();
 if(options.rotate){ctx.translate(canvas.width,0);ctx.rotate(Math.PI/2);}
 const width=options.rotate?canvas.height:canvas.width,height=options.rotate?canvas.width:canvas.height,margin=5;
 const layout=core.layout(value,width-margin*2,height-margin*2,(s,size)=>{ctx.font=`bold ${size}px sans-serif`;return ctx.measureText(s).width;});
 ctx.font=`bold ${layout.size}px sans-serif`;ctx.textAlign='center';ctx.textBaseline='middle';
 let y=(height-layout.lines.length*layout.lineHeight)/2+layout.lineHeight/2;
 for(const line of layout.lines){ctx.fillText(line,width/2,y);y+=layout.lineHeight;}ctx.restore();
 const pixels=ctx.getImageData(0,0,canvas.width,canvas.height);for(let n=0;n<pixels.data.length;n+=4){const value=pixels.data[n]<160?0:255;pixels.data[n]=pixels.data[n+1]=pixels.data[n+2]=value;pixels.data[n+3]=255;}ctx.putImageData(pixels,0,0);
 $('previewNote').textContent=`${options.height}×${options.width} мм · ${options.copies} экз. · перенос строк автоматический`;
 prepared={canvas,options};
 }catch(e){$('previewNote').textContent=e.message;}controls();}
window.setDescription=value=>{if(printing)return;$('description').value=String(value);paint();};
for(const id of ['width','height','copies','rotation'])$(id).oninput=paint;
$('devices').onchange=controls;
$('scan').onclick=async()=>{busy=true;controls();status('Поиск D110… Включите принтер и держите его рядом.');try{if(client)await client.disconnect();client=null;detected=null;const devices=await rpc('scan');$('devices').replaceChildren();for(const d of devices){const option=document.createElement('option');option.value=d.id;option.textContent=d.name+' · '+d.id;$('devices').append(option);}status(devices.length?'Выберите принтер и нажмите «Подключить».':'Принтер не найден. Проверьте Bluetooth и питание. На Android 8–11 также включите геолокацию.');}catch(e){status(e.message);}finally{busy=false;controls();}};
$('connect').onclick=async()=>{busy=true;controls();status('Подключение и проверка модели…');try{
 if(client)await client.disconnect();detected=null;client=new AndroidClient();
 const info=await client.connect($('devices').value);detected=core.profile(info,client.getModelMetadata(),client.getPrintTaskType());
 client.on('printprogress',event=>{if(printing)status(`Печать: ${event.page} / ${event.pagesTotal} · ${event.pagePrintProgress}%`);});
 status(`D110 подключён. ПО ${info.softwareVersion??'—'}, аппаратура ${info.hardwareVersion??'—'}.`+(unknown?' Предыдущее задание не подтверждено — проверьте этикетку.':''));
 }catch(e){detected=null;try{await client?.disconnect();}catch{}status(e.message);}finally{busy=false;controls();}};
$('print').onclick=async()=>{if(busy||printing||unknown||!prepared||!detected||!client?.connected)return;
 const current=++job,{canvas,options}=prepared,selected=detected;let task;
 try{
  core.profile(client.getPrinterInfo(),client.getModelMetadata(),client.getPrintTaskType());
  if(canvas.height>selected.head)throw Error('Этикетка шире печатающей головки D110.');
  if(!LabelNative.saveConfig(options.width,options.height,options.copies))throw Error('Не удалось сохранить параметры печати.');
  const image=lib.ImageEncoder.encodeCanvas(canvas,lib.PageColorType.SingleColor,selected.direction);
  task=client.protocol.newPrintTask(selected.task,{totalPages:options.copies,statusPollIntervalMs:200,statusTimeoutMs:15000,pageColor:lib.PageColorType.SingleColor,density:selected.density});
  printing=true;controls();status('Передача этикетки на принтер…');
  await core.run(task,image,options.copies,{persist:value=>{const ok=LabelNative.pending(value);if(ok)unknown=value;return ok;},abort:()=>{client?.disconnect().catch(()=>{});}});
  if(job===current){unknown=false;status('Принтер подтвердил завершение. Проверьте отпечаток.');}
 }catch(e){status((unknown?'Результат печати не подтверждён. Проверьте этикетку. ':'')+e.message);}finally{printing=false;controls();}};
$('review').onclick=()=>{if(printing)return;if(LabelNative.pending(false)){unknown=false;status('Можно начать новое задание вручную.');controls();}else status('Не удалось сохранить состояние.');};
try{const config=JSON.parse(LabelNative.config());for(const id of ['width','height','copies'])if(config[id]!=null)$(id).value=config[id];unknown=!!config.pending;if(unknown)status('Предыдущее задание не подтверждено. Проверьте этикетку перед новой печатью.');}catch(e){status('Не удалось загрузить параметры печати.');}
controls();
})();
