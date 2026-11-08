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
        internal Dictionary<string, object> Result { get; private set; }
        private readonly ListBox list = new ListBox();private readonly PictureBox preview = new PictureBox();private readonly Label info = new Label();
        private readonly Button add = new Button(), remove = new Button(), save = new Button();
        private bool busy;
        internal PortablePlansDialog(Dictionary<string, object> state)
        {
            Result = BridgeValues.Copy(state);PortablePlans.Validate(Result);
            Text = "Планы помещений · для APK";Font = new Font("Segoe UI", 10);Size = new Size(960, 680);MinimumSize = new Size(700, 500);StartPosition = FormStartPosition.CenterParent;
            var header = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 58, Padding = new Padding(10), WrapContents = false };
            add.Text = "Добавить PNG/JPEG";add.Width = 190;add.Height = 36;remove.Text = "Удалить план";remove.Width = 140;remove.Height = 36;header.Controls.AddRange(new Control[] { add, remove });
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 98, Padding = new Padding(12) };
            info.Dock = DockStyle.Fill;info.Text = "В APK: «Загрузить проект из Windows» → карточка устройства → «Открыть план».\nРасставить устройства можно на телефоне. Местные планы в APK сохраняются при обновлении.";
            save.Text = "Сохранить планы";save.Width = 165;save.Dock = DockStyle.Right;
            footer.Controls.Add(info);footer.Controls.Add(save);
            list.Dock = DockStyle.Left;list.Width = 240;list.IntegralHeight = false;list.SelectedIndexChanged += delegate { ShowSelected(); };
            preview.Dock = DockStyle.Fill;preview.SizeMode = PictureBoxSizeMode.Zoom;preview.BackColor = Color.FromArgb(232, 239, 244);preview.Padding = new Padding(14);
            Controls.Add(preview);Controls.Add(list);Controls.Add(footer);Controls.Add(header);
            add.Click += async delegate { await Add(); };remove.Click += delegate { Remove(); };
            save.Click += delegate { PortablePlans.Validate(Result);DialogResult = DialogResult.OK;Close(); };
            FormClosing += (s,e) => { if(busy)e.Cancel=true; };
            FormClosed += delegate { if(preview.Image!=null)preview.Image.Dispose(); };
            RefreshList();
        }
        private Dictionary<string, object> Selected(){var plans=BridgeValues.Array(Result,"plans");return list.SelectedIndex>=0&&list.SelectedIndex<plans.Length?plans[list.SelectedIndex] as Dictionary<string,object>:null;}
        private void RefreshList(){list.Items.Clear();foreach(var p in BridgeValues.Array(Result,"plans"))list.Items.Add(BridgeValues.Text(p as Dictionary<string,object>,"name"));if(list.Items.Count>0)list.SelectedIndex=list.Items.Count-1;else ShowSelected();}
        private void ShowSelected()
        {
            if(preview.Image!=null){preview.Image.Dispose();preview.Image=null;}var plan=Selected();remove.Enabled=plan!=null&&!busy;add.Enabled=list.Items.Count<PortablePlans.MaxPlans&&!busy;
            if(plan==null)return;
            try{preview.Image=PlanImage.Preview(BridgeValues.Text(plan,"image"));}
            catch(Exception){info.Text="Изображение повреждено. Можно удалить этот план и добавить исправленный PNG/JPEG.";}
        }
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
                    busy=true;add.Enabled=remove.Enabled=save.Enabled=list.Enabled=false;UseWaitCursor=true;
                    try{byte[] image=await Task.Run(()=>PlanImage.Read(open.FileName));Result=PortablePlans.Add(Result,name.Text,image);RefreshList();}
                    catch(Exception e){MessageBox.Show(this,e.Message,"План не добавлен",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
                    finally{busy=false;UseWaitCursor=false;save.Enabled=list.Enabled=true;add.Enabled=list.Items.Count<PortablePlans.MaxPlans;remove.Enabled=list.SelectedIndex>=0;}
                }
            }
        }
        private void Remove()
        {
            var plan=Selected();if(plan==null)return;
            if(MessageBox.Show(this,"Удалить план и его отметки из Windows-проекта? Устройства останутся. Уже загруженную копию в APK удалите отдельно.","Удалить план",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
            Result=PortablePlans.Remove(Result,BridgeValues.Text(plan,"id"));RefreshList();
        }
    }
}
