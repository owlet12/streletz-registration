// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using Streletz.MasterBridge;

internal static class NativeDeviceCatalogSelfTest
{
    private class ExpectedIb1 { }
    private sealed class DerivedIb1 : ExpectedIb1 { }
    private sealed class OtherRadio { }
    private static int count;
    private static int Main()
    {
        try
        {
            Func<Type> expected = () => typeof(ExpectedIb1);
            Func<Type> unexpectedLookup = () => { throw new Exception("Unrelated subtype must not resolve IB1."); };
            foreach (int subtype in new[] { 0, 17, 32, 77, 94 })
                Check(NativeDeviceCatalog.IsSupported(128, subtype, null, unexpectedLookup), "Existing radio subtype " + subtype + " retains its catalog membership.");
            Check(NativeDeviceCatalog.IsSupported(128, 98, new ExpectedIb1(), expected), "Exact IB1 type and 128:98 are included.");
            Check(!NativeDeviceCatalog.IsSupported(128, 98, new OtherRadio(), expected), "Another native info type cannot impersonate IB1 using subtype 98.");
            Check(!NativeDeviceCatalog.IsSupported(128, 98, new DerivedIb1(), expected), "An unverified derived info class is not accepted as exact IB1.");
            Check(!NativeDeviceCatalog.IsSupported(128, 98, null, expected), "Missing IB1 info object is rejected.");
            Check(!NativeDeviceCatalog.IsSupported(128, 98, new ExpectedIb1(), () => null), "Unresolved exact vendor type is rejected.");
            Check(!NativeDeviceCatalog.IsSupported(128, 98, new ExpectedIb1(), null), "Missing type resolver is rejected.");
            Check(!NativeDeviceCatalog.IsSupported(128, 95, new ExpectedIb1(), unexpectedLookup), "Distinct IB1 execution 2 is not added by name or similarity.");
            Check(!NativeDeviceCatalog.IsSupported(128, 99, new ExpectedIb1(), unexpectedLookup), "Unverified radio subtype is rejected.");
            Check(!NativeDeviceCatalog.IsSupported(18, 98, new ExpectedIb1(), unexpectedLookup), "Same numeric subtype in another family is rejected.");
            Check(!NativeDeviceCatalog.IsSupported(255, 142, new ExpectedIb1(), unexpectedLookup), "A child output is not included as a physical IB1 radio device.");
            Console.WriteLine("PASS " + count + " native catalog eligibility assertions; no vendor GUI or hardware.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL " + error.Message); return 1; }
    }
    private static void Check(bool ok, string label) { if (!ok) throw new Exception(label); count++; Console.WriteLine("PASS " + label); }
}
