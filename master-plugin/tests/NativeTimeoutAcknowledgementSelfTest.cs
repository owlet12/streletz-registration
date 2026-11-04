// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using System.Drawing;
using Streletz.MasterBridge;
internal static class NativeTimeoutAcknowledgementSelfTest
{
    private static readonly IntPtr Owner = new IntPtr(10), Popup = new IntPtr(20), Button = new IntPtr(21);
    private const string Body = "Истекло время ожидания готовности ДУ. Повторить инициализацию?", Caption = "Ошибка";
    private static int checks;
    [STAThread] private static int Main()
    {
        try {
            var op = Operation(); var api = new FakeWindows(); var gate = new NativeTimeoutAcknowledgement(api); int proofs = 0;
            Check(gate.TryDecline(op, Owner, Body, Caption, () => { proofs++; return true; }) && proofs == 2 && api.MessageReads == 2 && api.Sends == 1, "two proofs, two snapshots, one decline");
            Check(api.SentButton == Button && api.SentPopup == Popup && op.Status == "unknown" && op.ErrorCode == NativeTimeoutAcknowledgement.Code && !op.NativeRunFinished && !op.RecordConfirmed, "exact NO; no fabricated hardware or completion result");
            Check(!gate.TryDecline(op, Owner, Body, Caption, () => true) && api.Sends == 1, "one-shot");
            Action<OperationState>[] invalid = { x => x.NativeCommandStarted=false, x=>x.NativeRunFinished=true, x=>x.ConfirmationSent=false, x=>x.NativeSuccessObserved=true, x=>x.RecordConfirmed=true, x=>x.Status="registered", x=>x.Status="failed", x=>x.Status="cancelled", x=>x.Status="waiting_sensor", x=>x.Request.expectedHardwareId="123" };
            foreach(var change in invalid) { op=Operation();api=new FakeWindows();change(op);Check(!new NativeTimeoutAcknowledgement(api).TryDecline(op,Owner,Body,Caption,()=>true)&&api.Sends==0,"invalid operation untouched"); }
            Action<FakeWindows>[] malformed = {
                x=>x.OwnerWindow.ProcessId++, x=>x.OwnerWindow.ThreadId++, x=>x.OwnerWindow.Visible=false,
                x=>x.Message.ProcessId++, x=>x.Message.ThreadId++, x=>x.Message.Owner=new IntPtr(99), x=>x.Message.ClassName="Custom",
                x=>x.Message.Text="Warning", x=>x.Message.Enabled=false, x=>x.Message.Visible=false, x=>x.Message.Exists=false,
                x=>x.Message.Children[0].Text="Another question", x=>x.Message.Children[0].Visible=false,
                x=>x.Message.Children[1].ControlId=1, x=>x.Message.Children[1].Enabled=false, x=>x.Message.Children[1].Visible=false,
                x=>x.Message.Children[1].ButtonKind=3, x=>x.Message.Children[1].Parent=new IntPtr(99), x=>x.Message.Children[1].ThreadId++, x=>x.Message.Children[1].ProcessId++,
                x=>x.Message.Children[2].ControlId=2, x=>x.Message.Children[2].Handle=Button,
                x=>x.Message.Children.Add(x.Message.Children[2]), x=>x.Message.Children[0].ClassName="Edit",
                x=>x.ExtraOwnedWindow=true, x=>x.Enumerated=null, x=>x.Message.Children=null
            };
            foreach(var change in malformed) { op=Operation();api=new FakeWindows();change(api);Check(!new NativeTimeoutAcknowledgement(api).TryDecline(op,Owner,Body,Caption,()=>true)&&api.Sends==0&&!op.NativeTimeoutAcknowledgementAttempted,"unrecognized window untouched"); }
            op=Operation();api=new FakeWindows();proofs=0;Check(!new NativeTimeoutAcknowledgement(api).TryDecline(op,Owner,Body,Caption,()=>++proofs==1)&&api.Sends==0,"target changed during discovery");
            op=Operation();api=new FakeWindows();api.BeforeMessageRead=n=>{if(n==2)api.Message.Children[1].Handle=new IntPtr(89);};Check(!new NativeTimeoutAcknowledgement(api).TryDecline(op,Owner,Body,Caption,()=>true)&&api.Sends==0,"NO handle changed");
            op=Operation();api=new FakeWindows();gate=new NativeTimeoutAcknowledgement(api);api.DuringSend=()=>gate.TryDecline(op,Owner,Body,Caption,()=>true);Check(gate.TryDecline(op,Owner,Body,Caption,()=>true)&&api.Sends==1,"nested timer cannot dispatch twice");
            op=Operation();api=new FakeWindows{SendSucceeds=false};gate=new NativeTimeoutAcknowledgement(api);Check(!gate.TryDecline(op,Owner,Body,Caption,()=>true)&&op.Status=="unknown"&&!op.NativeTimeoutDeclined,"failed dispatch leaves unknown");Check(!gate.TryDecline(op,Owner,Body,Caption,()=>true)&&api.Sends==1,"failed dispatch not retried");
            op=Operation();api=new FakeWindows();Check(!new NativeTimeoutAcknowledgement(api).TryDecline(op,Owner,"@+IntDU.TimeError",Caption,()=>true)&&api.Sends==0,"unresolved localization untouched");
            RealMessageBox();
            Console.WriteLine("PASS "+checks+" timeout assertions (synthetic windows and own Win32 fixture; no Master or hardware)"); return 0;
        } catch(Exception e) {Console.Error.WriteLine("FAIL "+e);return 1;}
    }
    private static void RealMessageBox()
    {
        using(var owner=new Form {ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-20000,-20000),Size=new Size(100,100)})
        using(var timer=new Timer {Interval=50}) {
            owner.Show(); var op=Operation();var api=new NativeSuccessWindowApi();var gate=new NativeTimeoutAcknowledgement(api);bool sent=false;DateTime deadline=DateTime.UtcNow.AddSeconds(5);
            timer.Tick+=(s,e)=> { if(!sent)sent=gate.TryDecline(op,owner.Handle,Body,Caption,()=>true);if(DateTime.UtcNow>deadline) {timer.Stop();owner.Close();} };
            timer.Start();var answer=MessageBox.Show(owner,Body,Caption,MessageBoxButtons.YesNo,MessageBoxIcon.Error);timer.Stop();
            Check(sent&&answer==DialogResult.No&&op.Status=="unknown"&&!op.NativeRunFinished,"real Win32 modal returns NO without success");owner.Close();
        }
    }
    private static OperationState Operation() { return new OperationState { Request=new StartRequest {expectedHardwareId="112233AABBCC"},Status="registering",NativeCommandStarted=true,ConfirmationSent=true }; }
    private static void Check(bool ok,string label) {if(!ok)throw new Exception(label);checks++;}
    private sealed class FakeWindows : INativeTimeoutWindowApi
    {
        internal NativeSuccessWindow OwnerWindow = new NativeSuccessWindow { Handle = Owner, Exists = true, Visible = true, ProcessId = 5, ThreadId = 7 };
        internal NativeSuccessWindow Message = new NativeSuccessWindow { Handle = Popup, Owner = Owner, Exists = true, Visible = true, Enabled = true,
            ProcessId = 5, ThreadId = 7, ClassName = "#32770", Text = Caption, OkButton = Button, Children = new List<NativeSuccessWindow> {
                new NativeSuccessWindow { Handle = new IntPtr(22), Parent = Popup, Exists = true, Visible = true, Enabled = true, ProcessId = 5, ThreadId = 7, ClassName = "Static", Text = Body },
                new NativeSuccessWindow { Handle = Button, Parent = Popup, Exists = true, Visible = true, Enabled = true, ProcessId = 5, ThreadId = 7, ClassName = "Button", ControlId = 7, ButtonKind = 0 }
            } };
        internal FakeWindows() { Message.Children.Add(new NativeSuccessWindow { Handle = new IntPtr(23), Parent = Popup, Exists = true, Visible = true, Enabled = true, ProcessId = 5, ThreadId = 7, ClassName = "Button", ControlId = 6, ButtonKind = 1 }); }
        public bool SendOk(IntPtr message, IntPtr button) { throw new Exception("Unexpected OK"); }
        internal IList<IntPtr> Enumerated = new List<IntPtr> { Owner, Popup };
        internal int Sends, Reads, MessageReads;
        internal IntPtr SentPopup, SentButton;
        internal bool SendSucceeds = true, ExtraOwnedWindow;
        internal Action DuringSend;
        internal Action<int> BeforeMessageRead;
        public uint CurrentProcessId { get { Reads++; return 5; } }
        public uint CurrentThreadId { get { Reads++; return 7; } }
        public NativeSuccessWindow ReadIdentity(IntPtr handle)
        {
            Reads++;
            if (handle == Owner) return Clone(OwnerWindow);
            if (handle == Popup) return Clone(Message);
            if (handle == new IntPtr(30) && ExtraOwnedWindow) return new NativeSuccessWindow { Handle = handle, Exists = true, Visible = true, Owner = Owner, ProcessId = 5, ThreadId = 7 };
            return new NativeSuccessWindow { Handle = handle };
        }
        public IList<IntPtr> EnumerateThreadWindows(uint thread)
        {
            Reads++; if (Enumerated == null) return null;
            var values = Enumerated.ToList(); if (ExtraOwnedWindow) values.Add(new IntPtr(30)); return values;
        }
        public NativeSuccessWindow ReadMessage(IntPtr handle)
        {
            Reads++; MessageReads++; if (BeforeMessageRead != null) BeforeMessageRead(MessageReads);
            return handle == Popup ? Clone(Message) : ReadIdentity(handle);
        }
        public bool SendNo(IntPtr message, IntPtr button)
        {
            Sends++; SentPopup = message; SentButton = button; if (DuringSend != null) DuringSend(); return SendSucceeds;
        }
        private static NativeSuccessWindow Clone(NativeSuccessWindow value)
        {
            return new NativeSuccessWindow { Handle = value.Handle, Owner = value.Owner, Parent = value.Parent, OkButton = value.OkButton,
                ProcessId = value.ProcessId, ThreadId = value.ThreadId, Exists = value.Exists, Visible = value.Visible, Enabled = value.Enabled,
                ClassName = value.ClassName, Text = value.Text, ControlId = value.ControlId, ButtonKind = value.ButtonKind,
                Children = value.Children == null ? null : value.Children.Select(Clone).ToList() };
        }
    }
}
