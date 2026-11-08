// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Xml;

[assembly: AssemblyVersion("0.5.27.0")]
[assembly: AssemblyFileVersion("0.5.27.0")]

namespace StreletzBridge
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 4 && args[0] == "--http-permissions")
                {
                    if (args[3] != "enable" && args[3] != "remove") throw new ArgumentException("Unknown permission action");
                    return HttpGatewayWindows.Configure(args[1], args[2], args[3] == "remove");
                }
                if (args.Length == 3 && args[0] == "--export-bfx")
                {
                    Dictionary<string, object> project = BfxReader.Read(args[1], Path.GetFileNameWithoutExtension(args[1]));
                    ProjectFiles.Save(args[2], project);
                    Console.WriteLine(ProjectFiles.SummaryText(project));
                    return 0;
                }
                if (args.Length == 2 && args[0] == "--summary")
                {
                    Console.WriteLine(ProjectFiles.SummaryText(ProjectFiles.Load(args[1])));
                    return 0;
                }
                if (args.Length == 4 && args[0] == "--receive-once")
                {
                    Dictionary<string, object> project = ProjectFiles.Load(args[1]);
                    int port = Int32.Parse(args[2], CultureInfo.InvariantCulture);
                    using (LanReceiver receiver = new LanReceiver(port, args[3], delegate(Dictionary<string, object> request)
                    {
                        string message = ProjectFiles.ApplyInitializationJob(project, request);
                        ProjectFiles.Save(args[1], project);
                        return LanReceiver.Response(true, message);
                    }))
                    {
                        Console.WriteLine(receiver.ReceiveOnce());
                    }
                    return 0;
                }

                // Acquire before MainForm field initializers read pairing-code,
                // panels or journal. Hold across shutdown and pending workers.
                using (BridgeStateLease lease = BridgeStateLease.Acquire(BridgeSettings.StateDirectory()))
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new MainForm());
                }
                return 0;
            }
            catch (Exception error)
            {
                Exception actual = error is TargetInvocationException && error.InnerException != null
                    ? error.InnerException : error;
                if (args.Length > 0 && args[0] == "--http-permissions")
                    return actual is TimeoutException ? 3 : actual is UnauthorizedAccessException ? 4 : 1;
                if (Environment.UserInteractive && args.Length == 0)
                    MessageBox.Show(actual.Message, "Стрелец · Мост для Windows", MessageBoxButtons.OK, MessageBoxIcon.Error);
                else
                    Console.Error.WriteLine(actual.ToString());
                return 1;
            }
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly DataGridView grid = new DataGridView();
        private readonly TextBox filter = new TextBox();
        private readonly Label summary = new Label();
        private readonly Label receiverInfo = new Label();
        private readonly Label registrationInfo = new Label();
        private readonly ToolStripStatusLabel status = new ToolStripStatusLabel();
        private readonly Button receiverButton;
        private readonly Label httpInfo = new Label();
        private HttpGateway httpGateway;
        private HttpGatewaySettings httpSettings = new HttpGatewaySettings();
        private string HttpSettingsPath { get { return Path.Combine(BridgeSettings.StateDirectory(), "http-settings.json"); } }
        private readonly string pairingCode = BridgeSettings.LoadPairingCode();
        private Dictionary<string, object> project;
        private string projectPath;
        private List<ViewRow> rows = new List<ViewRow>();
        private LanReceiver receiver;
        private readonly object projectGuard = new object();
        private readonly PanelProfileStore panelProfiles;
        private readonly RegistrationCoordinator registrations;
        private bool closePending;
        private bool allowCloseAfterCancellation;
        private bool exchangeStartedOnce;

        public MainForm()
        {
            string stateDirectory = BridgeSettings.StateDirectory();
            string httpSettingsError = "";
            try { httpSettings = HttpGatewaySettings.Load(HttpSettingsPath); }
            catch (Exception error) { httpSettingsError = "KeenDNS отключён: " + error.Message; }
            panelProfiles = new PanelProfileStore(Path.Combine(stateDirectory, "panels.json"));
            IPanelRegistrationDriver driver = CreateRegistrationDriver();
            registrations = new RegistrationCoordinator(panelProfiles, GetRegistrationProject, SaveRegistrationResult,
                driver, Path.Combine(stateDirectory, "registration-journal.json"), ProtocolV3.JournalIdentityKey(pairingCode),
                savePanelCatalog: SavePanelCatalog);
            registrations.Changed += RefreshRegistrationStatus;
            Text = "Стрелец · Мост для Windows · 0.5.27";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(980, 620);
            Size = new Size(1280, 760);
            Font = new Font("Segoe UI", 9F);

            Panel header = new Panel { Dock = DockStyle.Top, Height = 148, Padding = new Padding(14), BackColor = Color.FromArgb(31, 72, 94) };
            Label title = new Label { Text = "СТРЕЛЕЦ · МОСТ ДЛЯ WINDOWS", ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 17F), AutoSize = true, Left = 14, Top = 11 };
            Label hint = new Label { Text = "Телефон → выбранная панель → регистрация датчика → подтверждение результата", ForeColor = Color.FromArgb(210, 226, 235), AutoSize = true, Left = 16, Top = 45 };
            FlowLayoutPanel actions = new FlowLayoutPanel { Left = 12, Top = 69, Width = 1220, Height = 74, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, WrapContents = true };
            Button exportBfx = MakeButton("Создать проект из BFX", 180);
            Button updateBfx = MakeButton("Обновить из BFX", 150);
            Button open = MakeButton("Открыть проект", 180);
            Button compare = MakeButton("Сравнить с BFX", 150);
            Button saveCopy = MakeButton("Сохранить копию", 145);
            Button csv = MakeButton("Отчёт CSV", 120);
            receiverButton = MakeButton("Включить обмен", 165);
            Button panelsButton = MakeButton("Панели…", 100);
            Button cancelRegistration = MakeButton("Отменить регистрацию", 175);
            Button plansButton = MakeButton("Планы…", 100);
            plansButton.Click += delegate { EditPlans(); };
            Button httpButton = MakeButton("KeenDNS…", 120);
            actions.Controls.AddRange(new Control[] { exportBfx, updateBfx, open, compare, saveCopy, csv, plansButton, panelsButton, receiverButton, httpButton, cancelRegistration });
            httpButton.Click += delegate { ConfigureHttp(); };
            header.Controls.AddRange(new Control[] { title, hint, actions });

            Panel info = new Panel { Dock = DockStyle.Top, Height = 152, Padding = new Padding(14, 8, 14, 8), BackColor = Color.FromArgb(240, 244, 246) };
            summary.AutoSize = false;
            summary.Dock = DockStyle.Top;
            summary.Height = 25;
            summary.Font = new Font("Segoe UI Semibold", 10F);
            summary.Text = "Откройте проект, полученный из APK, или создайте новый из копии BFX.";
            receiverInfo.AutoSize = false;
            receiverInfo.Dock = DockStyle.Top;
            receiverInfo.Height = 24;
            receiverInfo.Text = "Откройте проект для автоматической загрузки на телефон";
            receiverInfo.ForeColor = Color.FromArgb(55, 86, 102);
            registrationInfo.AutoSize = false;
            registrationInfo.AutoEllipsis = true;
            registrationInfo.Dock = DockStyle.Top;
            registrationInfo.Height = 26;
            registrationInfo.Text = registrations.Summary;
            filter.Dock = DockStyle.Bottom;
            filter.Height = 28;
            filter.Text = "";
            info.Controls.Add(summary);
            info.Controls.Add(receiverInfo);
            httpInfo.Dock = DockStyle.Top; httpInfo.Height = 25; httpInfo.AutoEllipsis = true;
            httpInfo.Text = "KeenDNS · HTTP-доступ выключен";
            httpInfo.Cursor = Cursors.Hand;
            httpInfo.Click += delegate {
                if (!String.IsNullOrEmpty(httpSettings.Address)) {
                    try { Clipboard.SetText(httpSettings.Address); status.Text = "HTTPS-адрес для APK скопирован."; }
                    catch { status.Text = "Не удалось открыть буфер обмена."; }
                } else ConfigureHttp();
            };
            info.Controls.Add(httpInfo);
            info.Controls.Add(registrationInfo);
            info.Controls.Add(filter);

            ConfigureGrid();
            StatusStrip strip = new StatusStrip();
            status.Text = String.IsNullOrEmpty(httpSettingsError) ? "Исходный BFX не изменяется" : httpSettingsError;
            strip.Items.Add(status);

            Controls.Add(grid);
            Controls.Add(info);
            Controls.Add(header);
            Controls.Add(strip);

            exportBfx.Click += delegate { ExportBfx(); };
            updateBfx.Click += delegate { UpdateProjectFromBfx(); };
            open.Click += delegate { OpenProject(); };
            compare.Click += delegate { CompareWithBfx(); };
            saveCopy.Click += delegate { SaveProjectCopy(); };
            csv.Click += delegate { SaveCsv(); };
            receiverButton.Click += delegate { ToggleReceiver(); };
            panelsButton.Click += delegate {
                using (PanelProfilesDialog dialog = new PanelProfilesDialog(panelProfiles, registrations, ImportPanelsFromBfx)) dialog.ShowDialog(this);
            };
            cancelRegistration.Click += delegate { registrations.CancelAll(); };
            filter.TextChanged += delegate { ApplyFilter(); };
            FormClosing += CloseAfterRegistrationStops;
            FormClosed += delegate { StopReceiver(); registrations.Dispose(); };
        }

        private void CloseAfterRegistrationStops(object sender, FormClosingEventArgs args)
        {
            if (allowCloseAfterCancellation || !registrations.HasActiveOperations) return;
            args.Cancel = true;
            if (closePending) return;
            closePending = true;
            StopReceiver(); registrations.CancelAll();
            status.Text = "Остановка регистрации перед закрытием Windows-моста…";
            DateTime deadline = DateTime.UtcNow.AddSeconds(12);
            System.Windows.Forms.Timer waitForStop = new System.Windows.Forms.Timer { Interval = 200 };
            waitForStop.Tick += delegate {
                if (registrations.HasActiveOperations && DateTime.UtcNow < deadline) return;
                waitForStop.Stop(); waitForStop.Dispose();
                allowCloseAfterCancellation = true; Close();
            };
            waitForStop.Start();
        }

        private static IPanelRegistrationDriver CreateRegistrationDriver()
        {
            return new PanelRegistrationDriver();
        }

        private Dictionary<string, object> GetRegistrationProject()
        {
            lock (projectGuard) return project == null ? null : BridgeValues.Copy(project);
        }

        private bool SavePanelCatalog(string fingerprint, Dictionary<string, object>[] panels, string nativeFingerprint)
        {
            lock (projectGuard)
            {
                if (project == null || BridgeValues.ProjectFingerprint(project) != fingerprint) return false;
                var updated = BridgeValues.Copy(project);
                ProjectPanelCatalog.Apply(updated, panels, nativeFingerprint);
                if (String.IsNullOrEmpty(projectPath)) return false;
                // Commit the in-memory copy only after the atomic write. Retrying
                // a failed write must not report a false successful save.
                ProjectFiles.Save(projectPath, updated);
                project = updated;
                return true;
            }
        }

        private bool SaveRegistrationResult(RegistrationTarget target, RegistrationDriverResult result)
        {
            lock (projectGuard)
            {
                if (project == null || String.IsNullOrEmpty(projectPath) || BridgeValues.ProjectFingerprint(project) != target.ProjectFingerprint)
                    return false;
                Dictionary<string, object> updated = BridgeValues.Copy(project);
                Dictionary<string, object> row = ProjectFiles.Records(updated).FirstOrDefault(r => String.Equals(ProjectFiles.Text(r, "id"), target.RecordId, StringComparison.OrdinalIgnoreCase));
                if (row == null || !result.RegistrationConfirmed || !String.Equals(result.HardwareId, target.ExpectedHardwareId, StringComparison.OrdinalIgnoreCase)) return false;
                Dictionary<string, object> commissioning = ProjectFiles.Dict(row, "commissioning");
                if (commissioning == null) { commissioning = new Dictionary<string, object>(); row["commissioning"] = commissioning; }
                commissioning["stage"] = 2; commissioning["stageName"] = "Инициализировано";
                commissioning["serial"] = target.ExpectedHardwareId;
                if (!String.IsNullOrEmpty(target.DeviceKey)) commissioning["deviceKey"] = target.DeviceKey;
                commissioning["updatedAt"] = BridgeValues.NowMs();
                commissioning["registrationOperationId"] = target.OperationId;
                commissioning["registrationEvidence"] = result.Evidence;
                row["initialized"] = "+";
                ProjectFiles.Save(projectPath, updated);
                project = updated;
                return true;
            }
        }

        private void RefreshRegistrationStatus()
        {
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(RefreshRegistrationStatus)); } catch (InvalidOperationException) { }
                return;
            }
            registrationInfo.Text = registrations.Summary;
            if (!String.IsNullOrEmpty(registrations.LastStorageProblem)) status.Text = registrations.LastStorageProblem;
            lock (projectGuard)
            {
                if (project != null)
                {
                    rows = ProjectFiles.MakeRows(project); ApplyFilter();
                    summary.Text = ProjectFiles.SummaryText(project) + "   •   " + Path.GetFileName(projectPath);
                }
            }
        }

        private static Button MakeButton(string text, int width)
        {
            return new Button { Text = text, Width = width, Height = 32, Margin = new Padding(3), FlatStyle = FlatStyle.System };
        }

        private void ConfigureGrid()
        {
            grid.Dock = DockStyle.Fill;
            grid.ReadOnly = true;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToOrderColumns = true;
            grid.AutoGenerateColumns = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = false;
            grid.RowHeadersVisible = false;
            grid.BackgroundColor = Color.White;
            grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(246, 249, 250);
            grid.Columns.Add(Column("Address", "Адрес", 68));
            grid.Columns.Add(Column("Name", "Устройство", 150));
            grid.Columns.Add(Column("Description", "Описание", 165));
            grid.Columns.Add(Column("Group", "Группа", 190));
            grid.Columns.Add(Column("Zone", "Зона", 65));
            grid.Columns.Add(Column("Stage", "Этап APK", 135));
            grid.Columns.Add(Column("Serial", "Аппаратный ID", 130));
            grid.Columns.Add(Column("DeviceKey", "Ключ устройства", 120));
            grid.Columns.Add(Column("Note", "Примечание", 220));
            grid.Columns.Add(Column("Comparison", "Сверка с BFX", 180));
        }

        private static DataGridViewTextBoxColumn Column(string property, string title, int width)
        {
            return new DataGridViewTextBoxColumn { DataPropertyName = property, HeaderText = title, Width = width, SortMode = DataGridViewColumnSortMode.Automatic };
        }

        private void ExportBfx()
        {
            using (OpenFileDialog open = new OpenFileDialog())
            {
                open.Filter = "Проект Стрелец-Мастер (*.bfx)|*.bfx|Все файлы (*.*)|*.*";
                open.Title = "Выберите копию проекта BFX";
                if (open.ShowDialog(this) != DialogResult.OK) return;
                using (SaveFileDialog save = new SaveFileDialog())
                {
                    save.Filter = "Проект для APK (*.s3project)|*.s3project";
                    save.FileName = Path.GetFileNameWithoutExtension(open.FileName) + ".s3project";
                    save.Title = "Сохранить проект для телефона";
                    if (save.ShowDialog(this) != DialogResult.OK) return;
                    RunBusy("Чтение BFX…", delegate
                    {
                        Dictionary<string, object> created = BfxReader.Read(open.FileName, Path.GetFileNameWithoutExtension(open.FileName));
                        ProjectFiles.Save(save.FileName, created);
                        LoadIntoGrid(created, save.FileName, false);
                    });
                }
            }
        }

        private void UpdateProjectFromBfx()
        {
            if (!RequireProject()) return;
            using (OpenFileDialog open = new OpenFileDialog())
            {
                open.Filter = "Проект Стрелец-Мастер (*.bfx)|*.bfx|Все файлы (*.*)|*.*";
                open.Title = "Выберите сохранённый BFX этого проекта для обновления состава";
                if (open.ShowDialog(this) != DialogResult.OK) return;
                using (SaveFileDialog save = new SaveFileDialog())
                {
                    save.Filter = "Обновлённая копия проекта (*.s3project)|*.s3project";
                    save.Title = "Новое имя копии: текущий проект и его журнал будут сохранены";
                    save.OverwritePrompt = false;
                    save.FileName = Path.GetFileNameWithoutExtension(projectPath ?? "streletz-project")
                        + "-updated-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".s3project";
                    if (save.ShowDialog(this) != DialogResult.OK) return;
                    RunBusy("Обновление состава из BFX с сохранением журнала…", delegate {
                        long revision;
                        var baseline = registrations.ReadProjectSnapshot(out revision);
                        if (baseline == null) throw new InvalidOperationException("Откройте текущий переносимый проект.");
                        string originalPath;
                        lock (projectGuard) originalPath = projectPath;
                        string fingerprint = BridgeValues.ProjectFingerprint(baseline);
                        string destination = Path.GetFullPath(save.FileName);
                        if ((!String.IsNullOrEmpty(originalPath) && String.Equals(destination, Path.GetFullPath(originalPath), StringComparison.OrdinalIgnoreCase))
                            || File.Exists(destination) || Directory.Exists(destination))
                            throw new InvalidOperationException("Выберите новое имя файла для обновлённой копии. Исходный проект не перезаписывается.");
                        var fresh = BfxReader.Read(open.FileName, Path.GetFileNameWithoutExtension(open.FileName));
                        int added = 0;
                        registrations.RunProjectMutation(revision, delegate {
                            lock (projectGuard)
                            {
                                if (project == null || BridgeValues.ProjectFingerprint(project) != fingerprint ||
                                    !String.Equals(projectPath, originalPath, StringComparison.OrdinalIgnoreCase))
                                    throw new InvalidOperationException("Открытый проект изменился во время чтения BFX. Повторите обновление для текущего проекта.");
                                // Merge the current state under the mutation gate: a worker may
                                // have finished while the BFX was being read. Never publish the
                                // captured older commissioning snapshot over that result.
                                var updated = BfxProjectUpdate.Merge(project, fresh);
                                var updatedRows = ProjectFiles.MakeRows(updated);
                                added = updatedRows.Count - ProjectFiles.MakeRows(project).Count;
                                ProjectFiles.SaveNewCopy(destination, updated);
                                project = updated;
                                projectPath = destination;
                                rows = updatedRows;
                            }
                        });
                        ApplyFilter();
                        lock (projectGuard) summary.Text = ProjectFiles.SummaryText(project) + "   •   " + Path.GetFileName(projectPath);
                        status.Text = "Состав обновлён. Добавлено устройств: " + added
                            + ". Журнал сохранён по GUID; открыта новая копия проекта.";
                    });
                }
            }
        }

        private void OpenProject()
        {
            using (OpenFileDialog open = new OpenFileDialog())
            {
                open.Filter = "Проект APK (*.s3project)|*.s3project|JSON (*.json)|*.json|Все файлы (*.*)|*.*";
                open.Title = "Открыть результат с телефона";
                if (open.ShowDialog(this) != DialogResult.OK) return;
                RunBusy("Открытие проекта…", delegate { LoadIntoGrid(ProjectFiles.Load(open.FileName), open.FileName); });
            }
        }

        private void EditPlans()
        {
            if (!RequireProject()) return;
            RunBusy("Открытие планов…", delegate {
                long revision;var baseline=registrations.ReadProjectSnapshot(out revision);
                string originalPath;lock(projectGuard)originalPath=projectPath;
                string fingerprint=BridgeValues.ProjectFingerprint(baseline);
                using(var dialog=new PortablePlansDialog(PortablePlans.Read(baseline)))
                {
                    UseWaitCursor=false;
                    if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                    registrations.RunProjectMutation(revision,delegate {
                        lock(projectGuard)
                        {
                            if(project==null||fingerprint!=BridgeValues.ProjectFingerprint(project)||!String.Equals(originalPath,projectPath,StringComparison.OrdinalIgnoreCase))
                                throw new InvalidOperationException("Проект изменился. Откройте планы заново.");
                            var updated=PortablePlans.Apply(project,dialog.Result);
                            // Save first: failure must leave the currently served project untouched.
                            ProjectFiles.Save(projectPath,updated);project=updated;
                        }
                    });
                    status.Text="Планы сохранены. В APK нажмите «Загрузить проект из Windows», затем «Открыть план» в карточке устройства.";
                }
            });
        }

        private void SaveProjectCopy()
        {
            if (!RequireProject()) return;
            using (SaveFileDialog save = new SaveFileDialog())
            {
                save.Filter = "Проект APK (*.s3project)|*.s3project";
                save.FileName = Path.GetFileName(projectPath ?? "streletz-project.s3project");
                if (save.ShowDialog(this) != DialogResult.OK) return;
                RunBusy("Сохранение копии проекта…", delegate {
                    var copy = GetRegistrationProject();
                    ProjectFiles.Save(save.FileName, copy);
                    status.Text = "Копия сохранена: " + save.FileName
                        + ". Чтобы продолжить работу с ней, выберите «Открыть проект» и эту копию.";
                });
            }
        }

        private void SaveCsv()
        {
            if (!RequireProject()) return;
            using (SaveFileDialog save = new SaveFileDialog())
            {
                save.Filter = "CSV для Excel (*.csv)|*.csv";
                save.FileName = "streletz-apk-result.csv";
                if (save.ShowDialog(this) != DialogResult.OK) return;
                ProjectFiles.SaveCsv(save.FileName, rows);
                status.Text = "Отчёт сохранён: " + save.FileName;
            }
        }

        private void CompareWithBfx()
        {
            if (!RequireProject()) return;
            using (OpenFileDialog open = new OpenFileDialog())
            {
                open.Filter = "Проект Стрелец-Мастер (*.bfx)|*.bfx";
                open.Title = "Выберите актуальную копию BFX для сверки";
                if (open.ShowDialog(this) != DialogResult.OK) return;
                RunBusy("Сверка с BFX…", delegate
                {
                    Dictionary<string, object> bfx = BfxReader.Read(open.FileName, Path.GetFileNameWithoutExtension(open.FileName));
                    Dictionary<string, ViewRow> actual = ProjectFiles.MakeRows(bfx).ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
                    int equal = 0, pending = 0, conflict = 0, missing = 0;
                    foreach (ViewRow row in rows)
                    {
                        ViewRow current;
                        if (!actual.TryGetValue(row.Id, out current))
                        {
                            row.Comparison = "Нет в выбранном BFX";
                            missing++;
                        }
                        else if (row.StageNumber >= 1 && current.StageNumber < 2)
                        {
                            row.Comparison = "Требуется инициализация в Мастере";
                            pending++;
                        }
                        else if (!String.IsNullOrEmpty(row.Serial) && !String.IsNullOrEmpty(current.Serial) && !String.Equals(row.Serial, current.Serial, StringComparison.OrdinalIgnoreCase))
                        {
                            row.Comparison = "Аппаратный ID отличается";
                            conflict++;
                        }
                        else
                        {
                            row.Comparison = "Совпадает";
                            equal++;
                        }
                    }
                    ApplyFilter();
                    status.Text = String.Format("Сверка: совпадает {0}; требуется действие {1}; конфликт {2}; отсутствует {3}", equal, pending, conflict, missing);
                });
            }
        }

        private void ImportPanelsFromBfx()
        {
            if (!RequireProject()) return;
            using (var open = new OpenFileDialog { Filter = "Проект Стрелец-Мастер (*.bfx)|*.bfx", Title = "Исходный BFX с теми же записями устройств" })
            {
                if (open.ShowDialog(this) != DialogResult.OK) return;
                long revision;
                var baseline = registrations.ReadProjectSnapshot(out revision);
                if (baseline == null) throw new InvalidOperationException("В Windows-мосте не открыт проект.");
                var source = BfxReader.ReadPanelCatalog(open.FileName, baseline);
                var catalog = ProjectPanelCatalog.Cached(source);
                if (catalog.Length == 0) throw new InvalidDataException("Каталог BFX пуст после чтения: панелей 0; записей текущего проекта: "
                    + ProjectFiles.Records(baseline).Count() + ". Файл проекта не изменён.");
                registrations.RunProjectMutation(revision, delegate {
                    lock (projectGuard)
                    {
                        if (project == null || BridgeValues.ProjectFingerprint(project) != BridgeValues.ProjectFingerprint(source))
                            throw new InvalidDataException("Состав записей выбранного BFX отличается от открытого переносимого проекта. Создайте актуальный проект из BFX и откройте его на телефоне.");
                        var updated = BridgeValues.Copy(project);
                        ProjectPanelCatalog.Apply(updated, catalog);
                        updated["panelCatalogComplete"] = BridgeValues.Flag(source, "panelCatalogComplete");
                        updated["panelCatalogOrigin"] = "bfx";
                        updated["panelSource"] = BridgeValues.Copy(BridgeValues.Object(source, "panelSource"));
                        updated["panelCatalogIssues"] = BridgeValues.Array(source, "panelCatalogIssues") ?? new object[0];
                        updated["unresolvedPanelRecordIds"] = BridgeValues.Array(source, "unresolvedPanelRecordIds") ?? new object[0];
                        if (String.IsNullOrEmpty(projectPath)) throw new InvalidOperationException("Не указан путь открытого переносимого проекта.");
                        ProjectFiles.Save(projectPath, updated);
                        project = updated;
                    }
                });
                status.Text = "Из BFX прочитано панелей: " + catalog.Length + ". Принадлежность сохранена в переносимом проекте.";
            }
        }

        private void LoadIntoGrid(Dictionary<string, object> value, string path, bool enrich = false)
        {
            registrations.RunProjectMutation(delegate {
                lock (projectGuard) { project = value; projectPath = path; rows = ProjectFiles.MakeRows(value); }
            });
            ApplyFilter();
            summary.Text = ProjectFiles.SummaryText(value) + "   •   " + Path.GetFileName(path);
            int panels = ProjectPanelCatalog.Cached(value).Length;
            status.Text = panels > 0 ? "Проект открыт. Панелей из файла: " + panels + "."
                : "Проект открыт. Каталога панелей в файле нет: Панели… → Прочитать BFX…";
            if (!exchangeStartedOnce && receiver == null) ToggleReceiver();
        }

        private void ToggleReceiver()
        {
            if (receiver != null)
            {
                StopReceiver();
                return;
            }
            try
            {
                receiver = new LanReceiver(23019, pairingCode, ReceiveInitializationJob, registrations.Handle);
                receiver.Start();
                exchangeStartedOnce = true;
                receiverButton.Text = "Остановить обмен";
                receiverInfo.Text = "Телефон ↔ " + LanReceiver.LocalAddresses().First() + ":23019   •   код " + BridgeSettings.DisplayCode(pairingCode);
                status.Text = "Обмен включён. Телефон автоматически загрузит открытый проект после настройки Windows-моста.";
                StartHttp();
            }
            catch (Exception error)
            {
                receiver = null;
                MessageBox.Show(this, error.Message, "Не удалось включить приём", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ConfigureHttp()
        {
            using (HttpGatewayDialog dialog = new HttpGatewayDialog(httpSettings, LanReceiver.LocalAddresses().First(), StopHttp))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    dialog.Selected.Save(HttpSettingsPath);
                    StopHttp(); httpSettings = dialog.Selected;
                    if (httpSettings.Enabled && receiver == null) ToggleReceiver();
                    else StartHttp();
                }
                catch (Exception error) { MessageBox.Show(this, error.Message, "KeenDNS", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }

        private void StartHttp()
        {
            if (!httpSettings.Enabled || receiver == null || httpGateway != null) return;
            HttpGateway candidate = new HttpGateway(pairingCode, registrations.Handle);
            try
            {
                candidate.Stopped += delegate {
                    if (!IsDisposed && !Disposing && IsHandleCreated) {
                        try { BeginInvoke((Action)delegate {
                            if (ReferenceEquals(httpGateway, candidate)) { httpGateway = null; httpInfo.Text = "KeenDNS · HTTP-доступ остановлен с ошибкой. Откройте настройки."; }
                        }); } catch (InvalidOperationException) { }
                    }
                };
                candidate.Start(httpSettings.Port); httpGateway = candidate;
                httpInfo.Text = "KeenDNS · HTTP " + LanReceiver.LocalAddresses().First() + ":" + httpSettings.Port
                    + (String.IsNullOrEmpty(httpSettings.Address) ? " · домен задаётся в настройках" : " · " + httpSettings.Address);
            }
            catch (Exception error)
            {
                candidate.Dispose();
                httpInfo.Text = "KeenDNS · HTTP не запущен. Проверьте разрешение Windows и порт.";
                string message = error is HttpListenerException && ((HttpListenerException)error).ErrorCode == 5
                    ? "Откройте KeenDNS → «Разрешить в Windows», подтвердите запрос администратора и нажмите «Применить»."
                    : "Не удалось включить HTTP-доступ: " + error.Message;
                MessageBox.Show(this, message, "KeenDNS", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void StopHttp()
        {
            HttpGateway current = httpGateway; httpGateway = null;
            if (current != null) current.Dispose();
            if (!IsDisposed) httpInfo.Text = "KeenDNS · HTTP-доступ выключен";
        }

        private void StopReceiver()
        {
            StopHttp();
            LanReceiver current = receiver;
            receiver = null;
            if (current != null) current.Dispose();
            if (!IsDisposed)
            {
                receiverButton.Text = "Включить обмен";
                receiverInfo.Text = "Обмен с телефоном остановлен";
            }
        }

        private Dictionary<string, object> ReceiveInitializationJob(Dictionary<string, object> request)
        {
            if (InvokeRequired)
                return (Dictionary<string, object>)Invoke(new Func<Dictionary<string, object>>(
                    delegate { return ReceiveInitializationJob(request); }));
            try
            {
                if (project == null || String.IsNullOrEmpty(projectPath))
                    throw new InvalidOperationException("В Windows-мосте не открыт проект.");
                string message = "";
                registrations.RunProjectMutation(delegate {
                    lock (projectGuard)
                    {
                        Dictionary<string, object> updated = BridgeValues.Copy(project);
                        message = ProjectFiles.ApplyInitializationJob(updated, request);
                        ProjectFiles.Save(projectPath, updated); project = updated;
                        rows = ProjectFiles.MakeRows(project);
                    }
                });
                ApplyFilter();
                summary.Text = ProjectFiles.SummaryText(project) + "   •   " + Path.GetFileName(projectPath);
                status.Text = message + ". Проект сохранён.";
                return LanReceiver.Response(true, message);
            }
            catch (Exception error)
            {
                status.Text = "Задание APK отклонено: " + error.Message;
                return LanReceiver.Response(false, error.Message);
            }
        }

        private void ApplyFilter()
        {
            string query = (filter.Text ?? "").Trim().ToLowerInvariant();
            List<ViewRow> visible = String.IsNullOrEmpty(query) ? rows : rows.Where(delegate(ViewRow row)
            {
                return row.SearchText.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
            }).ToList();
            grid.DataSource = null;
            grid.DataSource = visible;
        }

        private bool RequireProject()
        {
            if (project != null) return true;
            MessageBox.Show(this, "Сначала откройте проект с телефона или создайте его из BFX.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        private void RunBusy(string message, Action action)
        {
            try
            {
                UseWaitCursor = true;
                status.Text = message;
                Application.DoEvents();
                action();
            }
            catch (Exception error)
            {
                Exception actual = error is TargetInvocationException && error.InnerException != null ? error.InnerException : error;
                MessageBox.Show(this, actual.Message, "Операция не выполнена", MessageBoxButtons.OK, MessageBoxIcon.Error);
                status.Text = "Ошибка: " + actual.Message;
            }
            finally { UseWaitCursor = false; }
        }
    }

    internal sealed class ViewRow
    {
        public string Id { get; set; }
        public string Address { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Group { get; set; }
        public string Zone { get; set; }
        public string Stage { get; set; }
        public int StageNumber { get; set; }
        public string Serial { get; set; }
        public string DeviceKey { get; set; }
        public string Note { get; set; }
        public string Comparison { get; set; }
        public string SearchText { get { return String.Join(" ", new[] { Address, Name, Description, Group, Zone, Stage, Serial, DeviceKey, Note, Comparison }); } }
    }

    internal static class ProjectFiles
    {
        private static JavaScriptSerializer Serializer()
        {
            return new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue, RecursionLimit = 4096 };
        }

        public static Dictionary<string, object> Load(string path)
        {
            Dictionary<string, object> root = Serializer().DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
            Validate(root);
            return root;
        }

        public static void Save(string path, Dictionary<string, object> root)
        {
            Validate(root);
            foreach (var record in Records(root)) record["description"] = ProjectPanelCatalog.EffectiveDescription(record);
            BridgeFiles.WriteAtomic(path, Serializer().Serialize(root));
        }

        public static void SaveNewCopy(string path, Dictionary<string, object> root)
        {
            Validate(root);
            foreach (var record in Records(root)) record["description"] = ProjectPanelCatalog.EffectiveDescription(record);
            BridgeFiles.WriteNewAtomic(path, Serializer().Serialize(root));
        }

        public static void Validate(Dictionary<string, object> root)
        {
            if (root == null || Number(root, "schemaVersion") != 1 || !root.ContainsKey("records"))
                throw new InvalidDataException("Неподдерживаемый файл проекта. Требуется версия 1.");
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> row in Records(root))
            {
                string id = Text(row, "id");
                if (String.IsNullOrWhiteSpace(id) || !ids.Add(id))
                    throw new InvalidDataException("В проекте отсутствует или повторяется ID устройства.");
            }
        }

        public static List<ViewRow> MakeRows(Dictionary<string, object> root)
        {
            List<ViewRow> result = new List<ViewRow>();
            foreach (Dictionary<string, object> row in Records(root))
            {
                if (Text(row, "recordKind") != "device") continue;
                Dictionary<string, object> commissioning = Dict(row, "commissioning");
                Dictionary<string, object> bfx = Dict(row, "bfx");
                int stage = commissioning == null ? (Text(row, "initialized") == "+" ? 2 : 0) : Number(commissioning, "stage");
                string[] stages = { "Не начато", "Найдено", "Инициализировано", "Проверено" };
                stage = Math.Max(0, Math.Min(3, stage));
                result.Add(new ViewRow
                {
                    Id = Text(row, "id"), Address = bfx == null ? "" : Text(bfx, "address"),
                    Name = Text(row, "name"), Description = ProjectPanelCatalog.EffectiveDescription(row), Group = Text(row, "group"), Zone = Text(row, "zone"),
                    StageNumber = stage, Stage = stages[stage], Serial = commissioning == null ? "" : Text(commissioning, "serial"),
                    DeviceKey = commissioning == null ? "" : Text(commissioning, "deviceKey"),
                    Note = commissioning == null ? "" : Text(commissioning, "note"), Comparison = "Не сверялось"
                });
            }
            return result;
        }

        public static string SummaryText(Dictionary<string, object> root)
        {
            List<ViewRow> rows = MakeRows(root);
            int initialized = rows.Count(x => x.StageNumber >= 2);
            int checkedCount = rows.Count(x => x.StageNumber >= 3);
            int serials = rows.Count(x => !String.IsNullOrWhiteSpace(x.Serial));
            int keys = rows.Count(x => !String.IsNullOrWhiteSpace(x.DeviceKey));
            return String.Format("{0}: устройств {1}; инициализировано {2}; проверено {3}; аппаратных ID {4}; ключей {5}", Text(root, "siteName"), rows.Count, initialized, checkedCount, serials, keys);
        }

        public static string ApplyInitializationJob(Dictionary<string, object> root, Dictionary<string, object> job)
        {
            string recordId = Text(job, "recordId").Trim();
            string serial = Text(job, "serial").Trim().ToUpperInvariant();
            string deviceKey = Text(job, "deviceKey").Trim().ToUpperInvariant();
            string note = Text(job, "note").Trim();
            if (String.IsNullOrEmpty(recordId)) throw new InvalidDataException("В задании отсутствует ID записи проекта.");
            if (!Regex.IsMatch(serial, "^[0-9A-Z._/-]{4,40}$")) throw new InvalidDataException("Не указан корректный аппаратный ID устройства.");
            if (!String.IsNullOrEmpty(deviceKey) && !Regex.IsMatch(deviceKey, "^[0-9A-F]{8,32}$"))
                throw new InvalidDataException("Некорректный KEY устройства.");

            Dictionary<string, object> target = null;
            foreach (Dictionary<string, object> row in Records(root))
            {
                if (String.Equals(Text(row, "id"), recordId, StringComparison.OrdinalIgnoreCase)) target = row;
                if (!String.Equals(Text(row, "id"), recordId, StringComparison.OrdinalIgnoreCase))
                {
                    Dictionary<string, object> other = Dict(row, "commissioning");
                    if (other != null && String.Equals(Text(other, "serial"), serial, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Этот аппаратный ID уже назначен другому устройству проекта.");
                }
            }
            if (target == null) throw new InvalidDataException("Устройство не найдено в открытом проекте Windows-моста.");
            Dictionary<string, object> commissioning = Dict(target, "commissioning");
            if (commissioning == null)
            {
                commissioning = new Dictionary<string, object>();
                target["commissioning"] = commissioning;
            }
            int currentStage = Number(commissioning, "stage");
            if (currentStage >= 2) throw new InvalidOperationException("Устройство уже отмечено как инициализированное.");
            commissioning["stage"] = 1;
            commissioning["stageName"] = "Найдено";
            commissioning["serial"] = serial;
            commissioning["deviceKey"] = deviceKey;
            commissioning["note"] = note;
            commissioning["updatedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            target["initialized"] = "-";
            return "Получено задание: " + Text(target, "name");
        }

        public static void SaveCsv(string path, IEnumerable<ViewRow> rows)
        {
            StringBuilder value = new StringBuilder();
            value.AppendLine("Адрес;Устройство;Описание;Группа;Зона;Этап APK;Аппаратный ID;Ключ устройства;Примечание;Сверка с BFX;GUID");
            foreach (ViewRow row in rows)
            {
                value.AppendLine(String.Join(";", new[] { row.Address, row.Name, row.Description, row.Group, row.Zone, row.Stage, row.Serial, row.DeviceKey, row.Note, row.Comparison, row.Id }.Select(Csv)));
            }
            File.WriteAllText(path, value.ToString(), new UTF8Encoding(true));
        }

        private static string Csv(string value) { return "\"" + (value ?? "").Replace("\"", "\"\"") + "\""; }

        public static IEnumerable<Dictionary<string, object>> Records(Dictionary<string, object> root)
        {
            object value = root["records"];
            object[] array = value as object[];
            if (array == null) throw new InvalidDataException("В проекте отсутствует список устройств.");
            foreach (object item in array)
            {
                Dictionary<string, object> row = item as Dictionary<string, object>;
                if (row == null) throw new InvalidDataException("Повреждена запись устройства.");
                yield return row;
            }
        }

        public static Dictionary<string, object> Dict(Dictionary<string, object> value, string key)
        {
            object result;
            return value != null && value.TryGetValue(key, out result) ? result as Dictionary<string, object> : null;
        }

        public static string Text(Dictionary<string, object> value, string key)
        {
            object result;
            return value != null && value.TryGetValue(key, out result) && result != null ? Convert.ToString(result, CultureInfo.InvariantCulture) : "";
        }

        public static int Number(Dictionary<string, object> value, string key)
        {
            int result;
            return Int32.TryParse(Text(value, key), NumberStyles.Any, CultureInfo.InvariantCulture, out result) ? result : 0;
        }
    }

    internal static class BridgeSettings
    {
        public static string StateDirectory()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StreletzBridge");
        }

        public static string LoadPairingCode()
        {
            string directory = StateDirectory();
            string path = Path.Combine(directory, "pairing-code.txt");
            try
            {
                if (File.Exists(path))
                {
                    string current = File.ReadAllText(path).Trim().ToUpperInvariant();
                    if (Regex.IsMatch(current, "^[0-9A-F]{16}$") && current != "0000000000000000") return current;
                }
                Directory.CreateDirectory(directory);
                byte[] random = new byte[8];
                using (RandomNumberGenerator generator = RandomNumberGenerator.Create()) generator.GetBytes(random);
                string created = BitConverter.ToString(random).Replace("-", "");
                BridgeFiles.WriteAtomic(path, created);
                return created;
            }
            catch (Exception error)
            {
                throw new IOException("Не удалось создать или прочитать код подключения Windows-моста. Приём команд недоступен; проверьте доступ к профилю пользователя.", error);
            }
        }

        public static string DisplayCode(string code)
        {
            string value = (code ?? "").Replace("-", "").ToUpperInvariant();
            return value.Length == 16
                ? String.Join("-", Enumerable.Range(0, 4).Select(i => value.Substring(i * 4, 4)))
                : value;
        }
    }

    internal sealed class LanReceiver : IDisposable
    {
        // Allow pauses while a phone request or response crosses a slow VPN.
        // This bounds socket I/O only; registration and request-age limits are separate.
        private const int ConnectionIoTimeoutMilliseconds = 120000;
        private readonly int port;
        private readonly string pairingCode;
        private readonly Func<Dictionary<string, object>, Dictionary<string, object>> handler;
        private readonly Func<Dictionary<string, object>, Dictionary<string, object>> v3Handler;
        private readonly SemaphoreSlim connections = new SemaphoreSlim(8, 8);
        private TcpListener listener;
        private Thread thread;
        private volatile bool stopping;

        public LanReceiver(int port, string pairingCode, Func<Dictionary<string, object>, Dictionary<string, object>> handler,
            Func<Dictionary<string, object>, Dictionary<string, object>> v3Handler = null)
        {
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException("port");
            this.port = port;
            this.pairingCode = ProtocolV3.NormalizePairingCode(pairingCode);
            this.handler = handler;
            this.v3Handler = v3Handler;
        }

        public void Start()
        {
            listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            thread = new Thread(AcceptLoop) { IsBackground = true, Name = "Streletz APK receiver" };
            thread.Start();
        }

        public string ReceiveOnce()
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            try
            {
                using (TcpClient client = listener.AcceptTcpClient()) return ProcessClient(client);
            }
            finally { listener.Stop(); listener = null; }
        }

        private void AcceptLoop()
        {
            while (!stopping)
            {
                try
                {
                    TcpClient client = listener.AcceptTcpClient();
                    if (!connections.Wait(0)) { client.Close(); continue; }
                    ThreadPool.QueueUserWorkItem(delegate { try { using (client) ProcessClient(client); } finally { connections.Release(); } });
                }
                catch (SocketException) { if (!stopping) Thread.Sleep(200); }
                catch (ObjectDisposedException) { break; }
            }
        }

        private string ProcessClient(TcpClient client)
        {
            client.ReceiveTimeout = ConnectionIoTimeoutMilliseconds;
            client.SendTimeout = ConnectionIoTimeoutMilliseconds;
            string responseText = "";
            bool v3Mode = false;
            try
            {
                using (NetworkStream stream = client.GetStream())
                using (StreamReader input = new StreamReader(stream, new UTF8Encoding(false), false, 4096, true))
                using (StreamWriter output = new StreamWriter(stream, new UTF8Encoding(false), 4096, true) { AutoFlush = true })
                {
                    JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = 65536 };
                    Dictionary<string, object> response;
                    try
                    {
                        string line = ReadLineLimited(input, 65536);
                        Dictionary<string, object> envelope = serializer.DeserializeObject(line) as Dictionary<string, object>;
                        if (ProjectFiles.Number(envelope, "protocol") == 3)
                        {
                            v3Mode = true;
                            string requestId = "";
                            Dictionary<string, object> v3Response;
                            try
                            {
                                Dictionary<string, object> v3Request = ProtocolV3.Decode(envelope, pairingCode, "request");
                                requestId = ProjectFiles.Text(v3Request, "requestId");
                                v3Response = v3Handler == null
                                    ? ProtocolV3.Response(requestId, false, "Этот режим моста не поддерживает регистрацию.", "driver_unavailable")
                                    : v3Handler(v3Request);
                            }
                            catch (Exception error)
                            {
                                v3Response = ProtocolV3.Response(requestId, false,
                                    error is UnauthorizedAccessException ? "Неверный код подключения или повреждённое сообщение." : "Не удалось обработать защищённый запрос.",
                                    error is UnauthorizedAccessException ? "unauthorized" : "invalid_request");
                            }
                            response = ProtocolV3.Encode(v3Response, pairingCode, "response");
                        }
                        else
                        {
                        if (envelope == null || ProjectFiles.Number(envelope, "protocol") != 2
                                || ProjectFiles.Text(envelope, "type") != "initialize-encrypted")
                            throw new InvalidDataException("Неподдерживаемый запрос APK.");
                        Dictionary<string, object> request = DecryptRequest(envelope, serializer);
                        response = handler(request);
                        }
                    }
                    catch (Exception error)
                    {
                        response = v3Mode
                            ? ProtocolV3.Encode(ProtocolV3.Response("", false, "Не удалось обработать защищённый запрос.", "invalid_request"), pairingCode, "response")
                            : Response(false, error.Message);
                    }
                    responseText = v3Mode ? BridgeJson.Serialize(response) : serializer.Serialize(response);
                    if (v3Mode && responseText.Length > ProtocolV3.MaximumResponseLineLength)
                        throw new InvalidDataException("Ответ моста превышает допустимый размер.");
                    output.WriteLine(responseText);
                }
            }
            catch (Exception error)
            {
                responseText = new JavaScriptSerializer().Serialize(Response(false, error.Message));
            }
            return responseText;
        }

        private static string ReadLineLimited(StreamReader input, int maximum)
        {
            StringBuilder value = new StringBuilder();
            while (value.Length <= maximum)
            {
                int current = input.Read();
                if (current < 0 || current == '\n') break;
                if (current != '\r') value.Append((char)current);
            }
            if (value.Length == 0) throw new InvalidDataException("APK не передала данные.");
            if (value.Length > maximum) throw new InvalidDataException("Задание APK слишком большое.");
            return value.ToString();
        }

        private Dictionary<string, object> DecryptRequest(Dictionary<string, object> envelope, JavaScriptSerializer serializer)
        {
            byte[] iv, ciphertext, suppliedMac;
            try
            {
                iv = Convert.FromBase64String(ProjectFiles.Text(envelope, "iv"));
                ciphertext = Convert.FromBase64String(ProjectFiles.Text(envelope, "data"));
                suppliedMac = Convert.FromBase64String(ProjectFiles.Text(envelope, "mac"));
            }
            catch (FormatException) { throw new InvalidDataException("Повреждены данные задания APK."); }
            if (iv.Length != 16 || ciphertext.Length == 0 || suppliedMac.Length != 32)
                throw new InvalidDataException("Повреждены данные задания APK.");

            byte[] digest;
            using (SHA512 sha = SHA512.Create()) digest = sha.ComputeHash(Encoding.UTF8.GetBytes(pairingCode));
            byte[] encryptionKey = new byte[32], authenticationKey = new byte[32];
            Buffer.BlockCopy(digest, 0, encryptionKey, 0, 32);
            Buffer.BlockCopy(digest, 32, authenticationKey, 0, 32);
            byte[] authenticated = new byte[iv.Length + ciphertext.Length];
            Buffer.BlockCopy(iv, 0, authenticated, 0, iv.Length);
            Buffer.BlockCopy(ciphertext, 0, authenticated, iv.Length, ciphertext.Length);
            byte[] expectedMac;
            using (HMACSHA256 hmac = new HMACSHA256(authenticationKey)) expectedMac = hmac.ComputeHash(authenticated);
            if (!FixedEquals(expectedMac, suppliedMac))
                throw new UnauthorizedAccessException("Неверный код подключения Windows-моста или повреждённое задание.");

            byte[] plaintext;
            using (Aes aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = encryptionKey;
                aes.IV = iv;
                using (ICryptoTransform decryptor = aes.CreateDecryptor())
                    plaintext = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
            }
            Dictionary<string, object> request = serializer.DeserializeObject(Encoding.UTF8.GetString(plaintext)) as Dictionary<string, object>;
            if (request == null || ProjectFiles.Number(request, "protocol") != 1 || ProjectFiles.Text(request, "type") != "initialize")
                throw new InvalidDataException("Внутри задания находится неподдерживаемый запрос.");
            return request;
        }

        private static bool FixedEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            int difference = 0;
            for (int i = 0; i < left.Length; i++) difference |= left[i] ^ right[i];
            return difference == 0;
        }

        public static Dictionary<string, object> Response(bool ok, string message)
        {
            return new Dictionary<string, object> { { "ok", ok }, { "message", message ?? "" } };
        }

        public static string[] LocalAddresses()
        {
            string[] values = NetworkInterface.GetAllNetworkInterfaces()
                .Where(x => x.OperationalStatus == OperationalStatus.Up)
                .OrderBy(x => x.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 0
                        : x.Description.IndexOf("VMware", StringComparison.OrdinalIgnoreCase) >= 0 ? 3
                        : x.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 1 : 2)
                .SelectMany(x => x.GetIPProperties().UnicastAddresses)
                .Select(x => x.Address)
                .Where(x => x.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(x))
                .Select(x => x.ToString()).Distinct().ToArray();
            return values.Length == 0 ? new[] { "127.0.0.1" } : values;
        }

        public void Dispose()
        {
            stopping = true;
            TcpListener current = listener;
            listener = null;
            if (current != null) current.Stop();
        }
    }

    internal static class BfxReader
    {
        private static readonly Dictionary<string, string[]> Types = new Dictionary<string, string[]>
        {
            { "128:0", new[] { "РР-ПРО", "Расширители" } },
            { "128:17", new[] { "Аврора-ДТР-ПРО", "Пожарные извещатели" } },
            { "128:32", new[] { "ИПР-ПРО", "Пожарные извещатели" } },
            { "128:77", new[] { "Табло-ПРО", "Устройства оповещения" } },
            { "128:94", new[] { "Клапан-ПРО 220", "Исполнительные устройства" } },
            { "128:98", new[] { "ИБ1-ПРО", "Исполнительные устройства" } }
        };

        public static Dictionary<string, object> Read(string path, string siteName)
        {
            path = Path.GetFullPath(path);
            return Convert(LoadDocument(path), path, siteName);
        }

        public static Dictionary<string, object> ReadPanelCatalog(string path, Dictionary<string, object> currentProject)
        {
            path = Path.GetFullPath(path);
            ProjectFiles.Validate(currentProject);
            var result = BridgeValues.Copy(currentProject);
            BfxPanelCatalog.Read(LoadDocument(path), result);
            result["panelSource"] = new Dictionary<string, object> {
                { "format", "Streletz-Master BFX" }, { "fileName", Path.GetFileName(path) }, { "sha256", Sha256(path) }
            };
            return result;
        }

        private static XmlDocument LoadDocument(string path)
        {
            path = Path.GetFullPath(path);
            string master = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Streletz-Integral", "Streletz-Master");
            string core = Path.Combine(master, "Core.dll");
            if (!File.Exists(core))
            {
                string x86 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Streletz-Integral", "Streletz-Master");
                if (File.Exists(Path.Combine(x86, "Core.dll"))) { master = x86; core = Path.Combine(master, "Core.dll"); }
            }
            if (!File.Exists(core)) throw new FileNotFoundException("Не найден установленный Стрелец-Мастер: " + core);
            ResolveEventHandler resolver = delegate(object sender, ResolveEventArgs args)
            {
                string candidate = Path.Combine(master, new AssemblyName(args.Name).Name + ".dll");
                return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
            };
            AppDomain.CurrentDomain.AssemblyResolve += resolver;
            XmlDocument document;
            string previous = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = master;
                Assembly assembly = Assembly.LoadFrom(core);
                Type loader = assembly.GetType("Core.Bf.BfLoader", true);
                MethodInfo method = loader.GetMethod("Load", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                document = (XmlDocument)method.Invoke(null, new object[] { Path.GetFullPath(path) });
            }
            finally
            {
                Environment.CurrentDirectory = previous;
                AppDomain.CurrentDomain.AssemblyResolve -= resolver;
            }
            return document;
        }

        private static Dictionary<string, object> Convert(XmlDocument document, string path, string siteName)
        {
            Dictionary<string, XmlNode> resources = new Dictionary<string, XmlNode>(StringComparer.OrdinalIgnoreCase);
            foreach (XmlNode resource in document.SelectNodes("/Root/Resources/Resource"))
                resources[Attr(resource, "id")] = BfxPanelCatalog.ReadResourcePayload(resource);

            List<object> records = new List<object>();
            Dictionary<string, int> groups = new Dictionary<string, int>();
            int initialized = 0, rowNumber = 1;
            foreach (XmlNode item in document.SelectNodes("/Root/Items//Item"))
            {
                string key = Attr(item, "type") + ":" + Attr(item, "subType");
                string[] deviceType;
                if (!Types.TryGetValue(key, out deviceType)) continue;
                string id = Attr(item, "id");
                string address = Attr(item, "addres");
                XmlNode info;
                resources.TryGetValue(id, out info);
                bool ready = Attr(item, "state") == "WasProgrammed" || !String.IsNullOrEmpty(Attr(item, "infoID"));
                if (ready) initialized++;
                groups[deviceType[1]] = groups.ContainsKey(deviceType[1]) ? groups[deviceType[1]] + 1 : 1;
                int stage = ready ? 2 : 0;
                string firmware = NodeText(info, "FirmwareVersion");
                Dictionary<string, object> record = new Dictionary<string, object>
                {
                    { "id", id }, { "name", address + " " + deviceType[0] }, { "description", First(Attr(item, "CustomName"), NodeText(info, "Name")) },
                    { "programmed", ready ? "+" : "-" }, { "initialized", ready ? "+" : "-" }, { "version", firmware == "0" ? "" : "в. " + firmware },
                    { "zone", NodeText(info, "PartitionNumber") }, { "group", deviceType[1] }, { "type", deviceType[0] }, { "hiddenId", id },
                    { "sourceRow", rowNumber++ }, { "recordKind", "device" },
                    { "bfx", new Dictionary<string, object> { { "address", address }, { "state", Attr(item, "state") }, { "infoId", Attr(item, "infoID") }, { "tableId", First(NodeText(info, "TableID"), "-1") }, { "classType", NodeText(info, "ClassType") } } },
                    { "commissioning", new Dictionary<string, object> { { "stage", stage }, { "stageName", ready ? "Инициализировано" : "Не начато" }, { "serial", DecodeId(NodeText(info, "ID")) }, { "deviceKey", "" }, { "note", "" }, { "updatedAt", 0L } } }
                };
                records.Add(record);
            }
            if (records.Count == 0) throw new InvalidDataException("В BFX не найдены поддерживаемые радиоустройства ПРО.");
            var result = new Dictionary<string, object>
            {
                { "format", "streletz-portable-project" }, { "schemaVersion", 1 }, { "transferVersion", 1 }, { "siteName", siteName },
                { "exportedAt", (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds },
                { "source", new Dictionary<string, object> { { "format", "Streletz-Master BFX" }, { "fileName", Path.GetFileName(path) }, { "sha256", Sha256(path) } } },
                { "summary", new Dictionary<string, object> { { "total", records.Count }, { "devices", records.Count }, { "initialized", initialized }, { "groups", groups } } },
                { "records", records.ToArray() }
            };
            BfxPanelCatalog.Read(document, result);
            return result;
        }

        private static string Attr(XmlNode node, string name) { return node == null || node.Attributes[name] == null ? "" : node.Attributes[name].Value; }
        private static string NodeText(XmlNode node, string name) { XmlNode child = node == null ? null : node.SelectSingleNode(name); return child == null ? "" : (child.InnerText ?? "").Trim(); }
        private static string First(string value, string fallback) { return String.IsNullOrEmpty(value) ? fallback : value; }
        private static string DecodeId(string value)
        {
            if (String.IsNullOrEmpty(value)) return "";
            try { return BitConverter.ToString(System.Convert.FromBase64String(value)).Replace("-", ""); }
            catch { return value; }
        }
        private static string Sha256(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            using (SHA256 hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
    }
}
