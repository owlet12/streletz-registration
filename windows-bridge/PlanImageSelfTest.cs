// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using System.Reflection;
using System.Linq;
namespace StreletzBridge
{
    internal static class PlanImageSelfTest
    {
        static void Check(bool v,[System.Runtime.CompilerServices.CallerLineNumber] int line=0){if(!v)throw new Exception("Image test failed at line "+line);}
        static void Mouse(PlanCanvas canvas,string name,MouseButtons button,int x,int y,int delta=0)
        {typeof(PlanCanvas).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(canvas,new object[]{new MouseEventArgs(button,1,x,y,delta)});Application.DoEvents();}
        static void ClickAt(PlanCanvas canvas,int x,int y){Mouse(canvas,"OnMouseDown",MouseButtons.Left,x,y);Mouse(canvas,"OnMouseUp",MouseButtons.Left,x,y);}
        static System.Collections.Generic.IEnumerable<Control> Descendants(Control owner){foreach(Control c in owner.Controls){yield return c;foreach(var d in Descendants(c))yield return d;}}
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
                var project=new Dictionary<string,object>{{"schemaVersion",1},{"records",new object[]{
                    new Dictionary<string,object>{{"id","fixture-device"},{"recordKind","device"},{"name","1 Аврора-ПРО"},{"description","2R.BTH01"},{"type","Аврора-ПРО"},{"zone","201"}},
                    new Dictionary<string,object>{{"id","device-2"},{"name","2 ИПР-ПРО"},{"description","2R.BTM02"},{"type","ИПР-ПРО"},{"zone","202"}},
                    new Dictionary<string,object>{{"id","device-3"},{"name","3 Табло-ПРО"},{"description","2R.BIAL03"},{"type","Табло-ПРО"},{"zone","200"}}
                }}};
                var fields=PortablePlans.Add(PortablePlans.Read(project),"Учебный корпус · этаж 2",image);
                var updated=PortablePlans.Apply(project,fields);string transfer=Path.Combine(dir,"windows-plan.s3project");ProjectFiles.Save(transfer,updated);Check(BridgeValues.Array(PortablePlans.Read(ProjectFiles.Load(transfer)),"plans").Length==1);
                Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
                string before=BridgeJson.Serialize(fields),pid=BridgeValues.Text(BridgeValues.Array(fields,"plans")[0] as Dictionary<string,object>,"id");
                using(var dialog=new PortablePlansDialog(fields,project))
                {
                    dialog.StartPosition=FormStartPosition.Manual;dialog.Location=new Point(-20000,-20000);dialog.ShowInTaskbar=false;dialog.Show();Application.DoEvents();
                    var canvas=dialog.canvas;int cx=canvas.Width/2,cy=canvas.Height/2;
                    ClickAt(canvas,cx,cy);Check(dialog.editor.Point(pid,"fixture-device")==null);
                    canvas.Select("fixture-device");canvas.Placing=true;ClickAt(canvas,cx,cy);Check(dialog.editor.Point(pid,"fixture-device")!=null);
                    double initialX=PlanPlacement.Coordinate(dialog.editor.Point(pid,"fixture-device"),"x");
                    Mouse(canvas,"OnMouseDown",MouseButtons.Left,cx,cy);Mouse(canvas,"OnMouseMove",MouseButtons.Left,cx+80,cy+45);Mouse(canvas,"OnMouseUp",MouseButtons.Left,cx+80,cy+45);
                    var point=dialog.editor.Point(pid,"fixture-device");Check(PlanPlacement.Coordinate(point,"x")>.5);Check(BridgeValues.Array(dialog.editor.Plan(pid),"points").Length==1);
                    double x=PlanPlacement.Coordinate(point,"x");
                    Mouse(canvas,"OnMouseWheel",MouseButtons.None,cx,cy,120);Check(canvas.Viewport.Zoom>1);Check(PlanPlacement.Coordinate(dialog.editor.Point(pid,"fixture-device"),"x")==x);
                    Mouse(canvas,"OnMouseDown",MouseButtons.Left,20,20);Mouse(canvas,"OnMouseMove",MouseButtons.Left,40,45);Mouse(canvas,"OnMouseUp",MouseButtons.Left,40,45);Check(BridgeValues.Array(dialog.editor.Plan(pid),"points").Length==1);
                    // A drag outside the image must not save an invalid point.
                    canvas.Fit();point=dialog.editor.Point(pid,"fixture-device");int px=(int)(canvas.Viewport.Left+PlanPlacement.Coordinate(point,"x")*canvas.Viewport.DrawWidth),py=(int)(canvas.Viewport.Top+PlanPlacement.Coordinate(point,"y")*canvas.Viewport.DrawHeight);
                    Mouse(canvas,"OnMouseDown",MouseButtons.Left,px,py);Mouse(canvas,"OnMouseMove",MouseButtons.Left,-100,-100);Mouse(canvas,"OnMouseUp",MouseButtons.Left,-100,-100);Check(PlanPlacement.Coordinate(dialog.editor.Point(pid,"fixture-device"),"x")==x);
                    var search=Descendants(dialog).OfType<TextBox>().First();var grid=Descendants(dialog).OfType<DataGridView>().First();search.Text="BTM";Application.DoEvents();Check(grid.Rows.Count==1);search.Text="";Check(grid.Rows.Count==3);
                    var types=Descendants(dialog).OfType<ComboBox>().First(c=>c.Items.Contains("Все типы"));types.SelectedItem="ИПР-ПРО";Check(grid.Rows.Count==1);types.SelectedIndex=0;
                    var undo=Descendants(dialog).OfType<Button>().First(b=>b.Text=="Отменить последнее изменение");Check(undo.Visible&&undo.Enabled);undo.PerformClick();Application.DoEvents();Check(Math.Abs(PlanPlacement.Coordinate(dialog.editor.Point(pid,"fixture-device"),"x")-initialX)<.00001);
                    canvas.PlaceRecord("fixture-device",.27,.25);canvas.PlaceRecord("device-2",.74,.25);canvas.PlaceRecord("device-3",.59,.74);canvas.Fit();canvas.Select("fixture-device");Application.DoEvents();
                    using(var screenshot=new Bitmap(dialog.Width,dialog.Height)){dialog.DrawToBitmap(screenshot,new Rectangle(0,0,screenshot.Width,screenshot.Height));screenshot.Save(Path.Combine(dir,"windows-plans-dialog.png"),ImageFormat.Png);}
                    dialog.Size=dialog.MinimumSize;Application.DoEvents();using(var screenshot=new Bitmap(dialog.Width,dialog.Height)){dialog.DrawToBitmap(screenshot,new Rectangle(0,0,screenshot.Width,screenshot.Height));screenshot.Save(Path.Combine(dir,"windows-plans-small.png"),ImageFormat.Png);}
                    Check(BridgeJson.Serialize(fields)==before);var saved=PortablePlans.Apply(project,dialog.Result);ProjectFiles.Save(transfer,saved);Check(BridgeValues.Array((Dictionary<string,object>)BridgeValues.Array(PortablePlans.Read(ProjectFiles.Load(transfer)),"plans")[0],"points").Length==3);
                    Descendants(dialog).OfType<Button>().First(b=>b.Text=="Сохранить планы").PerformClick();Check(dialog.DialogResult==DialogResult.OK);
                }
                Console.WriteLine("PASS plan images: PNG to bounded JPEG, invalid image rejected, click placement, drag, pan, zoom, invalid drop, search/type filters, undo, staged edits, real s3project save/load and two WinForms renders; no bridge or hardware started.");return 0;
            }
            catch(Exception e){Console.Error.WriteLine(e);return 1;}
        }
    }
}
