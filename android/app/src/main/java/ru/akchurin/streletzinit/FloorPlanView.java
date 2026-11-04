// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;
import android.content.Context;
import android.graphics.*;
import android.view.*;
import org.json.*;
import java.util.*;

/** Normalized image coordinates; hit-testing uses the same fit transform as rendering. */
final class FloorPlanView extends View {
    interface Listener {void tap(String recordId,float x,float y);}
    final Bitmap image;final JSONArray points;final Map<String,String> labels;final Map<String,Integer> states;final Listener listener;
    final Paint paint=new Paint(Paint.ANTI_ALIAS_FLAG);final RectF area=new RectF();
    FloorPlanView(Context c,Bitmap image,JSONArray points,Map<String,String> labels,Map<String,Integer> states,Listener listener){super(c);this.image=image;this.points=points;this.labels=labels;this.states=states;this.listener=listener;setContentDescription("План помещений. Нажмите значок для карточки; свободное место — для размещения устройства.");}
    protected void onDraw(Canvas canvas){super.onDraw(canvas);float scale=Math.min(getWidth()/(float)image.getWidth(),getHeight()/(float)image.getHeight());float w=image.getWidth()*scale,h=image.getHeight()*scale;area.set((getWidth()-w)/2,(getHeight()-h)/2,(getWidth()+w)/2,(getHeight()+h)/2);canvas.drawColor(Color.rgb(240,242,245));canvas.drawBitmap(image,null,area,paint);
        float radius=12*getResources().getDisplayMetrics().density;
        for(int i=0;i<points.length();i++){JSONObject p=points.optJSONObject(i);if(p==null)continue;String id=p.optString("recordId");int state=states.getOrDefault(id,0);float x=area.left+(float)p.optDouble("x")*area.width(),y=area.top+(float)p.optDouble("y")*area.height();paint.setColor(state==2?Color.rgb(28,125,65):state==1?Color.rgb(174,99,0):Color.rgb(75,85,99));canvas.drawCircle(x,y,radius,paint);paint.setColor(Color.WHITE);paint.setTextSize(radius);paint.setTextAlign(Paint.Align.CENTER);canvas.drawText(state==2?"✓":state==1?"?":"–",x,y+radius/3,paint);}
    }
    public boolean onTouchEvent(MotionEvent e){if(e.getAction()!=MotionEvent.ACTION_UP)return true;if(!area.contains(e.getX(),e.getY()))return true;float x=(e.getX()-area.left)/area.width(),y=(e.getY()-area.top)/area.height();String hit="";double best=Math.pow(22*getResources().getDisplayMetrics().density,2);for(int i=0;i<points.length();i++){JSONObject p=points.optJSONObject(i);if(p==null)continue;double dx=(x-p.optDouble("x"))*area.width(),dy=(y-p.optDouble("y"))*area.height(),d=dx*dx+dy*dy;if(d<best){best=d;hit=p.optString("recordId");}}performClick();listener.tap(hit,x,y);return true;}
    public boolean performClick(){super.performClick();return true;}
}
