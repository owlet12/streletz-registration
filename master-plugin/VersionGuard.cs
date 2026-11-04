// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace Streletz.MasterBridge
{
    internal static class VersionGuard
    {
        // Official 12.4 distribution, statically inspected. No fallback to a different build.
        internal static readonly IDictionary<string, string> Expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            { "Master.exe", "d9c43e7ca593e2233bb0e7dc8b0285974893d9b3e653ca42854ce94f86804e77" },
            { "Core.dll", "e3e17961708143d679bc5aa412bd4c2c5c5d56a392a38ff3309d8da94d37c7e7" },
            { "Engine.dll", "7a7751f9123e1c99ffc20567d59d95ee1ff54553c052bbc96213ea9e13277f00" },
            { "Argus.Configuration.dll", "288ce24727e3531c7102a30db7676f7f23021beb67bc17f145a2f6926c238c2f" },
            { "Argus.GUI.Common.dll", "466f4e34b6945f14f8b51710f38c961508043488ed7202b69f55313fd16e83e6" },
            { "Plugin.StreletcProGUI.dll", "884082ec6dea2cba790363b28632cd739bc654f8dfb59dfcf7405109e6459960" },
            { "Plugin.StreletcPro.dll", "4bd0248b376b83011b1745b5e052f2eb39c6cb38b8c377dd9ce7a5b9968c7916" },
            { "Plugin.MostIPI.dll", "9d74fdda71faa8d6d6a9b2fc38d4b5c1d9ccbde5643ded53b90fa2ad1a0f744d" },
            { "Plugin.CorePlugins.dll", "dc3379542bbd382b956b4ffcab1cf725d8dd5f0318cfd7b3efdf70ae21ee3982" },
            { "Plugin.RROP-LON.dll", "6047f3f7d4e22a1ee5d2f0d99e6291116cf201e52840adb463f3be35f1de2f2a" },
            { "BaseAdapter.dll", "e2780708868caa375f406a562cb4f964133e9a8f6abfaef55a5f2c43f366e804" },
            { "BaseAdapterTransport.dll", "f39a3e197334959f2a9ecb502b42f887e521d0dc3a7722d2f1bfc1d386dceea7" },
            { "UsbEthernetAdapter.dll", "e630152789d3f3448c3da0942d0d7c818678a8cefa8e12be7b3c969e540a7b6a" },
            { "HardwareAdaptersFactory.dll", "24ac37e529b248d99519a2fa6845a39ee53565c3f3cdb735a1712826776ed18c" },
            { "TunnelAdapter.dll", "0fd7c1cd151912c71f7d0717a3b460aad5891443199ebbc68116c081994d1795" }
        };

        internal static void Verify(string directory)
        {
            foreach (var pair in Expected)
            {
                string path = Path.Combine(directory, pair.Key);
                if (!File.Exists(path)) throw new BridgeException("unsupported_master_build", "Required Master 12.4 component is missing: " + pair.Key);
                string digest;
                using (var sha = SHA256.Create())
                using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    digest = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                if (!String.Equals(digest, pair.Value, StringComparison.Ordinal))
                    throw new BridgeException("unsupported_master_build", "Master component does not match the inspected 12.4 build: " + pair.Key);
            }
        }
    }
}
