// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
namespace StreletzBridge
{
    internal sealed class PlanSaveResult
    {
        internal string Path, BackupPath;
        internal bool UsedNewCopy;
    }
    internal sealed class PlanSaveException : IOException
    {
        internal readonly string PendingPath, BackupPath;
        internal PlanSaveException(string message,Exception cause,string pending,string backup) : base(message,cause)
        {PendingPath=pending;BackupPath=backup;}
    }
    // Only plan saves use this policy. Registration journals keep their existing atomic contract.
    internal static class PlanProjectStorage
    {
        internal static PlanSaveResult Save(string path,string content,bool newFile=false)
        {return Save(path,content,newFile,(from,to,backup)=>File.Replace(from,to,backup),Thread.Sleep);}
        internal static PlanSaveResult Save(string path,string content,bool newFile,Action<string,string,string> replace,Action<int> wait)
        {
            string target=System.IO.Path.GetFullPath(path),directory=System.IO.Path.GetDirectoryName(target);
            Directory.CreateDirectory(directory);
            if(Directory.Exists(target))throw new IOException("Выбран каталог вместо файла проекта.");
            if(newFile&&File.Exists(target))throw new IOException("Файл уже существует. Для сохранения копии выберите новое имя.");
            string stamp=DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N").Substring(0,8);
            string pending=target+".plans-"+stamp+".pending",backup=null;
            byte[] bytes=new UTF8Encoding(false).GetBytes(content);bool written=false;
            try
            {
                using(var file=new FileStream(pending,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                {file.Write(bytes,0,bytes.Length);file.Flush(true);}written=true;
                if(!File.Exists(target))
                {
                    // Refuses a file created by another process after the check.
                    File.Move(pending,target);return new PlanSaveResult{Path=target};
                }
                if(newFile)throw new IOException("Имя копии уже занято. Выберите другое имя.");
                backup=target+".before-plans-"+stamp+".bak";
                // Durable independent backup before any operation which can rename the original.
                using(var original=new FileStream(target,FileMode.Open,FileAccess.Read,FileShare.Read))
                using(var copy=new FileStream(backup,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                {original.CopyTo(copy);copy.Flush(true);}
                string expected=Digest(backup);
                for(int attempt=0;attempt<3;attempt++)
                {
                    if(Digest(target)!=expected)throw new IOException("Файл проекта изменён другой программой. Сохраните расстановку под новым именем.");
                    try
                    {
                        replace(pending,target,null);
                        return new PlanSaveResult{Path=target,BackupPath=backup};
                    }
                    catch(IOException error)
                    {
                        int code=error.HResult&0xffff;
                        // These failures retain both original names. Do not retry ambiguous 1176/1177.
                        if(code!=32&&code!=33&&code!=1175)throw;
                        if(!File.Exists(pending)||!File.Exists(target)||Digest(target)!=expected)throw;
                        if(attempt<2){wait(150*(attempt+1));continue;}
                        string name=System.IO.Path.GetFileNameWithoutExtension(target);if(name.Length>80)name=name.Substring(0,80);
                        string next=System.IO.Path.Combine(directory,name+"-plans-"+stamp+".s3project");
                        File.Move(pending,next);
                        return new PlanSaveResult{Path=next,BackupPath=backup,UsedNewCopy=true};
                    }
                }
                throw new IOException("Не удалось сохранить план.");
            }
            catch(Exception error)
            {
                // Retain the complete pending project and backup on failure. Never delete the original
                // to work around a denied replacement and never truncate it in place.
                string retained=written&&File.Exists(pending)?pending:null;
                string message="Планы не сохранены. Расстановка остаётся в редакторе. Повторите попытку или нажмите «Сохранить как…».\n"+error.Message;
                if(retained!=null)message+="\nФайл для восстановления: "+retained;
                if(backup!=null&&File.Exists(backup))message+="\nРезервная копия: "+backup;
                throw new PlanSaveException(message,error,retained,backup);
            }
        }
        private static string Digest(string path)
        {using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))using(var hash=SHA256.Create())return Convert.ToBase64String(hash.ComputeHash(stream));}
    }
}
