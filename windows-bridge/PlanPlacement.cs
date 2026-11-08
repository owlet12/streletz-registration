// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace StreletzBridge
{
    // Image-relative coordinates are shared by painting, hit testing and persisted points.
    internal sealed class PlanViewport
    {
        internal double Width, Height, ImageWidth, ImageHeight, Left, Top, Zoom=1;
        internal double DrawWidth { get { return ImageWidth*Scale; } }
        internal double DrawHeight { get { return ImageHeight*Scale; } }
        private double Scale { get { return ImageWidth<=0||ImageHeight<=0?1:Math.Min(Width/ImageWidth,Height/ImageHeight)*Zoom; } }
        internal void Resize(double width,double height,double imageWidth,double imageHeight)
        {
            double x=DrawWidth>0?(Width/2-Left)/DrawWidth:.5,y=DrawHeight>0?(Height/2-Top)/DrawHeight:.5;
            Width=Math.Max(1,width);Height=Math.Max(1,height);ImageWidth=Math.Max(1,imageWidth);ImageHeight=Math.Max(1,imageHeight);
            Left=Width/2-x*DrawWidth;Top=Height/2-y*DrawHeight;Clamp();
        }
        internal void Fit(){Zoom=1;Left=(Width-DrawWidth)/2;Top=(Height-DrawHeight)/2;Clamp();}
        internal void ScaleAt(double factor,double x,double y)
        {
            if(Double.IsNaN(factor)||Double.IsInfinity(factor)||factor<=0)return;
            double ix=(x-Left)/DrawWidth,iy=(y-Top)/DrawHeight;
            Zoom=Math.Max(1,Math.Min(8,Zoom*factor));Left=x-ix*DrawWidth;Top=y-iy*DrawHeight;Clamp();
        }
        internal void Pan(double dx,double dy){Left+=dx;Top+=dy;Clamp();}
        internal void Focus(double x,double y){Zoom=3;Left=Width/2-x*DrawWidth;Top=Height/2-y*DrawHeight;Clamp();}
        internal bool Map(double px,double py,out double x,out double y)
        {x=(px-Left)/DrawWidth;y=(py-Top)/DrawHeight;return x>=0&&x<=1&&y>=0&&y<=1;}
        private void Clamp()
        {Left=DrawWidth<=Width?(Width-DrawWidth)/2:Math.Max(Width-DrawWidth,Math.Min(0,Left));Top=DrawHeight<=Height?(Height-DrawHeight)/2:Math.Max(Height-DrawHeight,Math.Min(0,Top));}
    }

    internal sealed class PlanDevice
    {
        internal string Id,Label,Name,Type,Address,Zone;
        internal static List<PlanDevice> Read(Dictionary<string,object> project)
        {
            var result=new List<PlanDevice>();var ids=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var value in BridgeValues.Array(project,"records")??new object[0])
            {
                var row=value as Dictionary<string,object>;string id=BridgeValues.Text(row,"id");
                if(id.Length==0||!ids.Add(id)||(!String.IsNullOrEmpty(BridgeValues.Text(row,"recordKind"))&&BridgeValues.Text(row,"recordKind")!="device"))continue;
                string description=ProjectPanelCatalog.EffectiveDescription(row),name=BridgeValues.Text(row,"name");
                result.Add(new PlanDevice{Id=id,Label=String.IsNullOrWhiteSpace(description)?name:description,Name=name,Type=BridgeValues.Text(row,"type"),Zone=BridgeValues.Text(row,"zone"),Address=BridgeValues.Text(BridgeValues.Object(row,"bfx"),"address")});
            }
            return result;
        }
        internal bool Matches(string query,string type)
        {return (type.Length==0||Type==type)&&String.Join(" ",Label,Name,Type,Address,Zone,Id).IndexOf(query??"",StringComparison.CurrentCultureIgnoreCase)>=0;}
    }

    internal sealed class PlanPlacement
    {
        internal Dictionary<string,object> State {get;private set;}
        private readonly HashSet<string> recordIds;
        private readonly List<Dictionary<string,object>> undo=new List<Dictionary<string,object>>();
        internal bool CanUndo {get{return undo.Count>0;}}
        internal PlanPlacement(Dictionary<string,object> state,IEnumerable<PlanDevice> devices)
        {PortablePlans.Validate(state);State=CopyState(state);recordIds=new HashSet<string>(devices.Select(d=>d.Id),StringComparer.OrdinalIgnoreCase);}
        private static Dictionary<string,object> CopyState(Dictionary<string,object> state)
        {
            var copy=BridgeValues.Copy(state);var source=BridgeValues.Array(state,"plans");var target=BridgeValues.Array(copy,"plans");
            // Images are immutable strings: undo history must not duplicate megabytes per drag.
            for(int i=0;i<source.Length;i++)((Dictionary<string,object>)target[i])["image"]=((Dictionary<string,object>)source[i])["image"];
            return copy;
        }
        internal Dictionary<string,object> Plan(string id)
        {return BridgeValues.Array(State,"plans").Cast<Dictionary<string,object>>().FirstOrDefault(p=>BridgeValues.Text(p,"id")==id);}
        internal void Replace(Dictionary<string,object> state)
        {
            PortablePlans.Validate(state);if(BridgeJson.Serialize(state)==BridgeJson.Serialize(State))return;
            undo.Add(State);if(undo.Count>50)undo.RemoveAt(0);State=CopyState(state);
        }
        internal void Undo(){if(!CanUndo)return;State=undo[undo.Count-1];undo.RemoveAt(undo.Count-1);}
        internal static double Coordinate(Dictionary<string,object> point,string axis)
        {return Double.Parse(BridgeValues.Text(point,axis),CultureInfo.InvariantCulture);}
        internal Dictionary<string,object> Point(string planId,string recordId)
        {var plan=Plan(planId);return plan==null?null:BridgeValues.Array(plan,"points").Cast<Dictionary<string,object>>().FirstOrDefault(p=>String.Equals(BridgeValues.Text(p,"recordId"),recordId,StringComparison.OrdinalIgnoreCase));}
        internal void Place(string planId,string recordId,double x,double y)
        {
            if(!recordIds.Contains(recordId)||Double.IsNaN(x)||Double.IsNaN(y)||Double.IsInfinity(x)||Double.IsInfinity(y)||x<0||x>1||y<0||y>1)throw new InvalidDataException("Выберите устройство проекта и место внутри плана.");
            Edit(planId,recordId,new Dictionary<string,object>{{"recordId",recordId},{"x",x},{"y",y}});
        }
        internal void Remove(string planId,string recordId){Edit(planId,recordId,null);}
        private void Edit(string planId,string recordId,Dictionary<string,object> value)
        {
            var next=CopyState(State);var plan=BridgeValues.Array(next,"plans").Cast<Dictionary<string,object>>().FirstOrDefault(p=>BridgeValues.Text(p,"id")==planId);
            if(plan==null)throw new InvalidDataException("План не найден.");
            var points=BridgeValues.Array(plan,"points").Where(p=>!String.Equals(BridgeValues.Text(p as Dictionary<string,object>,"recordId"),recordId,StringComparison.OrdinalIgnoreCase)).ToList();
            if(value!=null)points.Add(value);plan["points"]=points.ToArray();Replace(next);
        }
    }
}
