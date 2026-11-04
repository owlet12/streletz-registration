// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Streletz.MasterBridge
{
    internal sealed class LocalPipeServer : IDisposable
    {
        private readonly Func<string, Task<string>> handler;
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private readonly object sync = new object();
        private NamedPipeServerStream current;
        internal LocalPipeServer(Func<string, Task<string>> callback) { handler = callback; }
        internal void Start() { Task.Run(Loop); }

        private async Task Loop()
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using (var server = CreateLocalPipe())
                    {
                        lock (sync) current = server;
                        await server.WaitForConnectionAsync(stop.Token).ConfigureAwait(false);
                        Task<string> read = ReadLine(server, stop.Token);
                        if (await Task.WhenAny(read, Task.Delay(15000, stop.Token)).ConfigureAwait(false) != read) continue;
                        string request = await read.ConfigureAwait(false);
                        string response = await handler(request).ConfigureAwait(false);
                        byte[] bytes = new UTF8Encoding(false, true).GetBytes(response + "\n");
                        if (bytes.Length > NativeReplyJson.MaximumResponseBytes) throw new IOException("Response exceeds local IPC limit.");
                        await server.WriteAsync(bytes, 0, bytes.Length, stop.Token).ConfigureAwait(false);
                        await server.FlushAsync(stop.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { if (stop.IsCancellationRequested) break; }
                catch { if (!stop.IsCancellationRequested) await Task.Delay(500).ConfigureAwait(false); }
                finally { lock (sync) current = null; }
            }
        }

        private static async Task<string> ReadLine(Stream stream, CancellationToken token)
        {
            using (var bytes = new MemoryStream())
            {
                var buffer = new byte[1024];
                while (bytes.Length <= 32768)
                {
                    int count = await stream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
                    if (count == 0) throw new IOException("Incomplete local IPC request.");
                    for (int i = 0; i < count; i++)
                    {
                        if (buffer[i] == 10)
                        {
                            for (int rest = i + 1; rest < count; rest++)
                                if (buffer[rest] != 10 && buffer[rest] != 13 && buffer[rest] != 32) throw new IOException("One request per pipe connection is required.");
                            return new UTF8Encoding(false, true).GetString(bytes.ToArray()).TrimEnd('\r');
                        }
                        bytes.WriteByte(buffer[i]);
                        if (bytes.Length > 32768) throw new IOException("Local IPC request exceeds 32 KiB.");
                    }
                }
                throw new IOException("Local IPC request exceeds 32 KiB.");
            }
        }

        private static NamedPipeServerStream CreateLocalPipe()
        {
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            SecurityIdentifier sid = WindowsIdentity.GetCurrent().User;
            if (sid == null) throw new IOException("Current Windows account could not be established.");
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
            security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            byte[] descriptor = security.GetSecurityDescriptorBinaryForm();
            IntPtr memory = Marshal.AllocHGlobal(descriptor.Length);
            try
            {
                Marshal.Copy(descriptor, 0, memory, descriptor.Length);
                var attributes = new SecurityAttributes { Length = Marshal.SizeOf(typeof(SecurityAttributes)), Descriptor = memory, Inherit = false };
                string name = @"\\.\pipe\StreletzMasterBridgeV1-" + Process.GetCurrentProcess().SessionId;
                // FILE_FLAG_FIRST_PIPE_INSTANCE prevents silently attaching to another server.
                // PIPE_REJECT_REMOTE_CLIENTS is enforced by Windows in addition to the DACL.
                SafePipeHandle handle = CreateNamedPipe(name, 3u | 0x40000000u | 0x00080000u, 0x00000008u,
                    1, 65536, 65536, 15000, ref attributes);
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    throw new Win32Exception(error, "Unable to create the protected local Master pipe.");
                }
                return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle);
            }
            finally { Marshal.FreeHGlobal(memory); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            internal int Length;
            internal IntPtr Descriptor;
            [MarshalAs(UnmanagedType.Bool)] internal bool Inherit;
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode,
            uint maxInstances, uint outBufferSize, uint inBufferSize, uint timeout, ref SecurityAttributes attributes);

        public void Dispose()
        {
            if (stop.IsCancellationRequested) return;
            stop.Cancel();
            lock (sync) current?.Dispose();
        }
    }
}
