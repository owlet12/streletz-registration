// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
namespace StreletzBridge
{
    internal sealed class PlanCanvas : Control
    {
        internal readonly PlanViewport Viewport=new PlanViewport();
        private Image image;
        private Dictionary<string,object> plan;
        private Dictionary<string,string> labels=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        private string selected="",dragRecord="";
        private Point down,last;
        private bool dragging,moved;
        private PointF? candidate;
        internal bool Placing;
        internal bool ShowLabels=true;
        internal Action<string> SelectRecord;
        internal Action<string,double,double> PlaceRecord;
        internal Action ChangedScale;
        internal Action CancelPlacement;
        internal bool HasImage{get{return image!=null;}}
        internal PlanCanvas()
        {DoubleBuffered=true;SetStyle(ControlStyles.ResizeRedraw|ControlStyles.Selectable,true);TabStop=true;BackColor=Color.FromArgb(230,237,241);}
        internal void SetPlan(Image next,Dictionary<string,object> value,IEnumerable<PlanDevice> devices)
        {
            var old=image;image=next;old?.Dispose();plan=value;labels=devices.ToDictionary(d=>d.Id,d=>d.Label,StringComparer.OrdinalIgnoreCase);
            ResetGesture();Viewport.Resize(ClientSize.Width,ClientSize.Height,image==null?1:image.Width,image==null?1:image.Height);Fit();
        }
        internal void UpdatePoints(Dictionary<string,object> value){plan=value;Invalidate();}
        internal void Select(string id){selected=id??"";Invalidate();}
        internal void Fit(){Viewport.Fit();ChangedScale?.Invoke();Invalidate();}
        internal void Zoom(double factor){Viewport.ScaleAt(factor,ClientSize.Width/2.0,ClientSize.Height/2.0);ChangedScale?.Invoke();Invalidate();}
        internal void FocusPoint(Dictionary<string,object> point)
        {if(point==null)return;Viewport.Focus(PlanPlacement.Coordinate(point,"x"),PlanPlacement.Coordinate(point,"y"));ChangedScale?.Invoke();Invalidate();}
        protected override void OnResize(EventArgs e)
        {base.OnResize(e);if(image!=null)Viewport.Resize(ClientSize.Width,ClientSize.Height,image.Width,image.Height);Invalidate();}
        private IEnumerable<Dictionary<string,object>> Points(){return (BridgeValues.Array(plan,"points")??new object[0]).Cast<Dictionary<string,object>>();}
        private PointF Position(Dictionary<string,object> p)
        {
            double x=PlanPlacement.Coordinate(p,"x"),y=PlanPlacement.Coordinate(p,"y");
            if(BridgeValues.Text(p,"recordId")==dragRecord&&candidate.HasValue){x=candidate.Value.X;y=candidate.Value.Y;}
            return new PointF((float)(Viewport.Left+x*Viewport.DrawWidth),(float)(Viewport.Top+y*Viewport.DrawHeight));
        }
        private string Hit(Point at)
        {
            string found="";double best=18*18;
            foreach(var p in Points()){var pos=Position(p);double d=(pos.X-at.X)*(pos.X-at.X)+(pos.Y-at.Y)*(pos.Y-at.Y);if(d<=best){best=d;found=BridgeValues.Text(p,"recordId");}}
            return found;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
            if(image==null){TextRenderer.DrawText(g,"Добавьте PNG/JPEG плана",Font,ClientRectangle,Color.SlateGray,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);return;}
            g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.DrawImage(image,new RectangleF((float)Viewport.Left,(float)Viewport.Top,(float)Viewport.DrawWidth,(float)Viewport.DrawHeight));
            foreach(var p in Points().OrderBy(p=>String.Equals(selected,BridgeValues.Text(p,"recordId"),StringComparison.OrdinalIgnoreCase)?1:0))
            {
                string id=BridgeValues.Text(p,"recordId");bool active=String.Equals(selected,id,StringComparison.OrdinalIgnoreCase);PointF pos=Position(p);
                if(pos.X < -150||pos.Y < -30||pos.X>Width+30||pos.Y>Height+30)continue;
                using(var brush=new SolidBrush(active?Color.FromArgb(218,113,34):Color.FromArgb(33,107,150)))using(var outline=new Pen(Color.White,3))
                {g.FillEllipse(brush,pos.X-9,pos.Y-9,18,18);g.DrawEllipse(outline,pos.X-9,pos.Y-9,18,18);}
                if(active)using(var pen=new Pen(Color.FromArgb(218,113,34),2))g.DrawEllipse(pen,pos.X-13,pos.Y-13,26,26);
                if(ShowLabels||active)
                {
                    string label; if(!labels.TryGetValue(id,out label))label="Устройство отсутствует";
                    if(label.Length>34)label=label.Substring(0,31)+"…";
                    var size=TextRenderer.MeasureText(label,Font);var box=new Rectangle((int)pos.X+13,(int)pos.Y-size.Height/2,Math.Min(size.Width+6,260),size.Height+2);
                    g.FillRectangle(Brushes.White,box);TextRenderer.DrawText(g,label,Font,box,Color.FromArgb(24,47,62),TextFormatFlags.EndEllipsis|TextFormatFlags.VerticalCenter);
                }
            }
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);if(image==null)return;Focus();if(e.Button!=MouseButtons.Left&&e.Button!=MouseButtons.Middle)return;
            down=last=e.Location;moved=false;candidate=null;dragRecord=e.Button==MouseButtons.Left?Hit(e.Location):"";
            if(dragRecord.Length>0){selected=dragRecord;SelectRecord?.Invoke(dragRecord);}
            dragging=true;Capture=true;Invalidate();
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);if(!dragging)return;
            if(Math.Abs(e.X-down.X)+Math.Abs(e.Y-down.Y)>5)moved=true;
            if(moved)
            {
                if(dragRecord.Length==0)Viewport.Pan(e.X-last.X,e.Y-last.Y);
                else{double x,y;candidate=Viewport.Map(e.X,e.Y,out x,out y)?new PointF((float)x,(float)y):(PointF?)null;}
                last=e.Location;Invalidate();
            }
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);if(!dragging)return;
            string target=dragRecord;bool wasMoved=moved;PointF? point=candidate;
            ResetGesture();
            if(target.Length>0&&wasMoved&&point.HasValue)PlaceRecord?.Invoke(target,point.Value.X,point.Value.Y);
            else if(e.Button==MouseButtons.Left&&!wasMoved&&target.Length==0&&Placing&&selected.Length>0)
            {double x,y;if(Viewport.Map(e.X,e.Y,out x,out y))PlaceRecord?.Invoke(selected,x,y);}
            Invalidate();
        }
        protected override void OnMouseCaptureChanged(EventArgs e){base.OnMouseCaptureChanged(e);if(!Capture){dragging=false;candidate=null;dragRecord="";Invalidate();}}
        private void ResetGesture(){dragging=false;candidate=null;dragRecord="";Capture=false;}
        protected override void OnMouseWheel(MouseEventArgs e)
        {base.OnMouseWheel(e);if(image==null)return;Viewport.ScaleAt(e.Delta>0?1.25:.8,e.X,e.Y);ChangedScale?.Invoke();Invalidate();}
        protected override bool ProcessCmdKey(ref Message msg,Keys keyData)
        {if(keyData==Keys.Escape){ResetGesture();CancelPlacement?.Invoke();return true;}return base.ProcessCmdKey(ref msg,keyData);}
        protected override void Dispose(bool disposing){if(disposing){image?.Dispose();image=null;}base.Dispose(disposing);}
    }
}
