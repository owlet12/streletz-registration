// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
(function(root){'use strict';
function options(value){
 const width=Number(value.width),height=Number(value.height),copies=Number(value.copies);
 if(!Number.isInteger(width)||width<10||width>100||!Number.isInteger(height)||height<6||height>12||!Number.isInteger(copies)||copies<1||copies>20)throw Error('Размер: длина 10–100 мм, ширина 6–12 мм; копий от 1 до 20.');
 return {width,height,copies,rotate:!!value.rotate};
}
function text(value){const result=String(value??'').trim();if(!result||result.length>2000)throw Error('Заполните описание (до 2000 символов).');return result;}
function profile(info,meta,task){
 if(!info||![2304,2305].includes(Number(info.modelId))||!meta||meta.model!=='D110'||meta.dpi!==203||meta.printheadPixels!==96||meta.printDirection!=='left'||!task)throw Error('Этот выпуск поддерживает NIIMBOT D110. Модель или протокол подключённого принтера не подтверждены.');
 return {task,dpi:203,head:96,direction:'left',density:2};
}
function wrap(value,measure,width){
 const result=[];
 for(const paragraph of value.replace(/\r/g,'').split('\n')){
  if(!paragraph.trim()){result.push('');continue;}
  let line='';
  for(const word of paragraph.trim().split(/\s+/)){
   if(measure((line?line+' ':'')+word)<=width){line+=(line?' ':'')+word;continue;}
   if(line){result.push(line);line='';}
   for(const character of word){if(line&&measure(line+character)>width){result.push(line);line='';}line+=character;}
  }
  if(line)result.push(line);
 }
 return result;
}
function layout(value,width,height,measure){
 for(let size=36;size>=8;size--){
  const lines=wrap(text(value),s=>measure(s,size),width),lineHeight=Math.ceil(size*1.18);
  if(lines.length*lineHeight<=height&&lines.every(s=>measure(s,size)<=width))return {lines,size,lineHeight};
 }
 throw Error('Описание не помещается на этикетке. Увеличьте длину, измените поворот или сократите текст.');
}
async function run(task,image,copies,hooks){
 if(!hooks.persist(true))throw Error('Не удалось сохранить состояние задания. Печать не запускалась.');
 let timer,aborted=false,started=false,endAttempted=false;
 const check=()=>{if(aborted)throw Error('Время печати истекло');};
 const work=(async()=>{try{
  check();started=true;await task.printInit();check();
  await task.printPage(image,copies);check();await task.waitForPageFinished();check();
  await task.waitForFinished();check();endAttempted=true;await task.printEnd();check();started=false;
 }finally{if(started&&!aborted&&!endAttempted)try{await task.printEnd();}catch{}}})();
 const deadline=new Promise((resolve,reject)=>{timer=setTimeout(()=>{aborted=true;try{hooks.abort();}catch{}reject(Error('Превышено время ожидания принтера'));},hooks.timeoutMs||60000);});
 try{await Promise.race([work,deadline]);if(!hooks.persist(false))throw Error('Принтер закончил задание, но состояние не сохранено. Проверьте этикетку.');}
 finally{clearTimeout(timer);}
}
const api={options,text,profile,wrap,layout,run};if(typeof module!=='undefined')module.exports=api;root.LabelPrintCore=api;
})(typeof globalThis!=='undefined'?globalThis:this);
