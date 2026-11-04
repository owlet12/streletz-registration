// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Core;
using Core.Common;
using Core.Events;
using Form = System.Windows.Forms.Form;

namespace Streletz.MasterBridge
{
    // Loaded normally by Engine.PluginLoader from Plugin.*.dll in Master's directory.
    public sealed class Plugin : IPlugin
    {
        private static BridgeHost host;
        private static bool scheduled;

        public void RegInfos(IDevFactory factory) { }
        public void RegVisualisers(IDevFactory factory) { }
        public void RegCommands(IDevFactory factory, ApplicationType applicationType) { }
        public void RegProgrammers(IDevFactory factory) { }
        public void RegTreeNodesForInfo(IDevFactory factory) { }
        public void RegEventParser(IEventParserFactory factory) { }

        public void Initialize()
        {
            if (scheduled || Assembly.GetEntryAssembly()?.GetName().Name != "Master") return;
            scheduled = true;
            Application.Idle += OnIdle;
            Application.ApplicationExit += OnApplicationExit;
        }

        private static void OnIdle(object sender, EventArgs e)
        {
            if (host != null) return;
            Form main = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.GetType().FullName == "Master.Main");
            if (main == null || main.IsDisposed || !main.IsHandleCreated) return;
            Application.Idle -= OnIdle;
            host = new BridgeHost(main);
            main.FormClosed += OnMainClosed;
            host.Start();
        }

        private static void OnMainClosed(object sender, FormClosedEventArgs e) { OnApplicationExit(sender, e); }
        private static void OnApplicationExit(object sender, EventArgs e)
        {
            Application.Idle -= OnIdle;
            host?.Dispose();
            host = null;
        }

        // The vendor loader disposes each registration object immediately after Initialize.
        // The independently owned runtime is stopped by the main form / ApplicationExit.
        public void Dispose() { }
    }
}
