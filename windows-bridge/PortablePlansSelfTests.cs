// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
namespace StreletzBridge
{
    internal static class PortablePlansSelfTests
    {
        static void Check(bool v){if(!v)throw new Exception("Portable plan assertion failed");}
        static void Reject(Action action){try{action();}catch(InvalidDataException){return;}throw new Exception("Invalid plan accepted");}
        internal static void Run()
        {
            var project=new Dictionary<string,object>{{"schemaVersion",1},{"records",new object[]{new Dictionary<string,object>{{"id","fixture-device"},{"description","unchanged"}}}},{"extra","keep"}};
            var empty=PortablePlans.Read(project);empty["scans"]=new object[]{new Dictionary<string,object>{{"id","FA0000000001"},{"key",""}}};empty["future"]="preserve";
            string before=BridgeJson.Serialize(project);var fields=PortablePlans.Add(empty,"Этаж 2",new byte[]{1,2,3});Check(BridgeValues.Array(empty,"plans").Length==0);
            var plan=(Dictionary<string,object>)BridgeValues.Array(fields,"plans")[0];plan["points"]=new object[]{new Dictionary<string,object>{{"recordId","fixture-device"},{"x",0.25},{"y",0.75}}};
            var updated=PortablePlans.Apply(project,fields);Check(BridgeJson.Serialize(project)==before);Check(BridgeValues.Text(updated,"extra")=="keep");Check(BridgeValues.Text(BridgeValues.Object(updated,"fieldwork"),"future")=="preserve");
            Check(BridgeJson.Serialize(BridgeValues.Array(updated,"records"))==BridgeJson.Serialize(BridgeValues.Array(project,"records")));
            var reply=ProjectSyncSnapshot.Reply(updated,Guid.NewGuid().ToString("D"),"","");var transferred=PortablePlans.Read(BridgeValues.Object(reply,"project"));Check(BridgeValues.Array(transferred,"scans").Length==1);Check(BridgeValues.Array((Dictionary<string,object>)BridgeValues.Array(transferred,"plans")[0],"points").Length==1);
            Check(ProjectSyncSnapshot.ContentRevision(project)!=ProjectSyncSnapshot.ContentRevision(updated));
            var removed=PortablePlans.Remove(transferred,BridgeValues.Text(plan,"id"));Check(BridgeValues.Array(removed,"plans").Length==0);Check(BridgeValues.Array(transferred,"plans").Length==1);Check(BridgeValues.Array(removed,"scans").Length==1);
            Reject(()=>PortablePlans.Add(empty,"",new byte[]{1}));Reject(()=>PortablePlans.Add(empty,"Too big",new byte[800001]));
            var max=empty;for(int i=0;i<8;i++)max=PortablePlans.Add(max,"Floor "+i,new byte[]{1});Reject(()=>PortablePlans.Add(max,"Floor 9",new byte[]{1}));
            var broken=BridgeValues.Copy(fields);((Dictionary<string,object>)BridgeValues.Array((Dictionary<string,object>)BridgeValues.Array(broken,"plans")[0],"points")[0])["x"]=1.1;Reject(()=>PortablePlans.Validate(broken));
            broken=BridgeValues.Copy(fields);broken["version"]=2;Reject(()=>PortablePlans.Validate(broken));
            var devices=PlanDevice.Read(project);Check(devices.Count==1&&devices[0].Label=="unchanged");Check(devices[0].Matches("UNCHANGED",""));Check(!devices[0].Matches("not found",""));
            var edit=new PlanPlacement(fields,devices);string pid=BridgeValues.Text(plan,"id"),original=BridgeJson.Serialize(fields);
            edit.Place(pid,"fixture-device",.6,.4);Check(BridgeJson.Serialize(fields)==original);Check(PlanPlacement.Coordinate(edit.Point(pid,"FIXTURE-DEVICE"),"x")==.6);
            edit.Place(pid,"FIXTURE-DEVICE",.7,.3);Check(BridgeValues.Array(edit.Plan(pid),"points").Length==1);edit.Undo();Check(PlanPlacement.Coordinate(edit.Point(pid,"fixture-device"),"x")==.6);
            edit.Remove(pid,"fixture-device");Check(edit.Point(pid,"fixture-device")==null);edit.Undo();Check(edit.Point(pid,"fixture-device")!=null);
            string unchanged=BridgeJson.Serialize(edit.State);Reject(()=>edit.Place(pid,"unknown",.5,.5));Reject(()=>edit.Place(pid,"fixture-device",Double.NaN,.5));Reject(()=>edit.Place("missing","fixture-device",.5,.5));Reject(()=>edit.Place(pid,"fixture-device",1.1,.5));Check(BridgeJson.Serialize(edit.State)==unchanged);
            var second=PortablePlans.Add(edit.State,"Another floor",new byte[]{1});edit.Replace(second);string secondId=BridgeValues.Text(BridgeValues.Array(second,"plans")[1] as Dictionary<string,object>,"id");edit.Place(secondId,"fixture-device",.2,.3);Check(edit.Point(pid,"fixture-device")!=null&&edit.Point(secondId,"fixture-device")!=null);
            edit.Remove(secondId,"fixture-device");Check(edit.Point(pid,"fixture-device")!=null&&edit.Point(secondId,"fixture-device")==null);
            Check(BridgeValues.Array(edit.State,"scans").Length==1&&BridgeValues.Text(edit.State,"future")=="preserve");
            var v=new PlanViewport();v.Resize(600,400,1000,500);v.Fit();double x,y;Check(!v.Map(300,0,out x,out y));Check(v.Map(300,200,out x,out y)&&Math.Abs(x-.5)<.00001&&Math.Abs(y-.5)<.00001);
            v.ScaleAt(3,300,200);Check(v.Map(300,200,out x,out y)&&Math.Abs(x-.5)<.00001);v.Pan(99999,-99999);Check(v.Left<=0&&v.Left>=v.Width-v.DrawWidth&&v.Top<=0&&v.Top>=v.Height-v.DrawHeight);
            var random=new Random(28);for(int n=0;n<300;n++){v.ScaleAt(.5+random.NextDouble()*2,300,200);v.Pan(random.Next(-300,300),random.Next(-300,300));x=random.NextDouble();y=random.NextDouble();double rx,ry;Check(v.Map(v.Left+x*v.DrawWidth,v.Top+y*v.DrawHeight,out rx,out ry)&&Math.Abs(x-rx)<.000001&&Math.Abs(y-ry)<.000001);Check(v.Zoom>=1&&v.Zoom<=8);}
            v.ScaleAt(Double.NaN,0,0);Check(!Double.IsNaN(v.Zoom));v.Resize(400,600,1000,500);v.Focus(.5,.5);Check(v.Map(200,300,out x,out y)&&Math.Abs(x-.5)<.000001);
            // Cross-platform fixture now comes from the same placement path as the editor.
            edit=new PlanPlacement(fields,devices);edit.Remove(pid,"fixture-device");edit.Place(pid,"fixture-device",.25,.75);updated=PortablePlans.Apply(project,edit.State);
            // Cross-platform fixture: import this exact Windows-shaped envelope in Android tests.
            string path=Environment.GetEnvironmentVariable("STRELETZ_PLAN_TEST_OUTPUT");
            if(!String.IsNullOrEmpty(path))File.WriteAllText(path,BridgeJson.Serialize(updated));
        }
    }
}
