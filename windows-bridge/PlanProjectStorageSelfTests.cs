// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.IO;
using System.Text;
namespace StreletzBridge
{
    internal static class PlanProjectStorageSelfTests
    {
        static void Check(bool value){if(!value)throw new Exception("Plan save assertion failed");}
        static IOException Error(int code){return new IOException("Simulated Windows file error "+code,unchecked((int)0x80070000)|code);}
        internal static void Run()
        {
            string dir=Path.Combine(Path.GetTempPath(),"streletz-plan-save-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
            string path=Path.Combine(dir,"project.s3project"),original="{\"records\":[],\"old\":true}",next="{\"records\":[],\"plan\":\"новый\"}";
            File.WriteAllText(path,original,new UTF8Encoding(false));
            var result=PlanProjectStorage.Save(path,next);Check(result.Path==path&&!result.UsedNewCopy&&File.ReadAllText(path)==next&&File.ReadAllText(result.BackupPath)==original);
            int attempts=0;
            result=PlanProjectStorage.Save(path,"retry",false,(a,b,c)=>{if(attempts++==0)throw Error(1175);File.Replace(a,b,c);},ms=>{});
            Check(attempts==2&&result.Path==path&&File.ReadAllText(path)=="retry"&&File.ReadAllText(result.BackupPath)==next);
            attempts=0;
            result=PlanProjectStorage.Save(path,"versioned-copy",false,(a,b,c)=>{attempts++;throw Error(1175);},ms=>{});
            Check(attempts==3&&result.UsedNewCopy&&result.Path!=path&&File.ReadAllText(result.Path)=="versioned-copy"&&File.ReadAllText(path)=="retry"&&File.ReadAllText(result.BackupPath)=="retry");
            foreach(int code in new[]{5,1176,1177})
            {
                attempts=0;PlanSaveException failure=null;
                try{PlanProjectStorage.Save(path,"recover-"+code,false,(a,b,c)=>{attempts++;throw Error(code);},ms=>{});}catch(PlanSaveException e){failure=e;}
                Check(attempts==1&&failure!=null&&File.ReadAllText(path)=="retry"&&File.ReadAllText(failure.PendingPath)=="recover-"+code&&File.ReadAllText(failure.BackupPath)=="retry");
            }
            // An external writer is not silently overwritten by the retry.
            PlanSaveException changed=null;
            try{PlanProjectStorage.Save(path,"ours",false,(a,b,c)=>{File.WriteAllText(b,"external change");throw Error(1175);},ms=>{});}catch(PlanSaveException e){changed=e;}
            Check(changed!=null&&File.ReadAllText(path)=="external change"&&File.ReadAllText(changed.PendingPath)=="ours");
            bool refused=false;try{PlanProjectStorage.Save(path,"must not overwrite",true);}catch(IOException){refused=true;}Check(refused&&File.ReadAllText(path)=="external change");
            string copy=Path.Combine(dir,"new-copy.s3project");result=PlanProjectStorage.Save(copy,"new project",true);Check(result.Path==copy&&File.ReadAllText(copy)=="new project");
            // Reproduce a real Windows delete-sharing lock, as used by scanners/synchronizers.
            if(Path.DirectorySeparatorChar=='\\')using(var held=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                result=PlanProjectStorage.Save(path,"real-lock-copy");
                Check(result.UsedNewCopy&&File.ReadAllText(result.Path)=="real-lock-copy"&&File.ReadAllText(path)=="external change");
            }
            Console.WriteLine("PASS plan save: backup, transient retry, 1175 versioned copy, 5/1176/1177 recovery, external change protection, new-file collision and native delete-sharing lock; synthetic files only.");
        }
    }
}
