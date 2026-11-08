// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace StreletzBridge
{
    internal static class PlanImage
    {
        internal static byte[] Read(string path)
        {
            if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new InvalidDataException("Изображение должно быть не больше 32 МБ.");
            using (var source = Image.FromFile(path))
            {
                if (!source.RawFormat.Equals(ImageFormat.Png) && !source.RawFormat.Equals(ImageFormat.Jpeg)) throw new InvalidDataException("Выберите PNG или JPEG.");
                if ((long)source.Width * source.Height > 40000000) throw new InvalidDataException("Уменьшите изображение до 40 мегапикселей.");
                double scale = Math.Min(1, 1800.0 / Math.Max(source.Width, source.Height));
                using (var bitmap = new Bitmap(Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale))))
                {
                    using (var g = Graphics.FromImage(bitmap)) { g.Clear(Color.White); g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(source, 0, 0, bitmap.Width, bitmap.Height); }
                    var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                    foreach (long quality in new long[] { 82, 55 }) using (var parameters = new EncoderParameters(1)) using (var output = new MemoryStream())
                    {
                        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);bitmap.Save(output, codec, parameters);
                        if (output.Length <= PortablePlans.MaxImageBytes) return output.ToArray();
                    }
                }
            }
            throw new InvalidDataException("Слишком сложный план: уменьшите изображение или разделите этаж на части.");
        }
        internal static Image Preview(string base64)
        {
            using (var stream = new MemoryStream(Convert.FromBase64String(base64))) using (var image = Image.FromStream(stream))
            {
                if ((long)image.Width * image.Height > 40000000) throw new InvalidDataException("Изображение слишком велико.");
                return new Bitmap(image);
            }
        }
    }
    internal sealed class PortablePlansDialog : Form
    {
        internal Dictionary<string, object> Result { get { return editor.State; } }
        internal Func<Dictionary<string,object>,bool,Task<bool>> PersistAsync;
        internal string LastSaveError {get;private set;}
        internal readonly PlanPlacement editor;
        internal readonly PlanCanvas canvas = new PlanCanvas();
        private readonly List<PlanDevice> devices;
        private readonly ComboBox plans = new ComboBox(), types = new ComboBox();
        private readonly TextBox search = new TextBox();
        private readonly CheckBox unplaced = new CheckBox(), labels = new CheckBox();
        private readonly DataGridView grid = new DataGridView();
        private readonly Label info = new Label(), count = new Label(), percent = new Label();
        private readonly Button add = new Button(), remove = new Button(), save = new Button(), place = new Button(), removePoint = new Button(), undo = new Button();
        private readonly Button saveAs = new Button();
        private readonly TextBox saveError = new TextBox();
        private readonly string initial;
        private string selectedId="";
        private bool busy, refreshing;
        internal PortablePlansDialog(Dictionary<string, object> state, Dictionary<string, object> project)
        {
            devices=PlanDevice.Read(project);editor=new PlanPlacement(state,devices);initial=BridgeJson.Serialize(Result);
            Text="Планы помещений · расстановка устройств";Font=new Font("Segoe UI",10);Size=new Size(1250,800);MinimumSize=new Size(960,640);StartPosition=FormStartPosition.CenterParent;KeyPreview=true;
            var header=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,Padding=new Padding(10),WrapContents=true};
            plans.DropDownStyle=ComboBoxStyle.DropDownList;plans.Width=230;plans.Margin=new Padding(0,6,8,0);
            Setup(add,"Добавить PNG/JPEG",180);Setup(remove,"Удалить план",125);
            var minus=new Button();var plus=new Button();var fit=new Button();Setup(minus,"−",40);Setup(plus,"+",40);Setup(fit,"Весь план",100);
            percent.Text="100%";percent.Width=52;percent.Height=36;percent.TextAlign=ContentAlignment.MiddleCenter;
            labels.Text="Подписи";labels.Checked=true;labels.AutoSize=true;labels.Margin=new Padding(10,10,0,0);
            header.Controls.AddRange(new Control[]{plans,add,remove,minus,percent,plus,fit,labels});
            var footer=new Panel{Dock=DockStyle.Bottom,Height=76,Padding=new Padding(12)};
            Setup(save,"Сохранить планы",175);save.Dock=DockStyle.Right;Setup(saveAs,"Сохранить как…",155);saveAs.Dock=DockStyle.Right;
            info.Dock=DockStyle.Fill;info.Padding=new Padding(0,4,12,0);footer.Controls.Add(info);footer.Controls.Add(saveAs);footer.Controls.Add(save);
            saveError.Multiline=true;saveError.ReadOnly=true;saveError.ScrollBars=ScrollBars.Vertical;saveError.Dock=DockStyle.Bottom;saveError.Height=110;saveError.Visible=false;saveError.BackColor=Color.FromArgb(255,245,228);saveError.ForeColor=Color.FromArgb(120,48,0);
            var side=new Panel{Dock=DockStyle.Left,Width=345,Padding=new Padding(10,0,10,8)};
            var filters=new TableLayoutPanel{Dock=DockStyle.Top,Height=128,ColumnCount=1,RowCount=4};
            filters.Controls.Add(new Label{Text="Поиск: описание, адрес, зона",AutoSize=true});search.Dock=DockStyle.Fill;filters.Controls.Add(search);
            types.DropDownStyle=ComboBoxStyle.DropDownList;types.Dock=DockStyle.Fill;types.Items.Add("Все типы");foreach(string type in devices.Select(d=>d.Type).Where(t=>t.Length>0).Distinct().OrderBy(t=>t))types.Items.Add(type);types.SelectedIndex=0;filters.Controls.Add(types);
            unplaced.Text="Только без отметки на этом плане";unplaced.AutoSize=true;filters.Controls.Add(unplaced);
            var commands=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=158,FlowDirection=FlowDirection.TopDown,WrapContents=false};
            count.Width=320;count.Height=22;Setup(place,"Разместить выбранное устройство",315);Setup(removePoint,"Убрать отметку",185);Setup(undo,"Отменить последнее изменение",315);
            var row=new FlowLayoutPanel{Width=320,Height=39,Margin=Padding.Empty};row.Controls.Add(removePoint);
            commands.Controls.AddRange(new Control[]{count,place,row,undo});
            grid.Dock=DockStyle.Fill;grid.ReadOnly=true;grid.AllowUserToAddRows=false;grid.AllowUserToDeleteRows=false;grid.AllowUserToResizeRows=false;grid.RowHeadersVisible=false;grid.MultiSelect=false;grid.SelectionMode=DataGridViewSelectionMode.FullRowSelect;grid.BackgroundColor=Color.White;grid.AutoGenerateColumns=false;grid.BorderStyle=BorderStyle.FixedSingle;
            grid.Columns.Add(new DataGridViewTextBoxColumn{Name="mark",HeaderText="",Width=28,SortMode=DataGridViewColumnSortMode.NotSortable});
            grid.Columns.Add(new DataGridViewTextBoxColumn{Name="device",HeaderText="Описание / устройство",AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill,SortMode=DataGridViewColumnSortMode.NotSortable});
            grid.Columns.Add(new DataGridViewTextBoxColumn{Name="address",HeaderText="Адрес",Width=52,SortMode=DataGridViewColumnSortMode.NotSortable});
            grid.DefaultCellStyle.WrapMode=DataGridViewTriState.True;grid.AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.AllCells;
            side.Controls.Add(grid);side.Controls.Add(commands);side.Controls.Add(filters);
            canvas.Dock=DockStyle.Fill;Controls.Add(canvas);Controls.Add(side);Controls.Add(saveError);Controls.Add(footer);Controls.Add(header);
            plans.SelectedIndexChanged+=delegate{if(!refreshing)ShowSelected();};search.TextChanged+=delegate{RefreshDevices();};types.SelectedIndexChanged+=delegate{RefreshDevices();};unplaced.CheckedChanged+=delegate{RefreshDevices();};
            grid.SelectionChanged+=delegate{if(refreshing)return;selectedId=grid.SelectedRows.Count==0?"":(string)grid.SelectedRows[0].Tag;canvas.Select(selectedId);StopPlacement();UpdateButtons();};
            grid.CellDoubleClick+=(s,e)=>{if(e.RowIndex<0)return;var point=editor.Point(PlanId,selectedId);if(point!=null)canvas.FocusPoint(point);else BeginPlacement();};
            canvas.SelectRecord=id=>{selectedId=id;SelectGridRow();canvas.Select(id);StopPlacement();UpdateButtons();};
            canvas.PlaceRecord=(id,x,y)=>{try{editor.Place(PlanId,id,x,y);StopPlacement();RefreshDevices();canvas.UpdatePoints(editor.Plan(PlanId));UpdateButtons();info.Text="Отметка изменена. Нажмите «Сохранить планы», чтобы передать её в APK.";}catch(Exception e){Warn(e.Message);}};
            canvas.ChangedScale=()=>percent.Text=Math.Round(canvas.Viewport.Zoom*100)+"%";canvas.CancelPlacement=StopPlacement;
            minus.Click+=delegate{canvas.Zoom(.8);};plus.Click+=delegate{canvas.Zoom(1.25);};fit.Click+=delegate{canvas.Fit();};labels.CheckedChanged+=delegate{canvas.ShowLabels=labels.Checked;canvas.Invalidate();};
            place.Click+=delegate{if(canvas.Placing)StopPlacement();else BeginPlacement();};removePoint.Click+=delegate{RemovePoint();};
            undo.Click+=delegate{string id=PlanId;editor.Undo();RefreshPlans(id);};
            add.Click+=async delegate{await Add();};remove.Click+=delegate{RemovePlan();};
            save.Click+=async delegate{await Save(false);};saveAs.Click+=async delegate{await Save(true);};
            FormClosing+=(s,e)=>{if(busy){e.Cancel=true;return;}if(DialogResult!=DialogResult.OK&&initial!=BridgeJson.Serialize(Result)&&MessageBox.Show(this,"Закрыть без сохранения расстановки?","Несохранённые изменения",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)e.Cancel=true;};
            RefreshPlans("");
        }
        private async Task Save(bool copy)
        {
            if(busy)return;
            LastSaveError="";saveError.Visible=false;
            try
            {
                PortablePlans.Validate(Result);busy=true;UseWaitCursor=true;
                foreach(Control control in Controls)control.Enabled=false;
                // Do not dismiss until the project is durably saved and published by the bridge.
                bool saved=PersistAsync==null?!copy:await PersistAsync(BridgeValues.Copy(Result),copy);
                if(saved){busy=false;DialogResult=DialogResult.OK;Close();}
            }
            catch(Exception error)
            {
                LastSaveError=error is PlanSaveException?error.Message:"Сохранение не выполнено. Расстановка остаётся в редакторе. Повторите попытку или выберите «Сохранить как…».\r\n"+error.Message;
                saveError.Text=LastSaveError;saveError.Visible=true;
            }
            finally
            {
                busy=false;UseWaitCursor=false;
                if(!IsDisposed){foreach(Control control in Controls)control.Enabled=true;UpdateButtons();}
            }
        }
        private static void Setup(Button b,string text,int width){b.Text=text;b.Width=width;b.Height=36;b.Margin=new Padding(0,0,6,0);}
        private string PlanId{get{var items=BridgeValues.Array(Result,"plans");return plans.SelectedIndex>=0&&plans.SelectedIndex<items.Length?BridgeValues.Text(items[plans.SelectedIndex] as Dictionary<string,object>,"id"):"";}}
        private void RefreshPlans(string id)
        {
            refreshing=true;plans.Items.Clear();int index=0;foreach(var value in BridgeValues.Array(Result,"plans")){var p=(Dictionary<string,object>)value;if(BridgeValues.Text(p,"id")==id)index=plans.Items.Count;plans.Items.Add(BridgeValues.Text(p,"name"));}
            if(plans.Items.Count>0)plans.SelectedIndex=index;refreshing=false;ShowSelected();
        }
        private void ShowSelected()
        {
            StopPlacement();var plan=editor.Plan(PlanId);Image image=null;
            try{if(plan!=null)image=PlanImage.Preview(BridgeValues.Text(plan,"image"));}
            catch(Exception){info.Text="Изображение повреждено. Удалите план и добавьте PNG/JPEG.";}
            canvas.SetPlan(image,plan,devices);RefreshDevices();UpdateButtons();
        }
        private void RefreshDevices()
        {
            if(refreshing)return;refreshing=true;grid.Rows.Clear();string type=types.SelectedIndex<=0?"":(string)types.SelectedItem;int total=0;
            foreach(var device in devices)
            {
                bool has=editor.Point(PlanId,device.Id)!=null;if(has)total++;
                if(!device.Matches(search.Text.Trim(),type)||(unplaced.Checked&&has))continue;
                int row=grid.Rows.Add(has?"●":"",device.Label+"\n"+device.Type+" · зона "+device.Zone,device.Address);
                grid.Rows[row].Tag=device.Id;grid.Rows[row].Cells[1].ToolTipText=device.Label+"\n"+device.Name+" · зона "+device.Zone;
            }
            grid.ClearSelection();SelectGridRow();refreshing=false;count.Text="На плане: "+total+" / "+devices.Count+" · в списке: "+grid.Rows.Count;UpdateButtons();
        }
        private void SelectGridRow()
        {
            bool old=refreshing;refreshing=true;grid.ClearSelection();foreach(DataGridViewRow row in grid.Rows)if(String.Equals((string)row.Tag,selectedId,StringComparison.OrdinalIgnoreCase)){row.Selected=true;grid.FirstDisplayedScrollingRowIndex=row.Index;break;}refreshing=old;
        }
        private void UpdateButtons()
        {
            bool exists=devices.Any(d=>String.Equals(d.Id,selectedId,StringComparison.OrdinalIgnoreCase));place.Enabled=!busy&&canvas.HasImage&&exists;
            removePoint.Enabled=!busy&&editor.Point(PlanId,selectedId)!=null;undo.Enabled=!busy&&editor.CanUndo;add.Enabled=!busy&&plans.Items.Count<PortablePlans.MaxPlans;remove.Enabled=!busy&&plans.Items.Count>0;
        }
        private void StopPlacement(){canvas.Placing=false;canvas.Cursor=Cursors.Default;place.Text="Разместить выбранное устройство";info.Text="Колесо — масштаб · пустое место — перемещение плана · отметку можно перетащить.\nПосле сохранения в APK: «Загрузить проект из Windows».";}
        private void BeginPlacement(){if(!place.Enabled)return;canvas.Placing=true;canvas.Cursor=Cursors.Cross;place.Text="Отменить размещение (Esc)";var d=devices.FirstOrDefault(v=>v.Id==selectedId);info.Text="Нажмите место на плане: "+(d==null?selectedId:d.Label);canvas.Focus();}
        private void RemovePoint()
        {
            if(editor.Point(PlanId,selectedId)==null)return;
            if(MessageBox.Show(this,"Убрать выбранную отметку с этого плана? Устройство останется в проекте.","Убрать отметку",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
            editor.Remove(PlanId,selectedId);canvas.UpdatePoints(editor.Plan(PlanId));RefreshDevices();StopPlacement();UpdateButtons();
        }
        private void Warn(string text){MessageBox.Show(this,text,"Планы помещений",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
        private async Task Add()
        {
            using(var open=new OpenFileDialog{Filter="Планы PNG/JPEG|*.png;*.jpg;*.jpeg",Title="Выберите план этажа"})
            {
                if(open.ShowDialog(this)!=DialogResult.OK)return;
                using(var title=new Form{Text="Название плана",Size=new Size(440,155),StartPosition=FormStartPosition.CenterParent,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false})
                {
                    var name=new TextBox{Left=16,Top=15,Width=390,Text=Path.GetFileNameWithoutExtension(open.FileName),MaxLength=120};
                    var ok=new Button{Text="Добавить",Left=286,Top=55,Width=120,DialogResult=DialogResult.OK};title.Controls.AddRange(new Control[]{name,ok});title.AcceptButton=ok;
                    if(title.ShowDialog(this)!=DialogResult.OK)return;
                    busy=true;add.Enabled=remove.Enabled=save.Enabled=saveAs.Enabled=plans.Enabled=grid.Enabled=place.Enabled=removePoint.Enabled=undo.Enabled=canvas.Enabled=false;UseWaitCursor=true;
                    try{byte[] image=await Task.Run(()=>PlanImage.Read(open.FileName));var next=PortablePlans.Add(Result,name.Text,image);editor.Replace(next);RefreshPlans(BridgeValues.Text(BridgeValues.Array(next,"plans").Last() as Dictionary<string,object>,"id"));}
                    catch(Exception e){Warn(e.Message);}
                    finally{busy=false;UseWaitCursor=false;save.Enabled=saveAs.Enabled=plans.Enabled=grid.Enabled=canvas.Enabled=true;UpdateButtons();}
                }
            }
        }
        private void RemovePlan()
        {
            if(PlanId.Length==0)return;
            if(MessageBox.Show(this,"Удалить план и его отметки из Windows-проекта? Устройства останутся. Уже загруженную копию в APK удалите отдельно.","Удалить план",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
            editor.Replace(PortablePlans.Remove(Result,PlanId));RefreshPlans("");
        }
    }
}
