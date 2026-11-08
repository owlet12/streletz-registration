// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;

/** One transform for painting, focus, placement and marker hit tests. */
final class FloorPlanTransform {
    float width,height,imageWidth,imageHeight,base=1,zoom=1,left,top;
    void resize(float w,float h,float iw,float ih){
        if(w<=0||h<=0||iw<=0||ih<=0)return;
        float cx=width>0?imageX(width/2):.5f,cy=height>0?imageY(height/2):.5f;
        width=w;height=h;imageWidth=iw;imageHeight=ih;base=Math.min(w/iw,h/ih);
        left=w/2-cx*drawWidth();top=h/2-cy*drawHeight();clamp();
    }
    float drawWidth(){return imageWidth*base*zoom;}
    float drawHeight(){return imageHeight*base*zoom;}
    float screenX(float x){return left+x*drawWidth();}
    float screenY(float y){return top+y*drawHeight();}
    float imageX(float x){return (x-left)/drawWidth();}
    float imageY(float y){return (y-top)/drawHeight();}
    boolean contains(float x,float y){return drawWidth()>0&&drawHeight()>0&&x>=left&&x<=left+drawWidth()&&y>=top&&y<=top+drawHeight();}
    void reset(){zoom=1;left=(width-drawWidth())/2;top=(height-drawHeight())/2;clamp();}
    void scale(float factor,float x,float y){
        if(!Float.isFinite(factor)||factor<=0)return;
        float next=Math.max(1,Math.min(8,zoom*factor)),ratio=next/zoom;
        left=x-(x-left)*ratio;top=y-(y-top)*ratio;zoom=next;clamp();
    }
    void pan(float x,float y){left+=x;top+=y;clamp();}
    void focus(float x,float y){zoom=3;left=width/2-x*drawWidth();top=height/2-y*drawHeight();clamp();}
    private void clamp(){left=drawWidth()<=width?(width-drawWidth())/2:Math.max(width-drawWidth(),Math.min(0,left));top=drawHeight()<=height?(height-drawHeight())/2:Math.max(height-drawHeight(),Math.min(0,top));}
}
