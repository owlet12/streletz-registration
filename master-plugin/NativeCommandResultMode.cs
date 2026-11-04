// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using Core.Commands;

namespace Streletz.MasterBridge
{
    // The registered command is never passed here. This scope belongs to one
    // private bridge command. Every native precheck runs with normal UI rules;
    // only the native InitDu constructor sees SilentMode=true.
    internal sealed class NativeCommandResultMode
    {
        private readonly BaseCommand command;
        private bool insideBody;

        internal NativeCommandResultMode(BaseCommand command)
        {
            this.command = command ?? throw new ArgumentNullException("command");
        }

        internal void RunBody(Action nativeBody)
        {
            if (nativeBody == null) throw new ArgumentNullException("nativeBody");
            if (insideBody || command.SilentMode) throw Unavailable();
            insideBody = true;
            try
            {
                command.SilentMode = true;
                nativeBody();
            }
            finally
            {
                command.SilentMode = false;
                insideBody = false;
            }
        }

        internal T RunPrecheck<T>(Func<T> nativePrecheck)
        {
            if (nativePrecheck == null) throw new ArgumentNullException("nativePrecheck");
            if (!insideBody && command.SilentMode) throw Unavailable();
            bool restore = insideBody && command.SilentMode;
            try
            {
                command.SilentMode = false;
                return nativePrecheck();
            }
            finally { command.SilentMode = restore; }
        }

        internal static BridgeException Unavailable()
        {
            return new BridgeException("native_result_mode_unavailable",
                "The native success-result mode could not be isolated from the normal registration checks.");
        }
    }
}
