// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Core.Adapters;
using Core.Commands.Params;
using Core.Common;
using Engine;
using Engine.Commands;
using StreletcProGUI.Commands;

namespace Streletz.MasterBridge
{
    // Exact vendor 12.4 IL is pinned by VersionGuard. InitDevice.ProcessCommand
    // calls its normal two-argument PreCommandCheck before constructing InitDu
    // with !SilentMode. That overload dispatches to the four-argument virtual
    // method below. We retain normal authorization/adapter checks there, then
    // let the native constructor omit only its successful-result message.
    // No readonly field, registered command, or global Master setting changes.
    internal sealed class NativeInitDeviceCommand : InitDevice
    {
        private readonly OperationState operation;
        private readonly Guid recordId;
        private readonly NativeCommandResultMode resultMode;
        private bool bodyInvoked;

        private NativeInitDeviceCommand(InitDevice registered, OperationState operation, Guid recordId)
            : base(registered.Key)
        {
            Guid = registered.Guid;
            ApplyMaintenance = registered.ApplyMaintenance;
            SilentMode = false;
            this.operation = operation;
            this.recordId = recordId;
            resultMode = new NativeCommandResultMode(this);
        }

        internal static object Create(object registered, OperationState operation)
        {
            Guid recordId;
            if (registered == null || registered.GetType() != typeof(InitDevice) || operation == null ||
                operation.Request == null || operation.NativeCommandStarted || operation.ConfirmationSent ||
                !System.Guid.TryParse(operation.Request.recordId, out recordId) || recordId == System.Guid.Empty)
                throw NativeCommandResultMode.Unavailable();

            InitDevice native = (InitDevice)registered;
            MethodInfo body = typeof(InitDevice).GetMethod("ProcessCommand", BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(ICollection<PairGuids>), typeof(BaseParams) }, null);
            MethodInfo check = typeof(BaseProgrammCommand).GetMethod("PreCommandCheck", BindingFlags.NonPublic | BindingFlags.Instance,
                null, new[] { typeof(Guid), typeof(bool), typeof(bool), typeof(TryGetDuplicateAdapterEnum) }, null);
            if (native.Guid != InitDevice.CommandID || native.Guid == System.Guid.Empty ||
                body == null || body.DeclaringType != typeof(InitDevice) || !body.IsVirtual || body.IsFinal || body.ReturnType != typeof(void) ||
                check == null || check.DeclaringType != typeof(BaseProgrammCommand) || !check.IsFamily ||
                !check.IsVirtual || check.IsFinal || check.ReturnType != typeof(CommandCheckResult))
                throw NativeCommandResultMode.Unavailable();

            return new NativeInitDeviceCommand(native, operation, recordId);
        }

        public override void ProcessCommand(ICollection<PairGuids> nodesGuid, BaseParams parameters)
        {
            if (operation.CancellationRequested)
            {
                if (!operation.ConfirmationSent) operation.SetStatus("cancelled", "cancelled_before_native_confirmation");
                return;
            }
            if (operation.IsTerminal) return;
            PairGuids[] records = nodesGuid == null ? null : nodesGuid.ToArray();
            if (bodyInvoked || operation.ConfirmationSent || !operation.NativeCommandStarted || operation.NativeRunFinished || records == null ||
                records.Length != 1 || records[0] == null || records[0].NodeGuid != recordId)
                throw NativeCommandResultMode.Unavailable();
            bodyInvoked = true;
            resultMode.RunBody(() => base.ProcessCommand(records, parameters));
        }

        protected override CommandCheckResult PreCommandCheck(Guid guid, bool openAdapter, bool foundKsg,
            TryGetDuplicateAdapterEnum tryGetDuplicateAdapter = TryGetDuplicateAdapterEnum.AdapterFromDuplicate)
        {
            // This covers nested/terminal adapter checks as well. A failure or
            // exception is returned unchanged, and restoration runs in finally.
            return resultMode.RunPrecheck(() => base.PreCommandCheck(guid, openAdapter, foundKsg, tryGetDuplicateAdapter));
        }
    }
}
