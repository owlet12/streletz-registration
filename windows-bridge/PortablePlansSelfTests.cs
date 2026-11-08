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
            // Cross-platform fixture: import this exact Windows-shaped envelope in Android tests.
            string path=Environment.GetEnvironmentVariable("STRELETZ_PLAN_TEST_OUTPUT");
            if(!String.IsNullOrEmpty(path))File.WriteAllText(path,BridgeJson.Serialize(updated));
        }
    }
}
