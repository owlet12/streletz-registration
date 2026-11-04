// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Streletz.MasterBridge
{
    internal sealed class NativeSuccessWindowApi : INativeTimeoutWindowApi
    {
        public uint CurrentProcessId { get { return GetCurrentProcessId(); } }
        public uint CurrentThreadId { get { return GetCurrentThreadId(); } }

        public NativeSuccessWindow ReadIdentity(IntPtr handle)
        {
            var value = new NativeSuccessWindow { Handle = handle, Exists = handle != IntPtr.Zero && IsWindow(handle) };
            if (!value.Exists) return value;
            uint process;
            value.ThreadId = GetWindowThreadProcessId(handle, out process);
            value.ProcessId = process;
            value.Owner = GetWindow(handle, 4); // GW_OWNER, immediate owner only.
            value.Parent = GetParent(handle);
            value.Visible = IsWindowVisible(handle);
            value.Enabled = IsWindowEnabled(handle);
            var name = new StringBuilder(256);
            if (GetClassName(handle, name, name.Capacity) == 0) { value.Exists = false; return value; }
            value.ClassName = name.ToString();
            return value;
        }

        public IList<IntPtr> EnumerateThreadWindows(uint threadId)
        {
            var handles = new List<IntPtr>();
            bool ok = EnumThreadWindows(threadId, (handle, parameter) => {
                if (handles.Count >= 1024) return false;
                handles.Add(handle); return true;
            }, IntPtr.Zero);
            return ok ? handles : null;
        }

        public NativeSuccessWindow ReadMessage(IntPtr handle)
        {
            NativeSuccessWindow value = ReadIdentity(handle);
            if (!value.Exists) return value;
            value.Text = Text(handle);
            value.OkButton = GetDlgItem(handle, 1); // IDOK.
            bool overflow = false;
            EnumChildWindows(handle, (child, parameter) => {
                if (value.Children.Count >= 32) { overflow = true; return false; }
                NativeSuccessWindow item = ReadIdentity(child);
                item.Text = Text(child);
                item.ControlId = GetDlgCtrlID(child);
                item.ButtonKind = GetWindowLong(child, -16) & 15; // GWL_STYLE / BS_TYPEMASK.
                value.Children.Add(item); return true;
            }, IntPtr.Zero);
            if (overflow) value.Exists = false;
            return value;
        }

        public bool SendOk(IntPtr message, IntPtr button)
        {
            UIntPtr result;
            // WM_COMMAND: LOWORD=IDOK, HIWORD=BN_CLICKED (0), LPARAM=exact
            // validated button HWND. No key injection, activation or broadcast.
            return SendMessageTimeout(message, 0x0111, new IntPtr(1), button, 3, 1000, out result) != IntPtr.Zero;
        }

        public bool SendNo(IntPtr message, IntPtr button)
        {
            UIntPtr result;
            // IDNO / BN_CLICKED, exact owned message and validated button.
            return SendMessageTimeout(message, 0x0111, new IntPtr(7), button, 3, 1000, out result) != IntPtr.Zero;
        }

        private static string Text(IntPtr handle)
        {
            int length = GetWindowTextLength(handle);
            if (length < 0 || length > 4096) return null;
            var text = new StringBuilder(length + 1);
            GetWindowText(handle, text, text.Capacity);
            return text.ToString();
        }

        private delegate bool EnumWindowProc(IntPtr handle, IntPtr parameter);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr handle);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr handle);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowEnabled(IntPtr handle);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr handle, uint command);
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr handle);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr handle, StringBuilder name, int maximum);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumThreadWindows(uint threadId, EnumWindowProc callback, IntPtr parameter);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowProc callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr handle, int controlId);
        [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr handle);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowLong(IntPtr handle, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr handle);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle, StringBuilder text, int maximum);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr handle, uint message, IntPtr parameter, IntPtr data, uint flags, uint timeout, out UIntPtr result);
    }
}
