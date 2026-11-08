// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;
import android.content.Context;
import android.graphics.*;
import android.view.*;
import org.json.*;
import java.util.*;

final class FloorPlanView extends View {
    interface Listener {void tap(String recordId,float x,float y);}
    final Bitmap image;final JSONArray points;final Map<String,String> labels;final Map<String,Integer> states;final Listener listener;
    final Paint paint=new Paint(Paint.ANTI_ALIAS_FLAG|Paint.FILTER_BITMAP_FLAG);
    final FloorPlanTransform transform=new FloorPlanTransform();
    private final ScaleGestureDetector scaleDetector;private final GestureDetector gestures;
    private boolean multiTouch,placement,detached;private String focused="";private Runnable changed=()->{};
    FloorPlanView(Context c,Bitmap image,JSONArray points,Map<String,String> labels,Map<String,Integer> states,Listener listener){
        super(c);this.image=image;this.points=points;this.labels=labels;this.states=states;this.listener=listener;
        setContentDescription("План. Два пальца — масштаб, один — перемещение. Нажмите значок устройства.");
        scaleDetector=new ScaleGestureDetector(c,new ScaleGestureDetector.SimpleOnScaleGestureListener(){
            @Override public boolean onScale(ScaleGestureDetector d){transform.scale(d.getScaleFactor(),d.getFocusX(),d.getFocusY());refresh();return true;}
        });
        gestures=new GestureDetector(c,new GestureDetector.SimpleOnGestureListener(){
            @Override public boolean onDown(MotionEvent e){return true;}
            @Override public boolean onScroll(MotionEvent a,MotionEvent b,float dx,float dy){if(!scaleDetector.isInProgress()){transform.pan(-dx,-dy);refresh();}return true;}
            @Override public boolean onDoubleTap(MotionEvent e){if(!multiTouch){transform.scale(transform.zoom>=7.9f?1/transform.zoom:2,e.getX(),e.getY());refresh();}return true;}
            @Override public boolean onSingleTapConfirmed(MotionEvent e){if(!multiTouch&&!detached)tapAt(e.getX(),e.getY());return true;}
        });
    }
    void onScaleChanged(Runnable listener){changed=listener;}
    int percent(){return Math.round(transform.zoom*100);}
    void zoom(float factor){transform.scale(factor,getWidth()/2f,getHeight()/2f);refresh();}
    void fit(){transform.reset();refresh();}
    void place(boolean enabled){placement=enabled;}
    void focus(String record){focused=record==null?"":record;if(getWidth()>0)focusNow();}
    private void focusNow(){for(int i=0;i<points.length();i++){JSONObject p=points.optJSONObject(i);if(p!=null&&focused.equalsIgnoreCase(p.optString("recordId"))){transform.focus((float)p.optDouble("x"),(float)p.optDouble("y"));break;}}refresh();}
    private void refresh(){invalidate();changed.run();}
    @Override protected void onSizeChanged(int w,int h,int ow,int oh){transform.resize(w,h,image.getWidth(),image.getHeight());if(ow==0&&!focused.isEmpty())focusNow();else refresh();}
    @Override protected void onDraw(Canvas canvas){
        super.onDraw(canvas);if(image.isRecycled())return;
        canvas.drawColor(Color.rgb(235,242,245));paint.setColor(Color.WHITE);paint.setStyle(Paint.Style.FILL);
        canvas.drawBitmap(image,null,new RectF(transform.left,transform.top,transform.left+transform.drawWidth(),transform.top+transform.drawHeight()),paint);
        float radius=12*getResources().getDisplayMetrics().density;
        for(int i=0;i<points.length();i++){JSONObject p=points.optJSONObject(i);if(p==null)continue;String id=p.optString("recordId");int state=states.getOrDefault(id,0);
            float x=transform.screenX((float)p.optDouble("x")),y=transform.screenY((float)p.optDouble("y"));
            if(x < -radius||y < -radius||x>getWidth()+radius||y>getHeight()+radius)continue;
            if(id.equalsIgnoreCase(focused)){paint.setColor(Color.rgb(24,111,182));canvas.drawCircle(x,y,radius+dp(5),paint);}
            paint.setColor(Color.WHITE);canvas.drawCircle(x,y,radius+dp(2),paint);
            paint.setColor(state==2?Color.rgb(28,125,65):state==1?Color.rgb(174,99,0):Color.rgb(75,85,99));canvas.drawCircle(x,y,radius,paint);
            paint.setColor(Color.WHITE);paint.setTextSize(radius);paint.setTextAlign(Paint.Align.CENTER);canvas.drawText(state==2?"✓":state==1?"?":"–",x,y+radius/3,paint);
        }
    }
    private float dp(float n){return n*getResources().getDisplayMetrics().density;}
    private void tapAt(float x,float y){
        if(!transform.contains(x,y))return;String hit="";double best=Math.pow(dp(22),2);
        for(int i=0;i<points.length();i++){JSONObject p=points.optJSONObject(i);if(p==null)continue;double dx=x-transform.screenX((float)p.optDouble("x")),dy=y-transform.screenY((float)p.optDouble("y")),d=dx*dx+dy*dy;if(d<best){best=d;hit=p.optString("recordId");}}
        if(hit.isEmpty()&&!placement)return;performClick();listener.tap(hit,transform.imageX(x),transform.imageY(y));
    }
    @Override public boolean onTouchEvent(MotionEvent e){
        if(e.getActionMasked()==MotionEvent.ACTION_DOWN)multiTouch=false;
        if(e.getPointerCount()>1)multiTouch=true;
        if(getParent()!=null)getParent().requestDisallowInterceptTouchEvent(true);
        scaleDetector.onTouchEvent(e);gestures.onTouchEvent(e);return true;
    }
    @Override protected void onDetachedFromWindow(){detached=true;super.onDetachedFromWindow();}
    @Override public boolean performClick(){super.performClick();return true;}
}
