// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Collections.Generic;
using System.IO;

namespace StreletzBridge
{
    internal sealed class HttpGatewaySettings
    {
        public bool Enabled;
        public int Port = HttpGateway.DefaultPort;
        public string Address = "";
        public static string NormalizeAddress(string text)
        {
            string value = (text ?? "").Trim();
            if (value.Length == 0) return "";
            if (value.IndexOf("://", StringComparison.Ordinal) < 0) value = "https://" + value;
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Scheme != "https"
                || String.IsNullOrEmpty(uri.Host) || !String.IsNullOrEmpty(uri.UserInfo)
                || !String.IsNullOrEmpty(uri.Query) || !String.IsNullOrEmpty(uri.Fragment)
                || (uri.AbsolutePath != "/" && uri.AbsolutePath != HttpGateway.Route))
                throw new InvalidDataException("Укажите HTTPS-домен KeenDNS без логина, параметров и дополнительных путей.");
            return uri.GetLeftPart(UriPartial.Authority) + HttpGateway.Route;
        }
        public static HttpGatewaySettings Load(string file)
        {
            if (!File.Exists(file)) return new HttpGatewaySettings();
            if (new FileInfo(file).Length > 8192) throw new InvalidDataException("Повреждены настройки HTTP-доступа.");
            Dictionary<string, object> value = BridgeJson.ParseObject(File.ReadAllText(file));
            if (value == null || BridgeValues.Number(value, "version") != 1) throw new InvalidDataException("Неизвестная версия настроек HTTP-доступа.");
            long port = BridgeValues.Number(value, "port", HttpGateway.DefaultPort);
            if (port < 1024 || port > 65535) throw new InvalidDataException("Некорректный HTTP-порт.");
            HttpGateway.ValidatePort((int)port);
            return new HttpGatewaySettings { Enabled = BridgeValues.Flag(value, "enabled"), Port = (int)port,
                Address = NormalizeAddress(BridgeValues.Text(value, "address")) };
        }
        public void Save(string file)
        {
            HttpGateway.ValidatePort(Port);
            Address = NormalizeAddress(Address);
            BridgeFiles.WriteAtomic(file, BridgeJson.Serialize(new Dictionary<string, object> {
                { "version", 1 }, { "enabled", Enabled }, { "port", Port }, { "address", Address }
            }));
        }
    }
}
