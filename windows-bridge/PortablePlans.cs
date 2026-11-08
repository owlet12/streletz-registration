// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;

namespace StreletzBridge
{
    internal static class PortablePlans
    {
        internal const int MaxImageBytes = 800000, MaxPlans = 8, MaxStateChars = 7000000;
        internal static Dictionary<string, object> Read(Dictionary<string, object> project)
        {
            var source = BridgeValues.Object(project, "fieldwork");
            if (project.ContainsKey("fieldwork") && source == null) throw new InvalidDataException("Повреждены планы проекта.");
            var state = source == null ? new Dictionary<string, object> { { "version", 1 }, { "scans", new object[0] }, { "plans", new object[0] } } : BridgeValues.Copy(source);
            Validate(state); return state;
        }
        internal static void Validate(Dictionary<string, object> state)
        {
            var plans = BridgeValues.Array(state, "plans"); var scans = BridgeValues.Array(state, "scans");
            if (BridgeValues.Number(state, "version") != 1 || plans == null || scans == null) throw new InvalidDataException("Неизвестный формат планов и списка QR.");
            if (plans.Length > MaxPlans || scans.Length > 2000 || BridgeJson.Serialize(state).Length > MaxStateChars) throw new InvalidDataException("Превышен размер планов или списка QR.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in plans)
            {
                var plan = value as Dictionary<string, object>; string id = BridgeValues.Text(plan, "id"), image = BridgeValues.Text(plan, "image");
                if (id.Length == 0 || !ids.Add(id) || image.Length > ((MaxImageBytes + 2) / 3) * 4 || Convert.FromBase64String(image).Length > MaxImageBytes) throw new InvalidDataException("Повреждён план или превышен размер изображения.");
                var points = BridgeValues.Array(plan, "points"); if (points == null || points.Length > 20000) throw new InvalidDataException("Повреждены отметки плана.");
                var records = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in points)
                {
                    var point = item as Dictionary<string, object>; double x,y;
                    if (String.IsNullOrWhiteSpace(BridgeValues.Text(point, "recordId")) || !records.Add(BridgeValues.Text(point, "recordId"))
                        || !Double.TryParse(BridgeValues.Text(point, "x"), NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                        || !Double.TryParse(BridgeValues.Text(point, "y"), NumberStyles.Float, CultureInfo.InvariantCulture, out y)
                        || Double.IsNaN(x) || Double.IsNaN(y) || x < 0 || x > 1 || y < 0 || y > 1) throw new InvalidDataException("Некорректная отметка на плане.");
                }
            }
        }
        internal static Dictionary<string, object> Add(Dictionary<string, object> state, string name, byte[] jpeg)
        {
            Validate(state);
            if (String.IsNullOrWhiteSpace(name) || name.Trim().Length > 120) throw new InvalidDataException("Укажите название плана до 120 символов.");
            if (jpeg == null || jpeg.Length == 0 || jpeg.Length > MaxImageBytes) throw new InvalidDataException("План должен занимать до 800 КБ после обработки.");
            var result = BridgeValues.Copy(state); var plans = BridgeValues.Array(result, "plans").ToList();
            plans.Add(new Dictionary<string, object> { { "id", Guid.NewGuid().ToString("D") }, { "name", name.Trim() }, { "image", Convert.ToBase64String(jpeg) }, { "points", new object[0] } });
            result["plans"] = plans.ToArray(); Validate(result); return result;
        }
        internal static Dictionary<string, object> Remove(Dictionary<string, object> state, string id)
        {
            Validate(state); var result = BridgeValues.Copy(state);
            result["plans"] = BridgeValues.Array(result, "plans").Where(p => BridgeValues.Text(p as Dictionary<string, object>, "id") != id).ToArray();
            Validate(result); return result;
        }
        internal static Dictionary<string, object> Apply(Dictionary<string, object> project, Dictionary<string, object> state)
        {
            Validate(state); var result = BridgeValues.Copy(project); result["fieldwork"] = BridgeValues.Copy(state);
            // Also check the same envelope budget used by automatic project delivery.
            ProjectSyncSnapshot.Reply(result, Guid.NewGuid().ToString("D"), "", "");return result;
        }
    }
}
