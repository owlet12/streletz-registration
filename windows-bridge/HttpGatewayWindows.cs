// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Windows.Forms;
using System.Threading.Tasks;
using System.Text;
using System.ComponentModel;

namespace StreletzBridge
{
    internal static class HttpGatewayWindows
    {
        // Invoked only by the explicit button, elevated once. No project/password access in this mode.
        public static int Configure(string portText, string sidText, bool remove)
        {
            int port = Int32.Parse(portText, CultureInfo.InvariantCulture); HttpGateway.ValidatePort(port);
            string sid = new SecurityIdentifier(sidText).Value;
            if (!sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) && !sid.StartsWith("S-1-12-1-", StringComparison.Ordinal))
                throw new InvalidDataException("Требуется SID пользователя Windows, под которым запущен мост.");
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                throw new UnauthorizedAccessException("Подтвердите разрешение администратора Windows.");
            string prefix = HttpGateway.Prefix(port);
            string netsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe");
            int result;
            string existing = Run(netsh, "http show urlacl url=" + prefix, out result);
            bool reserved = existing.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) >= 0;
            if (reserved && existing.IndexOf(sid, StringComparison.OrdinalIgnoreCase) < 0)
                throw new IOException("Этот HTTP-адрес зарезервирован для другого пользователя. Выберите другой порт.");
            bool added = false;
            if (!remove && !reserved)
            {
                Run(netsh, "http add urlacl url=" + prefix + " sddl=D:(A;;GX;;;" + sid + ")", out result);
                if (result != 0) throw new IOException("Windows не разрешила приём HTTP на выбранном порту.");
                added = true;
            }
            try
            {
                string name = "StreletzBridge-KeenDNS-HTTP-" + port;
                string script = "$ErrorActionPreference='Stop'; $r=Get-NetFirewallRule -Name '" + name + "' -ErrorAction SilentlyContinue; ";
                if (remove) script += "if($r){$r|Remove-NetFirewallRule}";
                else script += "if($r){$r|Set-NetFirewallRule -Enabled True -Direction Inbound -Action Allow -Profile Any; "
                    + "$r|Get-NetFirewallPortFilter|Set-NetFirewallPortFilter -Protocol TCP -LocalPort " + port + " -RemotePort Any; "
                    + "$r|Get-NetFirewallAddressFilter|Set-NetFirewallAddressFilter -RemoteAddress LocalSubnet -LocalAddress Any} "
                    + "else{New-NetFirewallRule -Name '" + name + "' -DisplayName 'Streletz Bridge KeenDNS HTTP " + port
                    + "' -Direction Inbound -Action Allow -Enabled True -Protocol TCP -LocalPort " + port + " -RemoteAddress LocalSubnet -Profile Any | Out-Null}";
                string encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
                string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell/v1.0/powershell.exe");
                Run(powershell, "-NoProfile -NonInteractive -EncodedCommand " + encoded, out result);
                if (result != 0) throw new IOException("Не удалось настроить правило брандмауэра. Проверьте права администратора.");
                if (remove && reserved)
                {
                    Run(netsh, "http delete urlacl url=" + prefix, out result);
                    if (result != 0) throw new IOException("Не удалось удалить разрешение HTTP-порта.");
                }
            }
            catch
            {
                if (added) { try { Run(netsh, "http delete urlacl url=" + prefix, out result); } catch { /* Preserve original failure; outcome may be partial. */ } }
                throw;
            }
            return 0;
        }
        // Read BOTH pipes concurrently. A full stderr pipe must never stall stdout/WaitForExit.
        internal static string Run(string file, string args, out int code, int timeoutMs = 20000)
        {
            using (Process process = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false,
                CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true }))
            {
                Task<string> output = Drain(process.StandardOutput), error = Drain(process.StandardError);
                Stopwatch elapsed = Stopwatch.StartNew();
                if (!process.WaitForExit(timeoutMs) || !Task.WaitAll(new Task[] { output, error },
                    Math.Max(0, timeoutMs - (int)elapsed.ElapsedMilliseconds)))
                {
                    try { if (!process.HasExited) process.Kill(); } catch { }
                    try { process.WaitForExit(2000); } catch { }
                    // Observe faults from stream disposal without holding the caller indefinitely.
                    Task.WhenAll(output, error).ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    throw new TimeoutException("Windows не завершила настройку порта за отведённое время. Настройки могли примениться частично.");
                }
                code = process.ExitCode; return output.Result;
            }
        }
        private static async Task<string> Drain(StreamReader reader)
        {
            char[] buffer = new char[2048]; StringBuilder text = new StringBuilder(); int count;
            while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                if (text.Length < 65536) text.Append(buffer, 0, Math.Min(count, 65536 - text.Length));
            return text.ToString();
        }
        // Called only on a worker. The elevated helper never opens message boxes.
        internal static PermissionResult RequestPermission(int port, bool remove)
        {
            try
            {
                string sid = WindowsIdentity.GetCurrent().User.Value;
                using (Process process = Process.Start(new ProcessStartInfo(Application.ExecutablePath,
                    "--http-permissions " + port.ToString(CultureInfo.InvariantCulture) + " " + sid + (remove ? " remove" : " enable"))
                    { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden }))
                {
                    // While UAC/helper is pending the dialog can repaint and close. A shared session
                    // prevents another launch, including after closing and reopening the dialog.
                    process.WaitForExit();
                    return ResultForExit(process.ExitCode, remove);
                }
            }
            catch (Win32Exception error) when (error.NativeErrorCode == 1223)
            { return new PermissionResult(false, "Запрос администратора отменён. Разрешение не выдавалось."); }
        }
        internal static PermissionResult ResultForExit(int code, bool remove)
        {
            if (code == 0) return new PermissionResult(true, remove ? "Разрешение удалено. Нажмите «Применить», чтобы сохранить выключенное состояние."
                : "Windows разрешила HTTP-доступ. Нажмите «Применить».");
            if (code == 3) return new PermissionResult(false, "Настройка Windows превысила время ожидания. Изменения могли примениться частично. Проверьте HTTP-доступ перед повтором.");
            if (code == 4) return new PermissionResult(false, "Недостаточно прав администратора Windows. Разрешение не подтверждено.");
            return new PermissionResult(false, "Windows не завершила настройку. Проверьте права и занятость порта. Изменения могли примениться частично.");
        }
    }

    internal sealed class PermissionResult
    {
        public readonly bool Success; public readonly string Message;
        public PermissionResult(bool success, string message) { Success = success; Message = message; }
    }
    internal sealed class PermissionJob
    {
        public readonly int Port; public readonly bool Remove; public readonly DateTime Started = DateTime.UtcNow;
        public readonly Task<PermissionResult> Completion;
        public PermissionJob(int port, bool remove, Func<int, bool, PermissionResult> run)
        {
            Port = port; Remove = remove;
            Completion = Task.Run(() => {
                try { return run(port, remove); }
                catch { return new PermissionResult(false, "Не удалось завершить настройку Windows. Проверьте разрешение администратора; состояние порта не подтверждено."); }
            });
        }
    }
    internal sealed class PermissionSession
    {
        public static readonly PermissionSession Shared = new PermissionSession(HttpGatewayWindows.RequestPermission);
        private readonly object gate = new object(); private readonly Func<int, bool, PermissionResult> run;
        private PermissionJob current;
        public PermissionSession(Func<int, bool, PermissionResult> run) { this.run = run; }
        public PermissionJob Current { get { lock (gate) return current; } }
        public PermissionJob Start(int port, bool remove)
        {
            lock (gate)
            {
                if (current != null && !current.Completion.IsCompleted) return current;
                return current = new PermissionJob(port, remove, run);
            }
        }
    }

    internal sealed class HttpGatewayDialog : Form
    {
        private readonly CheckBox enabled = new CheckBox { Text = "Включать HTTP-доступ вместе с обменом", AutoSize = true };
        private readonly NumericUpDown port = new NumericUpDown { Minimum = 1024, Maximum = 65535, Width = 130 };
        private readonly TextBox address = new TextBox { Width = 530 };
        private readonly Button allow = new Button { Text = "Разрешить в Windows…", Width = 210, Height = 32 };
        private readonly Button revoke = new Button { Text = "Удалить разрешение…", Width = 210, Height = 32 };
        private readonly Button cancel = new Button { Text = "Отмена", Width = 110, Height = 32, DialogResult = DialogResult.Cancel };
        private readonly Button apply = new Button { Text = "Применить", Width = 120, Height = 32 };
        private readonly Label progress = new Label { Width = 565, Height = 76, ForeColor = Color.DarkSlateGray };
        private readonly Timer timer = new Timer { Interval = 500 };
        private readonly Action stopHttp; private readonly PermissionSession permissions;
        private PermissionJob observed; private bool busy;
        public HttpGatewaySettings Selected { get; private set; }
        public HttpGatewayDialog(HttpGatewaySettings settings, string localAddress, Action stopHttp = null, PermissionSession permissions = null)
        {
            this.stopHttp = stopHttp; this.permissions = permissions ?? PermissionSession.Shared;
            Text = "KeenDNS · встроенный HTTP-доступ"; ClientSize = new Size(610, 550);
            StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; Font = new Font("Segoe UI", 10F);
            FlowLayoutPanel form = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18),
                FlowDirection = FlowDirection.TopDown, WrapContents = false };
            Controls.Add(form);
            form.Controls.Add(new Label { Text = "Шлюз встроен в мост. Отдельная программа не нужна.\nНа новом компьютере откройте проект и разрешите HTTP-доступ.", Width = 565, Height = 48 });
            enabled.Checked = settings.Enabled; form.Controls.Add(enabled);
            form.Controls.Add(new Label { Text = "HTTP-порт для правила роутера (протокол HTTP)", AutoSize = true, Margin = new Padding(3, 12, 3, 3) });
            port.Value = settings.Port; form.Controls.Add(port);
            form.Controls.Add(new Label { Text = "Домен KeenDNS для APK (необязательно)", AutoSize = true, Margin = new Padding(3, 12, 3, 3) });
            address.Text = settings.Address; form.Controls.Add(address);
            Label route = new Label { Width = 565, Height = 56, Margin = new Padding(3, 12, 3, 3) };
            Action update = () => route.Text = "На роутере: " + localAddress + " · HTTP · " + port.Value
                + "\nЛокальная проверка: http://" + localAddress + ":" + port.Value + HttpGateway.Route;
            port.ValueChanged += delegate { update(); }; update(); form.Controls.Add(route);
            FlowLayoutPanel permission = new FlowLayoutPanel { Width = 565, Height = 42 };
            permission.Controls.AddRange(new Control[] { allow, revoke }); form.Controls.Add(permission);
            allow.Click += async delegate { await Configure(false); }; revoke.Click += async delegate { await Configure(true); };
            form.Controls.Add(progress);
            FlowLayoutPanel buttons = new FlowLayoutPanel { Width = 565, Height = 42, FlowDirection = FlowDirection.RightToLeft };
            buttons.Controls.AddRange(new Control[] { cancel, apply }); form.Controls.Add(buttons);
            AcceptButton = apply; CancelButton = cancel;
            cancel.Click += delegate { Close(); };
            apply.Click += delegate
            {
                if (busy) return;
                try
                {
                    HttpGateway.ValidatePort((int)port.Value);
                    Selected = new HttpGatewaySettings { Enabled = enabled.Checked, Port = (int)port.Value,
                        Address = HttpGatewaySettings.NormalizeAddress(address.Text) };
                    DialogResult = DialogResult.OK; Close();
                }
                catch (Exception error) { progress.Text = error.Message; }
            };
            timer.Tick += delegate { ShowWaiting(); };
            Shown += async delegate { PermissionJob job = this.permissions.Current; if (job != null) await Observe(job); };
        }
        private async Task Configure(bool remove)
        {
            if (busy) return;
            try
            {
                HttpGateway.ValidatePort((int)port.Value);
                PermissionJob pending = permissions.Current;
                if (pending == null || pending.Completion.IsCompleted)
                    if (remove && stopHttp != null) stopHttp();
                await Observe(permissions.Start((int)port.Value, remove));
            }
            catch (Exception error) { if (!IsDisposed) progress.Text = error.Message; }
        }
        private async Task Observe(PermissionJob job)
        {
            observed = job; SetBusy(true); ShowWaiting(); timer.Start();
            PermissionResult result = await job.Completion;
            if (IsDisposed || Disposing) return;
            timer.Stop(); SetBusy(false);
            progress.Text = "Порт " + job.Port + ": " + result.Message;
            if (result.Success && job.Remove && (int)port.Value == job.Port) enabled.Checked = false;
        }
        private void ShowWaiting()
        {
            if (!busy || observed == null) return;
            int seconds = (int)(DateTime.UtcNow - observed.Started).TotalSeconds;
            progress.Text = "Настройка порта " + observed.Port + " · " + seconds + " с. Подтвердите запрос администратора Windows, если он появился.\n"
                + (seconds < 90 ? "Можно закрыть это окно: настройка продолжится, повторный запуск заблокирован."
                    : "Windows пока не завершила операцию. Можно закрыть окно; повторная настройка будет доступна после завершения.");
        }
        private void SetBusy(bool value)
        {
            busy = value; allow.Enabled = revoke.Enabled = apply.Enabled = enabled.Enabled = port.Enabled = address.Enabled = !value;
            cancel.Text = value ? "Закрыть" : "Отмена";
        }
        protected override void Dispose(bool disposing) { if (disposing) timer.Dispose(); base.Dispose(disposing); }
    }
}
