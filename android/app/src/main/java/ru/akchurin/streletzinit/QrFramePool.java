// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;

/** Two owned luminance buffers. Busy decoders retain ownership across camera close/reopen. */
final class QrFramePool {
    static final class Frame { byte[] data; int width,height; private boolean busy; }
    private final Frame[] frames={new Frame(),new Frame()};
    synchronized Frame crop(byte[] source,int width,int height,ScannerGeometry.Crop crop) {
        if(source==null||(long)width*height>source.length||width<=0||height<=0
                ||crop.left<0||crop.top<0||crop.width<=0||crop.height<=0
                ||(long)crop.left+crop.width>width||(long)crop.top+crop.height>height)throw new IllegalArgumentException("Invalid preview crop");
        Frame frame=null;for(Frame item:frames)if(!item.busy){frame=item;break;}if(frame==null)return null;
        int length=Math.multiplyExact(crop.width,crop.height);
        if(frame.data==null||frame.data.length<length)frame.data=new byte[length];
        frame.width=crop.width;frame.height=crop.height;
        for(int y=0;y<crop.height;y++)System.arraycopy(source,(crop.top+y)*width+crop.left,frame.data,y*crop.width,crop.width);
        frame.busy=true;return frame;
    }
    synchronized void release(Frame frame) {if(frame==frames[0]||frame==frames[1])frame.busy=false;}
}
