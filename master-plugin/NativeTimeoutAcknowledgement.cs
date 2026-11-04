// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Linq;

namespace Streletz.MasterBridge
{
    internal interface INativeTimeoutWindowApi : INativeSuccessWindowApi
    {
        bool SendNo(IntPtr message, IntPtr button);
    }

    // Only decline the known readiness-timeout question owned by our InitDu.
    // A decline is not evidence of the device's physical registration state.
    internal sealed class NativeTimeoutAcknowledgement
    {
        internal const string Code = "native_device_ready_timeout";
        internal const string Message = "Истекло время ожидания готовности устройства. Повторная инициализация отклонена. Регистрация не подтверждена; проверьте устройство в Стрелец-Мастере перед новой попыткой.";
        private readonly INativeTimeoutWindowApi windows;
        internal NativeTimeoutAcknowledgement(INativeTimeoutWindowApi windows) { this.windows = windows; }

        internal static bool Eligible(OperationState op)
        {
            return op != null && op.Request != null && op.NativeCommandStarted && !op.NativeRunFinished &&
                op.ConfirmationSent && !op.NativeSuccessObserved && !op.RecordConfirmed &&
                (op.Status == "registering" || op.Status == "unknown") && Hex.IsDeviceId(op.Request.expectedHardwareId) &&
                op.Request.expectedHardwareId != "000000000000" && op.Request.expectedHardwareId != "FFFFFFFFFFFF";
        }

        internal bool TryDecline(OperationState op, IntPtr owner, string text, string caption, Func<bool> verify)
        {
            if (!Eligible(op) || op.NativeTimeoutAcknowledgementAttempted || owner == IntPtr.Zero || verify == null || !verify() ||
                String.IsNullOrWhiteSpace(text) || String.IsNullOrWhiteSpace(caption) || text.StartsWith("@+") || caption.StartsWith("@+")) return false;
            uint process = windows.CurrentProcessId, thread = windows.CurrentThreadId;
            if (!OwnerMatches(windows.ReadIdentity(owner), owner, process, thread)) return false;
            var handles = windows.EnumerateThreadWindows(thread);
            if (handles == null) return false;
            var owned = handles.Select(windows.ReadIdentity).Where(w => w != null && w.Exists && w.Visible && w.Owner == owner).ToArray();
            if (owned.Length != 1) return false;
            NativeSuccessWindow candidate = windows.ReadMessage(owned[0].Handle);
            IntPtr button = NoButton(candidate, owner, process, thread, text, caption);
            if (button == IntPtr.Zero || !Eligible(op) || !verify() ||
                !OwnerMatches(windows.ReadIdentity(owner), owner, process, thread)) return false;
            NativeSuccessWindow current = windows.ReadMessage(candidate.Handle);
            if (current == null || current.Handle != candidate.Handle || NoButton(current, owner, process, thread, text, caption) != button) return false;
            lock (op.Sync)
            {
                if (!Eligible(op) || op.NativeTimeoutAcknowledgementAttempted) return false;
                op.NativeTimeoutAcknowledgementAttempted = true; // before nested UI dispatch
                op.Fail(Code, Message);
            }
            op.NativeTimeoutDeclined = windows.SendNo(current.Handle, button);
            if (!op.NativeTimeoutDeclined)
                op.Fail(Code, "Истекло время ожидания готовности устройства. Автоматически закрыть вопрос о повторе не удалось. Нажмите «Нет» в Стрелец-Мастере и проверьте результат регистрации.");
            return op.NativeTimeoutDeclined;
        }

        internal bool HasVisibleOwnedWindow(IntPtr owner)
        {
            var handles = windows.EnumerateThreadWindows(windows.CurrentThreadId);
            return handles == null || handles.Select(windows.ReadIdentity).Any(w => w != null && w.Exists && w.Visible && w.Owner == owner);
        }

        private static bool OwnerMatches(NativeSuccessWindow w, IntPtr owner, uint process, uint thread)
        {
            return w != null && w.Exists && w.Visible && w.Handle == owner && w.ProcessId == process && w.ThreadId == thread;
        }

        private static IntPtr NoButton(NativeSuccessWindow w, IntPtr owner, uint process, uint thread, string text, string caption)
        {
            if (w == null || !w.Exists || !w.Visible || !w.Enabled || w.Handle == IntPtr.Zero || w.Handle == owner ||
                w.Owner != owner || w.ProcessId != process || w.ThreadId != thread || w.ClassName != "#32770" ||
                w.Text != caption || w.Children == null) return IntPtr.Zero;
            var children = w.Children.ToArray();
            if (children.Any(c => c == null || !c.Exists || c.Parent != w.Handle || c.ProcessId != process || c.ThreadId != thread ||
                !(String.Equals(c.ClassName, "Button", StringComparison.OrdinalIgnoreCase) || String.Equals(c.ClassName, "Static", StringComparison.OrdinalIgnoreCase)))) return IntPtr.Zero;
            var buttons = children.Where(c => c.Visible && String.Equals(c.ClassName, "Button", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (buttons.Length != 2 || buttons.Any(c => !c.Enabled || c.Handle == IntPtr.Zero || (c.ButtonKind != 0 && c.ButtonKind != 1)) ||
                buttons.Count(c => c.ControlId == 6) != 1 || buttons.Count(c => c.ControlId == 7) != 1 || buttons[0].Handle == buttons[1].Handle) return IntPtr.Zero;
            var labels = children.Where(c => c.Visible && String.Equals(c.ClassName, "Static", StringComparison.OrdinalIgnoreCase) && !String.IsNullOrEmpty(c.Text)).ToArray();
            return labels.Length == 1 && labels[0].Text == text ? buttons.Single(c => c.ControlId == 7).Handle : IntPtr.Zero;
        }
    }
}
