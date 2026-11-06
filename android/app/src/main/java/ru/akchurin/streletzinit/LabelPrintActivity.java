// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;

import android.Manifest;
import android.app.*;
import android.bluetooth.*;
import android.bluetooth.le.*;
import android.content.*;
import android.content.pm.PackageManager;
import android.os.*;
import android.util.Base64;
import android.webkit.*;
import org.json.*;
import java.io.*;
import java.util.*;

/** Local label UI + BLE transport only; no access to project/panel commands. */
public final class LabelPrintActivity extends Activity {
    private static final String ORIGIN="https://streletz-print.invalid/";
    private static final UUID CCCD=UUID.fromString("00002902-0000-1000-8000-00805f9b34fb");
    private final Handler ui=new Handler(Looper.getMainLooper());
    private final Map<String,BluetoothDevice> found=new LinkedHashMap<>();
    private final Map<String,String> foundNames=new LinkedHashMap<>();
    private String profile=PrinterTransportProfile.D110;
    private WebView web;
    private BluetoothAdapter adapter;
    private BluetoothLeScanner scanner;
    private BluetoothGatt gatt;
    private BluetoothGattCharacteristic channel, notifyChannel;
    private boolean destroyed, scanning, discovering;
    private int scanId=-1, connectId=-1, writeId=-1, permissionId=-1, mtu=23, offset;
    private byte[] writing;
    private String description;
    private android.content.SharedPreferences settings;

    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        description=getIntent().getStringExtra("description");
        if(description==null || description.trim().isEmpty() || description.length()>2000){finish();return;}
        settings=getSharedPreferences("label-print",MODE_PRIVATE);
        if(Build.VERSION.SDK_INT>=33)getOnBackInvokedDispatcher().registerOnBackInvokedCallback(android.window.OnBackInvokedDispatcher.PRIORITY_DEFAULT,this::onBackPressed);
        BluetoothManager manager=(BluetoothManager)getSystemService(BLUETOOTH_SERVICE);
        adapter=manager==null?null:manager.getAdapter();
        web=new WebView(this);
        WebSettings ws=web.getSettings();ws.setJavaScriptEnabled(true);ws.setAllowFileAccess(false);ws.setAllowContentAccess(false);
        ws.setAllowFileAccessFromFileURLs(false);ws.setAllowUniversalAccessFromFileURLs(false);ws.setMixedContentMode(WebSettings.MIXED_CONTENT_NEVER_ALLOW);
        ws.setSupportMultipleWindows(false);ws.setDomStorageEnabled(false);
        web.addJavascriptInterface(new NativeBridge(),"LabelNative");
        web.setWebViewClient(new WebViewClient(){
            @Override public boolean shouldOverrideUrlLoading(WebView view,WebResourceRequest request){return true;}
            @Override public WebResourceResponse shouldInterceptRequest(WebView view,WebResourceRequest request){
                String url=request.getUrl().toString();
                String name=url.startsWith(ORIGIN)?url.substring(ORIGIN.length()):"";
                if(!Arrays.asList("index.html","print.js","print-core.js","wp9529.js","niimbluelib-0.48.0.min.js").contains(name))
                    return new WebResourceResponse("text/plain","UTF-8",new ByteArrayInputStream(new byte[0]));
                try{return new WebResourceResponse(name.endsWith(".js")?"application/javascript":"text/html","UTF-8",getAssets().open("label-print/"+name));}
                catch(IOException e){return new WebResourceResponse("text/plain","UTF-8",new ByteArrayInputStream(new byte[0]));}
            }
            @Override public void onPageFinished(WebView view,String url){if(ORIGIN.concat("index.html").equals(url))js("window.setDescription("+JSONObject.quote(description)+");");}
        });
        setContentView(web);web.loadUrl(ORIGIN+"index.html");
    }

    private final class NativeBridge {
        @JavascriptInterface public String config(){
            try{return new JSONObject().put("width",settings.getInt("width",30)).put("height",settings.getInt("height",12))
                    .put("copies",settings.getInt("copies",1)).put("profile",settings.getString("profile","d110"))
                    .put("paper",settings.getString("paper","gap")).put("gap",settings.getString("gap","8.5")).put("density",settings.getInt("density",32)).put("pending",settings.getBoolean("pending",false)).toString();}
            catch(Exception e){return "{}";}
        }
        @JavascriptInterface public boolean saveConfig(int width,int height,int copies){
            if(width<10||width>100||height<6||height>12||copies<1||copies>20)return false;
            return settings.edit().putInt("width",width).putInt("height",height).putInt("copies",copies).commit();
        }
        @JavascriptInterface public boolean savePrinterConfig(String value){
            try{
                JSONObject v=new JSONObject(value);String model=PrinterTransportProfile.parse(v.getString("profile"));
                String paper=v.getString("paper");double gap=v.getDouble("gap");int density=v.getInt("density");
                if(!Arrays.asList("gap","continuous").contains(paper)||!Double.isFinite(gap)||gap<0.1||gap>25.5||!Arrays.asList(24,32,39).contains(density))return false;
                return settings.edit().putString("profile",model).putString("paper",paper).putString("gap",Double.toString(gap)).putInt("density",density).commit();
            }catch(Exception e){return false;}
        }
        @JavascriptInterface public boolean pending(boolean value){return settings.edit().putBoolean("pending",value).commit();}
        @JavascriptInterface public void setPrinting(boolean value){ui.post(()->{
            if(destroyed)return;
            if(value)getWindow().addFlags(android.view.WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
            else getWindow().clearFlags(android.view.WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        });}
        @JavascriptInterface public void request(int id,String action,String payload){
            if(id<1||action==null||payload==null||payload.length()>8192)return;
            ui.post(()->{if(!destroyed)handle(id,action,payload);});
        }
    }

    private void handle(int id,String action,String payload){
        try {
            if("disconnect".equals(action)){closeConnection("Связь закрыта");reply(id,new JSONObject(),null);return;}
            if("scan".equals(action)){
                if(scanning||connectId>=0||writeId>=0||permissionId>=0)throw new IOException("Дождитесь текущего подключения");
                profile=PrinterTransportProfile.parse(payload);
                if(!permissions()){permissionId=id;requestPermissions(Build.VERSION.SDK_INT>=31?new String[]{Manifest.permission.BLUETOOTH_SCAN,Manifest.permission.BLUETOOTH_CONNECT}:new String[]{Manifest.permission.ACCESS_FINE_LOCATION},42);return;}
                startScan(id);return;
            }
            if(!permissions())throw new IOException("Нужно разрешение Bluetooth");
            if("connect".equals(action)){connect(id,payload);return;}
            if("write".equals(action)){
                if(gatt==null||channel==null||connectId>=0||writeId>=0)throw new IOException("Принтер не подключён или занят");
                byte[] data=Base64.decode(payload,Base64.NO_WRAP);PrinterTransportProfile.validateWrite(profile,data,mtu);
                writing=data;offset=0;writeId=id;writeNext();
                ui.postDelayed(()->{if(writeId==id)closeConnection("Принтер не подтвердил передачу данных");},5000);return;
            }
            throw new IOException("Неизвестное действие");
        }catch(Exception e){if(writeId==id||connectId==id)closeConnection(safe(e));else if(scanId==id)finishScan(safe(e));else reply(id,null,safe(e));}
    }
    private boolean permissions(){
        if(Build.VERSION.SDK_INT>=31)return checkSelfPermission(Manifest.permission.BLUETOOTH_SCAN)==PackageManager.PERMISSION_GRANTED&&checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT)==PackageManager.PERMISSION_GRANTED;
        return checkSelfPermission(Manifest.permission.ACCESS_FINE_LOCATION)==PackageManager.PERMISSION_GRANTED;
    }
    @Override public void onRequestPermissionsResult(int request,String[] names,int[] grants){
        super.onRequestPermissionsResult(request,names,grants);if(request!=42)return;
        int id=permissionId;permissionId=-1;if(id<0)return;
        if(permissions())handle(id,"scan",profile);else reply(id,null,"Разрешение Bluetooth не предоставлено");
    }
    private void startScan(int id)throws Exception{
        if(adapter==null||!adapter.isEnabled())throw new IOException("Включите Bluetooth на телефоне");
        closeConnection("Выбор принтера");found.clear();foundNames.clear();scanner=adapter.getBluetoothLeScanner();
        if(scanner==null)throw new IOException("Поиск Bluetooth недоступен");
        scanning=true;scanId=id;
        scanner.startScan(null,new ScanSettings.Builder().setScanMode(ScanSettings.SCAN_MODE_LOW_LATENCY).build(),scanCallback);
        ui.postDelayed(()->{if(scanId==id)finishScan(null);},8000);
    }
    private final ScanCallback scanCallback=new ScanCallback(){
        @Override public void onScanResult(int type,ScanResult result){ui.post(()->{
            if(!scanning||found.size()>=40)return;
            try{
                BluetoothDevice device=result.getDevice();String name=device.getName();
                if(result.getScanRecord()!=null&&result.getScanRecord().getDeviceName()!=null)name=result.getScanRecord().getDeviceName();
                boolean service=result.getScanRecord()!=null&&result.getScanRecord().getServiceUuids()!=null&&result.getScanRecord().getServiceUuids().contains(new ParcelUuid(PrinterTransportProfile.NIIMBOT_SERVICE));
                if(PrinterTransportProfile.matches(profile,name,service)){found.put(device.getAddress(),device);foundNames.put(device.getAddress(),name==null?"NIIMBOT":name);}
            }catch(SecurityException ignored){}
        });}
        @Override public void onScanFailed(int error){ui.post(()->finishScan("Поиск Bluetooth завершился ошибкой "+error));}
    };
    private void finishScan(String error){
        if(!scanning)return;int id=scanId;scanId=-1;scanning=false;
        try{scanner.stopScan(scanCallback);}catch(Exception ignored){}
        JSONArray devices=new JSONArray();
        try{for(BluetoothDevice d:found.values())devices.put(new JSONObject().put("id",d.getAddress()).put("name",foundNames.get(d.getAddress())));}
        catch(Exception e){error=safe(e);}
        reply(id,devices,error);
    }
    private void connect(int id,String address)throws Exception{
        if(scanning)throw new IOException("Дождитесь окончания поиска");
        BluetoothDevice device=found.get(address);if(device==null)throw new IOException("Сначала найдите и выберите принтер");
        closeConnection("Новое подключение");connectId=id;mtu=23;discovering=false;
        gatt=device.connectGatt(this,false,callback,BluetoothDevice.TRANSPORT_LE);
        if(gatt==null){connectId=-1;throw new IOException("Не удалось подключиться");}
        ui.postDelayed(()->{if(connectId==id)closeConnection("Превышено время подключения");},18000);
    }
    private void discover(BluetoothGatt current){
        if(current!=gatt||discovering)return;discovering=true;
        if(!current.discoverServices())closeConnection("Не удалось прочитать службы принтера");
    }
    private final BluetoothGattCallback callback=new BluetoothGattCallback(){
        @Override public void onConnectionStateChange(BluetoothGatt current,int status,int state){ui.post(()->{
            if(current!=gatt)return;
            if(status!=BluetoothGatt.GATT_SUCCESS||state==BluetoothProfile.STATE_DISCONNECTED){closeConnection("Связь с принтером потеряна");return;}
            if(state==BluetoothProfile.STATE_CONNECTED){
                try{if(!current.requestMtu(185))discover(current);else ui.postDelayed(()->discover(current),1500);}catch(Exception e){closeConnection(safe(e));}
            }
        });}
        @Override public void onMtuChanged(BluetoothGatt current,int value,int status){ui.post(()->{if(current==gatt){if(status==0)mtu=Math.max(23,Math.min(value,185));discover(current);}});}
        @Override public void onServicesDiscovered(BluetoothGatt current,int status){ui.post(()->{
            if(current!=gatt)return;
            try{
                if(status!=0)throw new IOException("Не удалось прочитать службы принтера");
                BluetoothGattService service=current.getService(PrinterTransportProfile.service(profile));
                if(service==null)throw new IOException("Служба выбранной модели принтера не найдена");
                if(PrinterTransportProfile.WP9529.equals(profile)){
                    channel=service.getCharacteristic(PrinterTransportProfile.WP_WRITE);
                    notifyChannel=service.getCharacteristic(PrinterTransportProfile.WP_NOTIFY);
                    if(channel==null||(channel.getProperties()&BluetoothGattCharacteristic.PROPERTY_WRITE_NO_RESPONSE)==0)throw new IOException("Канал записи WP9529 недоступен");
                }else for(BluetoothGattCharacteristic candidate:service.getCharacteristics()){
                    int properties=candidate.getProperties();
                    if((properties&BluetoothGattCharacteristic.PROPERTY_NOTIFY)!=0&&(properties&BluetoothGattCharacteristic.PROPERTY_WRITE_NO_RESPONSE)!=0){
                        if(channel!=null)throw new IOException("Неоднозначный канал принтера");channel=candidate;
                    }
                }
                if(!PrinterTransportProfile.WP9529.equals(profile))notifyChannel=channel;
                if(notifyChannel==null||(notifyChannel.getProperties()&BluetoothGattCharacteristic.PROPERTY_NOTIFY)==0||!current.setCharacteristicNotification(notifyChannel,true))throw new IOException("Канал ответов принтера недоступен");
                BluetoothGattDescriptor descriptor=notifyChannel.getDescriptor(CCCD);if(descriptor==null)throw new IOException("Уведомления принтера недоступны");
                descriptor.setValue(BluetoothGattDescriptor.ENABLE_NOTIFICATION_VALUE);
                if(!current.writeDescriptor(descriptor))throw new IOException("Не удалось включить ответы принтера");
            }catch(Exception e){closeConnection(safe(e));}
        });}
        @Override public void onDescriptorWrite(BluetoothGatt current,BluetoothGattDescriptor descriptor,int status){ui.post(()->{
            if(current!=gatt||connectId<0||!CCCD.equals(descriptor.getUuid())||descriptor.getCharacteristic()!=notifyChannel)return;
            if(status!=0){closeConnection("Принтер отклонил уведомления");return;}
            int id=connectId;connectId=-1;
            try{reply(id,new JSONObject().put("profile",profile).put("name",foundNames.get(current.getDevice().getAddress())).put("mtu",mtu).put("maxPayload",PrinterTransportProfile.maxPayload(mtu)),null);}
            catch(Exception e){reply(id,null,safe(e));closeConnection(safe(e));}
        });}
        @Override public void onCharacteristicChanged(BluetoothGatt current,BluetoothGattCharacteristic ch){byte[] value=ch.getValue();receive(current,ch,value==null?null:value.clone());}
        @Override public void onCharacteristicChanged(BluetoothGatt current,BluetoothGattCharacteristic ch,byte[] value){receive(current,ch,value.clone());}
        @Override public void onCharacteristicWrite(BluetoothGatt current,BluetoothGattCharacteristic ch,int status){ui.post(()->{
            if(current!=gatt||ch!=channel||writeId<0)return;
            if(status!=0){closeConnection("Ошибка передачи на принтер");return;}
            int expectedWriteId=writeId;
            ui.postDelayed(()->{if(current==gatt&&writeId==expectedWriteId)try{writeNext();}catch(Exception e){closeConnection(safe(e));}},12);
        });}
    };
    private void receive(BluetoothGatt current,BluetoothGattCharacteristic ch,byte[] data){
        if(data==null)return;ui.post(()->{if(current==gatt&&ch==notifyChannel)js("window.onBleData("+JSONObject.quote(Base64.encodeToString(data,Base64.NO_WRAP))+");");});
    }
    private void writeNext()throws Exception{
        if(writing==null||writeId<0)return;
        if(offset==writing.length){int id=writeId;writeId=-1;writing=null;reply(id,new JSONObject(),null);return;}
        int count=PrinterTransportProfile.WP9529.equals(profile)?writing.length:Math.min(PrinterTransportProfile.maxPayload(mtu),writing.length-offset);
        byte[] part=Arrays.copyOfRange(writing,offset,offset+count);offset+=count;
        channel.setWriteType(BluetoothGattCharacteristic.WRITE_TYPE_NO_RESPONSE);channel.setValue(part);
        if(!gatt.writeCharacteristic(channel))throw new IOException("Принтер занят; передача остановлена");
    }
    private void closeConnection(String reason){
        BluetoothGatt previous=gatt;gatt=null;channel=null;notifyChannel=null;writing=null;discovering=false;
        if(connectId>=0){int id=connectId;connectId=-1;reply(id,null,reason);}
        if(writeId>=0){int id=writeId;writeId=-1;reply(id,null,reason);}
        if(previous!=null){try{previous.disconnect();previous.close();}catch(Exception ignored){}js("window.onBleDisconnected();");}
    }
    private void reply(int id,Object result,String error){
        if(id<0)return;js("window.nativeReply("+id+","+(error==null?"null":JSONObject.quote(error))+","+(result==null?"null":result.toString())+");");
    }
    private void js(String script){if(!destroyed&&web!=null)web.evaluateJavascript(script,null);}
    private static String safe(Exception e){String value=e.getMessage();return value==null?"Ошибка Bluetooth":value.substring(0,Math.min(240,value.length()));}
    @Override public void onBackPressed(){
        if(settings.getBoolean("pending",false))new AlertDialog.Builder(this).setMessage("Задание ещё не подтверждено. Закрыть печать? Перед повтором проверьте этикетку.")
                .setPositiveButton("Закрыть",(d,w)->finish()).setNegativeButton("Остаться",null).show();
        else super.onBackPressed();
    }
    @Override protected void onStop(){getWindow().clearFlags(android.view.WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);finishScan("Поиск остановлен");closeConnection("Экран печати закрыт");super.onStop();}
    @Override protected void onDestroy(){closeConnection("Экран печати закрыт");destroyed=true;ui.removeCallbacksAndMessages(null);if(web!=null){web.removeJavascriptInterface("LabelNative");web.destroy();}super.onDestroy();}
}
