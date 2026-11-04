// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;

namespace Streletz.MasterBridge
{
    internal static class NativeDeviceCatalog
    {
        // Master 12.4 Core.DeviceTypes.DevicesCollection: key "ИБ1 ПРО" is
        // (128,98). Plugin.StreletcPro registers that exact key for IB1.Info;
        // Plugin.StreletcProGUI registers the standard InitDevice for it.
        // The distinct ИБ1-ПРО исп. 2 (128,95) is not included implicitly.
        internal static bool IsSupported(int type, int subtype, object info, Func<Type> resolveIb1Type)
        {
            if (type != 128) return false;
            if (subtype == 0 || subtype == 17 || subtype == 32 || subtype == 77 || subtype == 94) return true;
            if (subtype != 98 || info == null || resolveIb1Type == null) return false;
            Type ib1Type = resolveIb1Type();
            return ib1Type != null && info.GetType() == ib1Type;
        }
    }
}
