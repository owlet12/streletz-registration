// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace StreletzBridge
{
    internal static class PermissionTests
    {
        static int checks;
        [STAThread] static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "flood")
            {
                Task a = Task.Run(() => { for (int i = 0; i < 600; i++) Console.Error.Write(new string('e', 2048)); });
                for (int i = 0; i < 600; i++) Console.Out.Write(new string('o', 2048)); a.Wait(); return 7;
            }
            if (args.Length > 0 && args[0] == "hang")
            { File.WriteAllText(args[1], Process.GetCurrentProcess().Id.ToString()); Thread.Sleep(60000); return 0; }
            try
            {
                int code;
                string result = HttpGatewayWindows.Run(Application.ExecutablePath, "flood", out code, 10000);
                Check(code == 7, "concurrent large stdout and stderr do not deadlock; exit code retained");
                Check(result.Length == 65536 && result.All(c => c == 'o'), "output retained with bounded memory");
                string pidFile = Path.Combine(Path.GetTempPath(), "bridge-permission-test-" + Guid.NewGuid().ToString("N") + ".txt");
                Stopwatch elapsed = Stopwatch.StartNew();
                try { HttpGatewayWindows.Run(Application.ExecutablePath, "hang \"" + pidFile + "\"", out code, 1500); throw new Exception("timeout expected"); }
                catch (TimeoutException) { Check(elapsed.ElapsedMilliseconds < 6000, "hung command has bounded wait"); }
                Check(File.Exists(pidFile), "timeout test child did start");
                int pid = Int32.Parse(File.ReadAllText(pidFile)); File.Delete(pidFile);
                bool exited; try { using (Process process = Process.GetProcessById(pid)) exited = process.HasExited; } catch (ArgumentException) { exited = true; }
                Check(exited, "only owned timed-out child terminated");
                Check(HttpGatewayWindows.ResultForExit(0, false).Success, "success requires exit zero");
                Check(!HttpGatewayWindows.ResultForExit(3, false).Success, "timeout never reported successful");
                Check(HttpGatewayWindows.ResultForExit(3, false).Message.Contains("частично"), "partial outcome explained on timeout");
                Check(!HttpGatewayWindows.ResultForExit(1, false).Success && !HttpGatewayWindows.ResultForExit(4, false).Success, "failure and privilege denial not success");
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                Dialog(args.Length > 0 ? args[0] : null);
                Console.WriteLine("PASS " + checks + " permission regression checks; fake permission executor and disposable child processes; no UAC/firewall/URL ACL changes."); return 0;
            }
            catch (Exception error) { Console.WriteLine("FAIL " + error); return 1; }
        }
        static void Dialog(string image)
        {
            using (ManualResetEventSlim release = new ManualResetEventSlim(false))
            {
                int calls = 0; int worker = 0; int ui = Thread.CurrentThread.ManagedThreadId;
                var session = new PermissionSession((port, remove) => {
                    Interlocked.Increment(ref calls); worker = Thread.CurrentThread.ManagedThreadId;
                    if (!release.Wait(10000)) throw new TimeoutException();
                    return new PermissionResult(true, remove ? "Удалено" : "Разрешено");
                });
                using (var first = Create(session))
                {
                    var watch = Stopwatch.StartNew(); Button(first, "Разрешить").PerformClick();
                    Check(watch.ElapsedMilliseconds < 1000, "click returns while helper waits");
                    Pump(() => calls == 1);
                    Check(worker != ui, "permission work runs outside UI thread");
                    Check(!Button(first, "Разрешить").Enabled && !Button(first, "Удалить").Enabled && !Button(first, "Применить").Enabled, "duplicate and apply disabled while pending");
                    Check(All(first).OfType<NumericUpDown>().All(c => !c.Enabled), "port cannot change mid-operation");
                    Check(session.Start(24000, true) == session.Current && session.Current.Port == 23020 && calls == 1, "session retains pending operation across other requests");
                    int ticks = 0;
                    using (var timer = new System.Windows.Forms.Timer { Interval = 20 })
                    { timer.Tick += delegate { ticks++; }; timer.Start(); Pump(() => ticks >= 3); }
                    Check(ticks >= 3, "UI message loop remains responsive while helper is blocked");
                    if (image != null)
                        using (var bitmap = new Bitmap(first.Width, first.Height)) { first.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height)); bitmap.Save(image); }
                    Button(first, "Закрыть").PerformClick();
                    Check(!first.Visible, "dialog can close without waiting or killing helper");
                }
                using (var second = Create(session))
                {
                    Check(!Button(second, "Разрешить").Enabled && calls == 1, "reopened dialog joins existing operation");
                    release.Set(); Pump(() => Button(second, "Разрешить").Enabled);
                    Check(All(second).OfType<Label>().Any(c => c.Text.Contains("Разрешено")), "result is displayed in parent dialog");
                    Check(calls == 1 && Button(second, "Применить").Enabled, "controls restored without extra dispatch");
                    Check(All(second).Where(c => c.Parent is FlowLayoutPanel).All(c => c.Bottom <= c.Parent.ClientSize.Height && c.Right <= c.Parent.ClientSize.Width), "status and buttons fit dialog");
                    Button(second, "Удалить").PerformClick(); Pump(() => calls == 2 && Button(second, "Разрешить").Enabled);
                    Check(!All(second).OfType<CheckBox>().Single().Checked, "successful removal unchecks HTTP");
                }
                var failed = new PermissionSession((port, remove) => { throw new IOException("synthetic secret exception"); });
                using (var dialog = Create(failed))
                {
                    Button(dialog, "Разрешить").PerformClick(); Pump(() => Button(dialog, "Разрешить").Enabled);
                    Check(All(dialog).OfType<Label>().Any(c => c.Text.Contains("не подтверждено")), "worker failure reported without hidden modal dialog");
                    Check(!All(dialog).OfType<Label>().Any(c => c.Text.Contains("secret")), "internal error text not exposed");
                }
            }
        }
        static HttpGatewayDialog Create(PermissionSession session)
        {
            var dialog = new HttpGatewayDialog(new HttpGatewaySettings { Enabled = true }, "192.168.0.244", permissions: session);
            dialog.StartPosition = FormStartPosition.Manual; dialog.Location = new Point(-10000, -10000);
            dialog.ShowInTaskbar = false; dialog.Opacity = 0; dialog.Show(); Application.DoEvents(); return dialog;
        }
        static Button Button(Control root, string prefix) { return All(root).OfType<Button>().Single(c => c.Text.StartsWith(prefix)); }
        static IEnumerable<Control> All(Control root) { foreach (Control child in root.Controls) { yield return child; foreach (Control item in All(child)) yield return item; } }
        static void Pump(Func<bool> done) { Stopwatch w = Stopwatch.StartNew(); while (!done() && w.ElapsedMilliseconds < 5000) { Application.DoEvents(); Thread.Sleep(10); } if (!done()) throw new Exception("UI wait timed out"); Application.DoEvents(); }
        static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    }
}
