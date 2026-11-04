// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
#if !BRIDGE_CORE_TESTS
using System.Drawing;
using System.Windows.Forms;
#endif

namespace StreletzBridge
{
    public sealed class PanelProfile
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }
        public string ModuleId { get; set; }
        public string Revision { get; set; }
        public string NativePanelId { get; set; }
        public string SegmentId { get; set; }
        public string Source { get; set; }
        public string RouteKind { get; set; }
        public string AdapterNodeId { get; set; }
        public string AdapterSegmentId { get; set; }
        public string TerminalPanelId { get; set; }
        public string TerminalSegmentId { get; set; }
        public byte[] PasswordMd5 { get; set; }
    }

    internal sealed class PanelProfileStore
    {
        private readonly object gate = new object();
        private readonly string path;
        private readonly Func<byte[], byte[]> protect;
        private readonly Func<byte[], byte[]> unprotect;
        private List<Dictionary<string, object>> values;

        public PanelProfileStore(string path, Func<byte[], byte[]> protect = null, Func<byte[], byte[]> unprotect = null)
        {
            this.path = path;
            this.protect = protect ?? ProtectWindows;
            this.unprotect = unprotect ?? UnprotectWindows;
            values = new List<Dictionary<string, object>>();
            if (!File.Exists(path)) return;
            Dictionary<string, object> root = BridgeJson.ParseObject(File.ReadAllText(path, Encoding.UTF8));
            object[] profiles = BridgeValues.Array(root, "panels");
            if (BridgeValues.Number(root, "version") != 1 || profiles == null)
                throw new InvalidDataException("Повреждён файл профилей панелей.");
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (object item in profiles)
            {
                Dictionary<string, object> row = item as Dictionary<string, object>;
                PanelProfile profile = PublicProfile(row);
                Validate(profile);
                if (!ids.Add(profile.Id))
                    throw new InvalidDataException("Повреждён файл профилей панелей.");
                values.Add(row);
            }
        }

        private static byte[] ProtectWindows(byte[] value)
        {
#if BRIDGE_CORE_TESTS
            throw new PlatformNotSupportedException("Защита профилей DPAPI доступна в Windows.");
#else
            return ProtectedData.Protect(value, Encoding.UTF8.GetBytes("StreletzBridge/panel-profile/v1"), DataProtectionScope.CurrentUser);
#endif
        }

        private static byte[] UnprotectWindows(byte[] value)
        {
#if BRIDGE_CORE_TESTS
            throw new PlatformNotSupportedException("Защита профилей DPAPI доступна в Windows.");
#else
            return ProtectedData.Unprotect(value, Encoding.UTF8.GetBytes("StreletzBridge/panel-profile/v1"), DataProtectionScope.CurrentUser);
#endif
        }

        public PanelProfile[] List()
        {
            lock (gate) return values.Select(PublicProfile).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }

        public PanelProfile Get(string id, bool includeSecret = false)
        {
            lock (gate)
            {
                Dictionary<string, object> row = values.FirstOrDefault(p => String.Equals(BridgeValues.Text(p, "panelId"), id, StringComparison.OrdinalIgnoreCase));
                if (row == null) throw new InvalidDataException("Выбранная панель отсутствует в Windows-мосте.");
                PanelProfile result = PublicProfile(row);
                // The native Master connection owns authentication. Legacy DPAPI
                // material is retained, but is not decrypted for this driver path.
                if (!includeSecret || BridgeValues.Text(row, "passwordDpapi").Length == 0) return result;
                try { result.PasswordMd5 = unprotect(Convert.FromBase64String(BridgeValues.Text(row, "passwordDpapi"))); }
                catch { throw new InvalidDataException("Не удалось прочитать пароль выбранной панели. Задайте его заново в Windows-мосте."); }
                if (result.PasswordMd5 == null || result.PasswordMd5.Length != 16)
                    throw new InvalidDataException("Повреждён пароль выбранной панели.");
                return result;
            }
        }

        public string Save(PanelProfile input, string connectionPassword)
        {
            if (input == null) throw new ArgumentNullException("input");
            PanelProfile profile = new PanelProfile {
                Id = String.IsNullOrEmpty(input.Id) ? Guid.NewGuid().ToString("D") : input.Id,
                Name = (input.Name ?? "").Trim(), Host = (input.Host ?? "").Trim(), Port = input.Port,
                ModuleId = (input.ModuleId ?? "").Trim().ToUpperInvariant(), Revision = Guid.NewGuid().ToString("D")
            };
            Validate(profile);
            lock (gate)
            {
                Dictionary<string, object> previous = values.FirstOrDefault(p => String.Equals(BridgeValues.Text(p, "panelId"), profile.Id, StringComparison.OrdinalIgnoreCase));
                if (previous != null && BridgeValues.Text(previous, "source") == "project")
                    throw new InvalidDataException("Эта панель получена из проекта. Измените её маршрут в Стрелец-Мастере и обновите список.");
                if (values.Any(p => !String.Equals(BridgeValues.Text(p, "panelId"), profile.Id, StringComparison.OrdinalIgnoreCase)
                    && (String.Equals(BridgeValues.Text(p, "moduleId"), profile.ModuleId, StringComparison.OrdinalIgnoreCase)
                    || (String.Equals(BridgeValues.Text(p, "host"), profile.Host, StringComparison.OrdinalIgnoreCase) && BridgeValues.Number(p, "port") == profile.Port))))
                    throw new InvalidDataException("Этот МК-IP или адрес подключения уже есть в списке панелей.");
                string encryptedPassword;
                if (String.IsNullOrEmpty(connectionPassword))
                {
                    encryptedPassword = previous == null ? "" : BridgeValues.Text(previous, "passwordDpapi");
                }
                else
                {
                    if (connectionPassword.Length > 128 || connectionPassword.Any(c => c < 32 || c > 126))
                        throw new InvalidDataException("Пароль МК-IP должен содержать от 1 до 128 ASCII-символов.");
                    byte[] digest;
                    using (MD5 md5 = MD5.Create()) digest = md5.ComputeHash(Encoding.ASCII.GetBytes(connectionPassword));
                    encryptedPassword = Convert.ToBase64String(protect(digest));
                    System.Array.Clear(digest, 0, digest.Length);
                }
                Dictionary<string, object> row = new Dictionary<string, object> {
                    { "panelId", profile.Id }, { "name", profile.Name }, { "host", profile.Host }, { "port", profile.Port },
                    { "moduleId", profile.ModuleId }, { "revision", profile.Revision }, { "passwordDpapi", encryptedPassword }
                };
                List<Dictionary<string, object>> replacement = values.Where(p => !String.Equals(BridgeValues.Text(p, "panelId"), profile.Id, StringComparison.OrdinalIgnoreCase)).ToList();
                replacement.Add(row);
                Persist(replacement);
                values = replacement;
                return profile.Id;
            }
        }

        public void Delete(string id)
        {
            lock (gate)
            {
                List<Dictionary<string, object>> replacement = values.Where(p => !String.Equals(BridgeValues.Text(p, "panelId"), id, StringComparison.OrdinalIgnoreCase)).ToList();
                if (replacement.Count == values.Count) throw new InvalidDataException("Панель уже удалена.");
                Persist(replacement);
                values = replacement;
            }
        }

        public void SynchronizeProjectPanels(IEnumerable<Dictionary<string, object>> catalog, ISet<string> activePanelIds)
        {
            lock (gate)
            {
                var replacement = values.Select(BridgeValues.Copy).ToList();
                bool changed = false;
                foreach (var item in catalog)
                {
                    string id = BridgeValues.Text(item, "projectPanelId");
                    var old = replacement.FirstOrDefault(p => String.Equals(BridgeValues.Text(p, "panelId"), id, StringComparison.OrdinalIgnoreCase));
                    // Never repurpose an explicit profile or change the route of
                    // a running operation. The caller marks route drift unavailable.
                    if (old != null && BridgeValues.Text(old, "source") != "project") continue;
                    if (activePanelIds != null && activePanelIds.Contains(id)) continue;
                    var profile = new PanelProfile {
                        Id = id, Name = BridgeValues.Text(item, "name"), Host = BridgeValues.Text(item, "host"),
                        Port = (int)BridgeValues.Number(item, "port"), ModuleId = BridgeValues.Text(item, "moduleId"),
                        NativePanelId = BridgeValues.GuidValue(BridgeValues.Text(item, "nativePanelId"), "панель проекта"),
                        SegmentId = BridgeValues.GuidValue(BridgeValues.Text(item, "segmentId"), "сегмент проекта"), Source = "project",
                        RouteKind = PanelRouteMetadata.Kind(item), AdapterNodeId = BridgeValues.Text(item, "adapterNodeId"),
                        AdapterSegmentId = BridgeValues.Text(item, "adapterSegmentId"), TerminalPanelId = BridgeValues.Text(item, "terminalPanelId"),
                        TerminalSegmentId = BridgeValues.Text(item, "terminalSegmentId")
                    };
                    Validate(profile);
                    if (old != null && MatchesCatalog(PublicProfile(old), item) && BridgeValues.Text(old, "name") == profile.Name) continue;
                    var row = new Dictionary<string, object> {
                        { "panelId", profile.Id }, { "name", profile.Name }, { "host", profile.Host }, { "port", profile.Port },
                        { "moduleId", profile.ModuleId }, { "revision", Guid.NewGuid().ToString("D") },
                        { "nativePanelId", profile.NativePanelId }, { "segmentId", profile.SegmentId }, { "source", "project" },
                        { "routeKind", profile.RouteKind }, { "adapterNodeId", profile.AdapterNodeId }, { "adapterSegmentId", profile.AdapterSegmentId },
                        { "terminalPanelId", profile.TerminalPanelId }, { "terminalSegmentId", profile.TerminalSegmentId },
                        { "passwordDpapi", old == null ? "" : BridgeValues.Text(old, "passwordDpapi") }
                    };
                    if (old != null) replacement.Remove(old);
                    replacement.Add(row); changed = true;
                }
                if (!changed) return;
                Persist(replacement);
                values = replacement;
            }
        }

        public static bool MatchesCatalog(PanelProfile profile, Dictionary<string, object> item)
        {
            return profile != null && profile.Source == "project" &&
                String.Equals(profile.Id, BridgeValues.Text(item, "projectPanelId"), StringComparison.OrdinalIgnoreCase) &&
                String.Equals(profile.NativePanelId, BridgeValues.Text(item, "nativePanelId"), StringComparison.OrdinalIgnoreCase) &&
                String.Equals(profile.SegmentId, BridgeValues.Text(item, "segmentId"), StringComparison.OrdinalIgnoreCase) &&
                String.Equals(profile.Host, BridgeValues.Text(item, "host"), StringComparison.OrdinalIgnoreCase) &&
                profile.Port == BridgeValues.Number(item, "port") &&
                String.Equals(profile.ModuleId, BridgeValues.Text(item, "moduleId"), StringComparison.OrdinalIgnoreCase) &&
                PanelRouteMetadata.Matches(profile, item);
        }

        private void Persist(List<Dictionary<string, object>> replacement)
        {
            BridgeFiles.WriteAtomic(path, BridgeJson.Serialize(new Dictionary<string, object> { { "version", 1 }, { "panels", replacement.ToArray() } }));
        }

        private static PanelProfile PublicProfile(Dictionary<string, object> row)
        {
            return new PanelProfile {
                Id = BridgeValues.Text(row, "panelId"), Name = BridgeValues.Text(row, "name"), Host = BridgeValues.Text(row, "host"),
                Port = (int)BridgeValues.Number(row, "port"), ModuleId = BridgeValues.Text(row, "moduleId"), Revision = BridgeValues.Text(row, "revision"),
                NativePanelId = BridgeValues.Text(row, "nativePanelId"), SegmentId = BridgeValues.Text(row, "segmentId"), Source = BridgeValues.Text(row, "source"),
                RouteKind = PanelRouteMetadata.Kind(row), AdapterNodeId = BridgeValues.Text(row, "adapterNodeId"),
                AdapterSegmentId = BridgeValues.Text(row, "adapterSegmentId"), TerminalPanelId = BridgeValues.Text(row, "terminalPanelId"),
                TerminalSegmentId = BridgeValues.Text(row, "terminalSegmentId")
            };
        }

        private static void Validate(PanelProfile profile)
        {
            profile.Id = BridgeValues.GuidValue(profile.Id, "панель");
            if (profile.Source == "project")
            {
                profile.NativePanelId = BridgeValues.GuidValue(profile.NativePanelId, "панель проекта");
                profile.SegmentId = BridgeValues.GuidValue(profile.SegmentId, "сегмент проекта");
                if (profile.Id != ProjectPanelCatalog.StableId(profile.NativePanelId, profile.SegmentId))
                    throw new InvalidDataException("Повреждена связь профиля с панелью проекта.");
                if (profile.RouteKind == PanelRouteMetadata.Terminal)
                {
                    profile.AdapterNodeId = BridgeValues.GuidValue(profile.AdapterNodeId, "адаптер терминала");
                    profile.AdapterSegmentId = BridgeValues.GuidValue(profile.AdapterSegmentId, "сегмент адаптера терминала");
                    profile.TerminalPanelId = BridgeValues.GuidValue(profile.TerminalPanelId, "Панель-Терминал");
                    profile.TerminalSegmentId = BridgeValues.GuidValue(profile.TerminalSegmentId, "сегмент терминала");
                    if (profile.AdapterSegmentId != profile.TerminalSegmentId || profile.SegmentId == profile.TerminalSegmentId
                        || profile.NativePanelId == profile.TerminalPanelId)
                        throw new InvalidDataException("Повреждена связь профиля с Панелью-Терминалом.");
                }
                else if (!String.IsNullOrEmpty(profile.RouteKind) && profile.RouteKind != PanelRouteMetadata.Direct)
                    throw new InvalidDataException("Неподдерживаемый маршрут профиля панели.");
            }
            if (String.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 100)
                throw new InvalidDataException("Введите название панели до 100 символов.");
            IPAddress address;
            if (String.IsNullOrWhiteSpace(profile.Host) || !IPAddress.TryParse(profile.Host, out address))
                throw new InvalidDataException("Введите IP-адрес МК-IP без схемы и пути, как в подключении Стрелец-Мастера.");
            profile.Host = address.ToString();
            if (profile.Port < 1 || profile.Port > 65535) throw new InvalidDataException("Некорректный TCP-порт панели.");
            if (!Regex.IsMatch(profile.ModuleId ?? "", "^[0-9A-F]{12}$")) throw new InvalidDataException("ID МК-IP должен содержать 12 шестнадцатеричных символов.");
        }
    }

    internal sealed class PanelCatalogRow
    {
        public string Id, Name, Host, ModuleId, Status, Route;
        public int Port, RecordCount;
        public bool ReadOnly;

        public static PanelCatalogRow[] Build(IEnumerable<Dictionary<string, object>> catalog, IEnumerable<PanelProfile> profiles)
        {
            var rows = new List<PanelCatalogRow>();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var panel in catalog)
            {
                string id = BridgeValues.Text(panel, "projectPanelId");
                if (!ids.Add(id)) continue;
                rows.Add(new PanelCatalogRow {
                    Id = id, Name = BridgeValues.Text(panel, "name"), Host = BridgeValues.Text(panel, "host"),
                    Port = (int)BridgeValues.Number(panel, "port"), ModuleId = BridgeValues.Text(panel, "moduleId"), ReadOnly = true,
                    RecordCount = (BridgeValues.Array(panel, "recordIds") ?? new object[0]).Length,
                    Route = BridgeValues.Text(panel, "routeDisplayName"),
                    Status = BridgeValues.Flag(panel, "available") ? "Маршрут проверен в текущем проекте Master."
                        : BridgeValues.Text(panel, "availableReason") + (BridgeValues.Text(panel, "routeIssue").Length > 0 ? " " + BridgeValues.Text(panel, "routeIssue") : "")
                });
            }
            foreach (var profile in profiles)
            {
                if (ids.Contains(profile.Id) || profile.Source == "project") continue;
                rows.Add(new PanelCatalogRow {
                    Id = profile.Id, Name = profile.Name, Host = profile.Host, Port = profile.Port,
                    ModuleId = profile.ModuleId, ReadOnly = false, RecordCount = 0,
                    Status = "Ручной профиль подключения. Принадлежность датчиков определяется исходным проектом."
                });
            }
            return rows.ToArray();
        }

        public override string ToString()
        {
            return Name + (ReadOnly ? " · устройств " + RecordCount : " · ручной профиль") +
                (String.IsNullOrEmpty(Route) ? "" : " · " + Route) +
                (String.IsNullOrEmpty(Host) ? "" : " · " + Host + (Port > 0 ? ":" + Port : ""));
        }
    }

#if !BRIDGE_CORE_TESTS
    internal sealed class PanelProfilesDialog : Form
    {
        private readonly PanelProfileStore store;
        private readonly RegistrationCoordinator coordinator;
        private readonly Action readBfx;
        private readonly ListBox list = new ListBox();
        private readonly TextBox name = new TextBox();
        private readonly TextBox host = new TextBox();
        private readonly NumericUpDown port = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = 6344 };
        private readonly TextBox projectPort = new TextBox { ReadOnly = true };
        private readonly TextBox module = new TextBox { CharacterCasing = CharacterCasing.Upper };
        private readonly TextBox hint = new TextBox { ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None };
        private readonly Label catalogInfo = new Label { AutoEllipsis = true };
        private readonly Button save = new Button { Text = "Сохранить профиль" };
        private readonly Button remove = new Button { Text = "Удалить профиль" };
        private readonly Button read = new Button { Text = "Прочитать BFX…" };
        private readonly Button check = new Button { Text = "Проверить связь" };
        private string selectedId;
        private PanelCatalogRow[] rows = new PanelCatalogRow[0];
        private bool refreshing;

        public PanelProfilesDialog(PanelProfileStore store, RegistrationCoordinator coordinator, Action readBfx)
        {
            this.store = store; this.coordinator = coordinator; this.readBfx = readBfx;
            Text = "Панели проекта · Стрелец 0.5.10"; StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(920, 520); MinimumSize = new Size(920, 560);
            Font = new Font("Segoe UI", 9F);
            catalogInfo.SetBounds(14, 14, 890, 22); catalogInfo.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            list.SetBounds(14, 42, 340, 350); list.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Bottom; list.HorizontalScrollbar = true;
            Controls.Add(catalogInfo); Controls.Add(list);
            string[] labels = { "Название панели", "IP-адрес МК-IP", "TCP-порт", "ID МК-IP" };
            Control[] inputs = { name, host, port, module };
            for (int i = 0; i < labels.Length; i++)
            {
                Label label = new Label { Text = labels[i], Left = 376, Top = 42 + i * 46, Width = 300, Height = 20 };
                inputs[i].SetBounds(376, 62 + i * 46, 528, 25); inputs[i].Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
                Controls.Add(label); Controls.Add(inputs[i]);
            }
            projectPort.Bounds = port.Bounds; projectPort.Anchor = port.Anchor; projectPort.Visible = false; Controls.Add(projectPort);
            hint.SetBounds(376, 255, 528, 137); hint.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right | AnchorStyles.Bottom;
            Controls.Add(hint);
            read.SetBounds(14, 408, 160, 31); read.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            check.SetBounds(186, 408, 168, 31); check.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            Button diagnostics = new Button { Text = "Диагностика связи", Left = 376, Top = 408, Width = 180, Height = 31, Anchor = AnchorStyles.Left | AnchorStyles.Bottom };
            Button create = new Button { Text = "Ручной профиль…", Left = 14, Top = 474, Width = 160, Height = 31, Anchor = AnchorStyles.Left | AnchorStyles.Bottom };
            remove.SetBounds(186, 474, 168, 31); remove.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            save.SetBounds(604, 474, 170, 31); save.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            Button close = new Button { Text = "Закрыть", Left = 786, Top = 474, Width = 118, Height = 31, Anchor = AnchorStyles.Right | AnchorStyles.Bottom, DialogResult = DialogResult.Cancel };
            Label sourceHint = new Label { Text = "Список берётся из файла проекта. Связь с Master нужна для регистрации.", Left = 14, Top = 445, Width = 890, Height = 22, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom };
            Controls.AddRange(new Control[] { read, check, diagnostics, create, remove, save, close, sourceHint });
            CancelButton = close;
            create.Click += delegate { ClearEditor(); };
            list.SelectedIndexChanged += delegate { if (!refreshing) ShowSelection(); };
            read.Click += delegate {
                try { if (this.readBfx != null) this.readBfx(); RefreshList(null); }
                catch (Exception error) { MessageBox.Show(this, error.Message, "Каталог не прочитан", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            };
            check.Click += async delegate {
                check.Enabled = false; read.Enabled = false; check.Text = "Проверка…";
                try
                {
                    var response = await System.Threading.Tasks.Task.Run(() => coordinator.Handle(new Dictionary<string, object> {
                        { "protocol", 3 }, { "type", "list_panels" }, { "requestId", Guid.NewGuid().ToString("D") }, { "sentAt", BridgeValues.NowMs() }
                    }));
                    if (IsDisposed || Disposing) return;
                    RefreshList(selectedId);
                    hint.Text = BridgeValues.Text(response, "message") + Environment.NewLine + Environment.NewLine + hint.Text;
                }
                catch (Exception error) { if (!IsDisposed) hint.Text = "Связь не подтверждена. " + error.Message; }
                finally { if (!IsDisposed) { check.Enabled = true; read.Enabled = true; check.Text = "Проверить связь"; } }
            };
            diagnostics.Click += delegate { ShowDiagnostics(); };
            save.Click += delegate {
                try
                {
                    string id = selectedId;
                    coordinator.RunPanelMutation(selectedId, delegate {
                        id = store.Save(new PanelProfile { Id = selectedId, Name = name.Text, Host = host.Text, Port = (int)port.Value, ModuleId = module.Text }, null);
                    });
                    RefreshList(id);
                }
                catch (Exception error) { MessageBox.Show(this, error.Message, "Профиль не сохранён", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            };
            remove.Click += delegate {
                if (String.IsNullOrEmpty(selectedId)) return;
                if (MessageBox.Show(this, "Удалить выбранный ручной профиль панели?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                try { coordinator.RunPanelMutation(selectedId, delegate { store.Delete(selectedId); }); RefreshList(null); }
                catch (Exception error) { MessageBox.Show(this, error.Message, "Профиль не удалён", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            };
            RefreshList(null);
        }

        private void ClearEditor()
        {
            list.ClearSelected(); selectedId = null; name.Text = ""; host.Text = ""; port.Value = 6344; module.Text = "";
            name.ReadOnly = host.ReadOnly = module.ReadOnly = false; port.Visible = true; projectPort.Visible = false;
            save.Enabled = true; remove.Enabled = false;
            hint.Text = "Ручной профиль не создаёт принадлежность датчиков. Панели и связи датчиков загружаются из исходного BFX.";
            name.Focus();
        }

        private void ShowSelection()
        {
            if (list.SelectedIndex < 0 || list.SelectedIndex >= rows.Length) return;
            var row = rows[list.SelectedIndex]; selectedId = row.Id;
            name.Text = row.Name; host.Text = row.Host; module.Text = row.ModuleId;
            name.ReadOnly = host.ReadOnly = module.ReadOnly = row.ReadOnly;
            port.Visible = !row.ReadOnly; projectPort.Visible = row.ReadOnly;
            port.Value = row.Port > 0 && row.Port <= 65535 ? row.Port : 6344;
            projectPort.Text = row.Port > 0 ? row.Port.ToString() : "Не указан в проекте";
            save.Enabled = !row.ReadOnly; remove.Enabled = !row.ReadOnly;
            hint.Text = (row.ReadOnly ? "Панель из проекта. Устройств: " + row.RecordCount + Environment.NewLine : "") +
                (String.IsNullOrEmpty(row.Route) ? "" : row.Route + Environment.NewLine) + row.Status +
                Environment.NewLine + Environment.NewLine + (row.ReadOnly ? "Название, состав и маршрут меняются в исходном проекте. Для обновления прочитайте BFX повторно. " : "") +
                "Пароль МК-IP и вход пользователя задаются в Master.";
        }

        private void RefreshList(string select)
        {
            refreshing = true;
            rows = PanelCatalogRow.Build(coordinator.DisplayPanels(), store.List()); list.Items.Clear();
            foreach (var row in rows) list.Items.Add(row);
            catalogInfo.Text = "Панелей из проекта: " + rows.Count(row => row.ReadOnly) + "; ручных профилей: " + rows.Count(row => !row.ReadOnly);
            int index = Array.FindIndex(rows, row => row.Id == select);
            if (rows.Length > 0) list.SelectedIndex = index >= 0 ? index : 0;
            refreshing = false;
            if (rows.Length > 0) ShowSelection();
            else
            {
                name.Text = host.Text = module.Text = ""; selectedId = null;
                name.ReadOnly = host.ReadOnly = module.ReadOnly = true; port.Visible = false; projectPort.Visible = true;
                projectPort.Text = "—"; save.Enabled = remove.Enabled = false;
                hint.Text = "В открытом переносимом проекте нет каталога панелей. Нажмите «Прочитать BFX…» и выберите исходный проект с теми же записями устройств. Список читается без соединения с Master.";
            }
        }

        private void ShowDiagnostics()
        {
            using (var dialog = new Form { Text = "Диагностика связи с Master", StartPosition = FormStartPosition.CenterParent, Size = new Size(850, 600), MinimumSize = new Size(600, 400) })
            {
                var text = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill,
                    Font = new Font("Consolas", 9F), Text = coordinator.ConnectionDiagnosticReport };
                var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.RightToLeft };
                var close = new Button { Text = "Закрыть", DialogResult = DialogResult.Cancel, Width = 110, Height = 30 };
                var saveLog = new Button { Text = "Сохранить…", Width = 130, Height = 30 };
                saveLog.Click += delegate {
                    using (var file = new SaveFileDialog { Filter = "Текстовый отчёт (*.txt)|*.txt", FileName = "streletz-connection-diagnostics.txt" })
                    {
                        if (file.ShowDialog(dialog) != DialogResult.OK) return;
                        try { coordinator.SaveConnectionDiagnostics(file.FileName); }
                        catch (Exception error) { MessageBox.Show(dialog, error.Message, "Отчёт не сохранён", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                    }
                };
                bar.Controls.Add(close); bar.Controls.Add(saveLog); dialog.Controls.Add(text); dialog.Controls.Add(bar);
                dialog.CancelButton = close; dialog.ShowDialog(this);
            }
        }
    }

#endif
}
