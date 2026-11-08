// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
namespace StreletzBridge
{
    internal static class PlanImageSelfTest
    {
        static void Check(bool v){if(!v)throw new Exception("Image test failed");}
        [STAThread] static int Main(string[] args)
        {
            try
            {
                if(args.Length!=1)throw new ArgumentException("Output directory required");
                string dir=Path.GetFullPath(args[0]);Directory.CreateDirectory(dir);string png=Path.Combine(dir,"plan-source.png");
                using(var b=new Bitmap(2400,1600))using(var g=Graphics.FromImage(b))using(var pen=new Pen(Color.SlateGray,12))using(var font=new Font("Segoe UI",50))
                {
                    g.Clear(Color.White);g.DrawRectangle(pen,120,120,2160,1360);g.DrawLine(pen,120,800,2280,800);g.DrawLine(pen,1200,120,1200,800);
                    g.DrawString("201",font,Brushes.SlateGray,450,350);g.DrawString("202",font,Brushes.SlateGray,1650,350);g.DrawString("Коридор",font,Brushes.SlateGray,950,1100);b.Save(png,ImageFormat.Png);
                }
                byte[] image=PlanImage.Read(png);Check(image.Length<=800000&&image[0]==255&&image[1]==216);
                using(var preview=PlanImage.Preview(Convert.ToBase64String(image)))Check(preview.Width==1800&&preview.Height==1200);
                string invalid=Path.Combine(dir,"invalid.png");File.WriteAllText(invalid,"not a picture");bool failed=false;try{PlanImage.Read(invalid);}catch(Exception){failed=true;}Check(failed);
                var project=new Dictionary<string,object>{{"schemaVersion",1},{"records",new object[]{new Dictionary<string,object>{{"id","fixture-device"},{"recordKind","device"},{"name","Тестовое устройство"}}}}};
                var fields=PortablePlans.Add(PortablePlans.Read(project),"Учебный корпус · этаж 2",image);
                var updated=PortablePlans.Apply(project,fields);string transfer=Path.Combine(dir,"windows-plan.s3project");ProjectFiles.Save(transfer,updated);Check(BridgeValues.Array(PortablePlans.Read(ProjectFiles.Load(transfer)),"plans").Length==1);
                Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
                using(var dialog=new PortablePlansDialog(fields))using(var screenshot=new Bitmap(dialog.Width,dialog.Height))
                {dialog.StartPosition=FormStartPosition.Manual;dialog.Location=new Point(-20000,-20000);dialog.ShowInTaskbar=false;dialog.Show();Application.DoEvents();dialog.DrawToBitmap(screenshot,new Rectangle(0,0,screenshot.Width,screenshot.Height));screenshot.Save(Path.Combine(dir,"windows-plans-dialog.png"),ImageFormat.Png);dialog.Close();}
                Console.WriteLine("PASS plan images: PNG to bounded JPEG, invalid image rejected, real s3project save/load, WinForms dialog render; no bridge or hardware started.");return 0;
            }
            catch(Exception e){Console.Error.WriteLine(e);return 1;}
        }
    }
}
