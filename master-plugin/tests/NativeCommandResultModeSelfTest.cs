// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Collections.Generic;
using Core.Commands;
using Core.Commands.Params;
using Core.Common;
using Streletz.MasterBridge;

internal static class NativeCommandResultModeSelfTest
{
    private static int checks;

    private static int Main()
    {
        try
        {
            var command = new TestCommand();
            var other = new TestCommand { SilentMode = true, ApplyMaintenance = true };
            Guid identity = Guid.NewGuid();
            command.Guid = identity;
            var mode = new NativeCommandResultMode(command);
            Check(!command.SilentMode, "A new own command starts with normal native checks.");
            var nativeOutcome = new object();
            object outcome = mode.RunPrecheck(() => {
                Check(!command.SilentMode, "Preparation outside ProcessCommand keeps native UI/check behavior.");
                return nativeOutcome;
            });
            Check(Object.ReferenceEquals(outcome, nativeOutcome) && !command.SilentMode,
                "An outer check returns its exact native result and preserves normal mode.");

            int bodies = 0, prechecks = 0;
            mode.RunBody(() => {
                bodies++;
                Check(command.SilentMode, "Inside ProcessCommand the InitDu constructor receives a suppressed success message.");
                bool accepted = mode.RunPrecheck(() => {
                    prechecks++;
                    Check(!command.SilentMode, "The actual native precheck runs with normal mode even inside the suppressed body.");
                    object nested = mode.RunPrecheck(() => {
                        prechecks++;
                        Check(!command.SilentMode, "A terminal or nested native precheck also runs in normal mode.");
                        return nativeOutcome;
                    });
                    Check(Object.ReferenceEquals(nested, nativeOutcome) && !command.SilentMode,
                        "A nested check preserves its native result and the surrounding normal check mode.");
                    return false;
                });
                Check(!accepted && command.SilentMode, "A refused native precheck remains refused; only the body mode is restored.");
            });
            Check(bodies == 1 && prechecks == 2 && !command.SilentMode,
                "Exactly one body and the requested checks execute; completion restores normal mode.");
            Check(command.Guid == identity && !command.ApplyMaintenance && other.SilentMode && other.ApplyMaintenance,
                "Mode scopes leave command identity, maintenance options and another command untouched.");

            var expectedError = new InvalidOperationException("synthetic-precheck-failure");
            mode.RunBody(() => {
                Exception caught = Catch(() => mode.RunPrecheck<object>(() => { throw expectedError; }));
                Check(Object.ReferenceEquals(caught, expectedError) && command.SilentMode,
                    "A precheck exception remains the original exception and restores the body mode.");
            });
            Check(!command.SilentMode, "Normal mode is restored after a body that handled a precheck failure.");

            Exception propagated = Catch(() => mode.RunBody(() => mode.RunPrecheck<object>(() => { throw expectedError; })));
            Check(Object.ReferenceEquals(propagated, expectedError) && !command.SilentMode,
                "An unhandled native precheck failure propagates unchanged and restores normal mode.");
            propagated = Catch(() => mode.RunBody(() => { throw expectedError; }));
            Check(Object.ReferenceEquals(propagated, expectedError) && !command.SilentMode,
                "A native body failure restores normal mode without converting the failure to success.");
            mode.RunBody(() => Check(command.SilentMode, "The same private scope is usable after exception unwinding."));
            Check(!command.SilentMode, "A subsequent completed body again restores normal mode.");

            int reentrantBodies = 0;
            mode.RunBody(() => {
                Exception rejected = Catch(() => mode.RunBody(() => reentrantBodies++));
                Check(IsRejected(rejected) && reentrantBodies == 0 && command.SilentMode,
                    "Reentrant ProcessCommand is refused before another body, without altering the active scope.");
            });
            Check(!command.SilentMode, "Reentrancy rejection leaves normal mode after the original body returns.");

            command.SilentMode = true;
            int unexpectedBodies = 0, unexpectedChecks = 0;
            Check(IsRejected(Catch(() => mode.RunBody(() => unexpectedBodies++))) && unexpectedBodies == 0 && command.SilentMode,
                "Unexpected silent state before a body is rejected, never used for native registration.");
            Check(IsRejected(Catch(() => mode.RunPrecheck(() => ++unexpectedChecks))) && unexpectedChecks == 0 && command.SilentMode,
                "Unexpected silent state outside a body cannot reach native checks.");
            command.SilentMode = false;

            propagated = Catch(() => mode.RunPrecheck<object>(() => { throw expectedError; }));
            Check(Object.ReferenceEquals(propagated, expectedError) && !command.SilentMode,
                "Preparation failure outside ProcessCommand remains visible and restores normal mode.");

            mode.RunBody(() => {
                mode.RunPrecheck(() => {
                    Exception nestedFailure = Catch(() => mode.RunPrecheck<object>(() => { throw expectedError; }));
                    Check(Object.ReferenceEquals(nestedFailure, expectedError) && !command.SilentMode,
                        "A nested failed precheck cannot accidentally enable silent mode in an outer check.");
                    return false;
                });
                Check(command.SilentMode, "Only leaving the outermost precheck restores suppressed result mode.");
            });
            Check(!command.SilentMode, "Nested failures leave the command in normal mode after the body.");

            Check(Catch(() => new NativeCommandResultMode(null)) is ArgumentNullException,
                "A missing command fails before any mode change.");
            Check(Catch(() => mode.RunBody(null)) is ArgumentNullException && !command.SilentMode,
                "A missing native body cannot enter a mode scope.");
            Check(Catch(() => mode.RunPrecheck<object>(null)) is ArgumentNullException && !command.SilentMode,
                "A missing native precheck cannot alter mode.");

            Console.WriteLine("PASS " + checks + " native result-mode assertions. Production scope, synthetic BaseCommand only; no InitDu, application manager, adapter, network or hardware calls.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL " + error.GetType().Name + ": " + error.Message); return 1; }
    }

    private static Exception Catch(Action action)
    {
        try { action(); return null; }
        catch (Exception error) { return error; }
    }

    private static bool IsRejected(Exception error)
    {
        BridgeException bridge = error as BridgeException;
        return bridge != null && bridge.Code == "native_result_mode_unavailable";
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
        checks++;
        Console.WriteLine("PASS " + message);
    }

    private sealed class TestCommand : BaseCommand
    {
        internal TestCommand() : base("synthetic-result-mode") { }
        public override string GetIconKey() { return ""; }
        public override BaseParams GetParametrs(ICollection<PairGuids> records) { return null; }
        public override void ProcessCommand(ICollection<PairGuids> records, BaseParams parameters)
        {
            throw new InvalidOperationException("A synthetic command must never execute a native body.");
        }
    }
}
