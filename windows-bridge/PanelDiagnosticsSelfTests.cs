// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
using System;
using System.Collections.Generic;
using System.IO;

namespace StreletzBridge
{
    internal static class PanelDiagnosticsSelfTests
    {
        private const string Secret = "SYNTHETIC_SECRET_MUST_NEVER_APPEAR";

        internal static void Run()
        {
            CheckFailure(new PanelRegistrationDriver((request, timeout) => { throw new TimeoutException(Secret); }, 0),
                "native_transport_timeout", "timeout classification");
            CheckFailure(new PanelRegistrationDriver((request, timeout) => { throw new UnauthorizedAccessException(Secret); }, 0),
                "native_access_denied", "access denial classification");
            CheckFailure(new PanelRegistrationDriver((request, timeout) => { throw new EndOfStreamException(Secret); }, 0),
                "native_response_closed", "closed native reply classification");
            CheckFailure(new PanelRegistrationDriver((request, timeout) => new Dictionary<string, object> {
                { "protocol", Secret }, { "password", Secret }
            }, 0), "native_protocol_mismatch", "protocol mismatch classification");

            foreach (string code in new[] { "master_login_required", "master_project_required", "unsupported_master_build" })
            {
                var driver = new PanelRegistrationDriver((request, timeout) => Reply(new Dictionary<string, object> {
                    { "version", "12.4" }, { "ready", false }, { "unavailableReason", code }, { "processId", 123 },
                    { "password", Secret }, { "pin", Secret }, { "deviceKey", Secret },
                    { "diagnostics", new Dictionary<string, object> { { "buildFailureComponent", "Core.dll" },
                        { "maximumResponseJsonCharacters", 4194303 }, { "rawConfig", Secret } } }
                }), 0);
                CheckFailure(driver, code, "native readiness classification");
                Check(driver.LastDiagnosticReport.Contains("hello получен: да"), "An answered hello must be distinguished from a missing pipe");
                Check(driver.LastDiagnosticReport.Contains("Core.dll"), "Guard component must remain diagnosable");
            }

            foreach (string component in new[] { "Plugin.RROP-LON.dll", "BaseAdapter.dll", "UsbEthernetAdapter.dll",
                "HardwareAdaptersFactory.dll", "TunnelAdapter.dll", "BaseAdapterTransport.dll" })
            {
                var driver = new PanelRegistrationDriver((request, timeout) => Reply(new Dictionary<string, object> {
                    { "version", "12.4" }, { "ready", false }, { "unavailableReason", "unsupported_master_build" },
                    { "diagnostics", new Dictionary<string, object> { { "buildFailureComponent", component }, { "rawError", Secret } } }
                }), 0);
                CheckFailure(driver, "unsupported_master_build", "route transport build guard classification");
                Check(driver.LastDiagnosticReport.Contains("Компонент проверки сборки: " + component),
                    "Newly inspected route/transport component was hidden from the build mismatch diagnostic: " + component);
            }

            var untrustedCode = new PanelRegistrationDriver((request, timeout) => new Dictionary<string, object> {
                { "protocol", "streletz-master-plugin-v1" }, { "ok", false },
                { "error", new Dictionary<string, object> { { "code", Secret }, { "message", Secret } } }
            }, 0);
            CheckFailure(untrustedCode, "unrecognized_native_error", "arbitrary native text must not enter the report");

            CompletionTelemetryIsBoundedAndReadOnly();

            int calls = 0;
            var ready = new PanelRegistrationDriver((request, timeout) => {
                calls++;
                string type = BridgeValues.Text(request, "type");
                Check(type == "hello" || type == "list_panels", "Diagnostics sent a mutating native request");
                return Reply(type == "hello" ? new Dictionary<string, object> { { "version", "12.4" }, { "ready", true }, { "processId", 456 } }
                    : new Dictionary<string, object> { { "panels", new object[0] }, { "privateProjectData", Secret } });
            }, 0);
            ready.ReadProjectPanels();
            Check(calls == 2 && ready.LastDiagnosticReport.Contains("Результат: успешно") && ready.LastDiagnosticReport.Contains("Этап: list_panels"),
                "Successful connection report must describe the completed read");
            Check(!ready.LastDiagnosticReport.Contains(Secret), "Successful response payload leaked into diagnostics");
            string directory = Path.Combine(Path.GetTempPath(), "streletz-diagnostic-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(directory);
                string file = Path.Combine(directory, "connection.txt");
                ready.SaveConnectionDiagnostics(file);
                Check(File.ReadAllText(file) == ready.LastDiagnosticReport, "Saved report differs from the reviewed snapshot");
                Check(calls == 2, "Saving diagnostics must not perform another native request");
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }

        private static Dictionary<string, object> Reply(Dictionary<string, object> data)
        { return new Dictionary<string, object> { { "protocol", "streletz-master-plugin-v1" }, { "ok", true }, { "data", data } }; }

        private static void CompletionTelemetryIsBoundedAndReadOnly()
        {
            string[] states = { "not_started", "waiting", "acknowledged", "completed", "blocked" };
            string[] checks = { "awaiting_success_window", "success_window_not_found", "success_window_ambiguous",
                "success_window_unverified", "success_target_changed", "success_acknowledgement_requested",
                "success_acknowledgement_failed", "success_acknowledgement_timeout", "native_completion_finished",
                "native_completion_unavailable", "awaiting_native_completion", "native_completion_timeout" };
            for (int i = 0; i < checks.Length; i++)
            {
                string state = states[i % states.Length], check = checks[i], lastCheck = checks[(i + 1) % checks.Length];
                bool supported = i % 2 == 0;
                int calls = 0;
                var driver = new PanelRegistrationDriver((request, timeout) => {
                    calls++;
                    string type = BridgeValues.Text(request, "type");
                    Check(type == "hello" || type == "list_panels", "Reading result completion telemetry sent a mutating native request");
                    return Reply(type == "hello" ? new Dictionary<string, object> {
                        { "version", "12.4" }, { "ready", true }, { "processId", 456 },
                        { "capabilities", new Dictionary<string, object> { { "autoConfirmSuccessResult", supported },
                            { "suppressNativeSuccessDialog", !supported }, { "raw", Secret } } },
                        { "diagnostics", new Dictionary<string, object> { { "nativeCompletionState", state },
                            { "nativeCompletionCheck", check }, { "nativeCompletionLastCheck", lastCheck },
                            { "nativeSuccessDialogSuppressed", supported },
                            { "windowText", Secret }, { "windowHandle", Secret }, { "deviceKey", Secret } } }
                    } : new Dictionary<string, object> { { "panels", new object[0] } });
                }, 0);
                driver.ReadProjectPanels();
                string report = driver.LastDiagnosticReport;
                Check(calls == 2 && report.Contains("Состояние завершения окна результата: " + state) &&
                    report.Contains("Проверка завершения окна результата: " + check) &&
                    report.Contains("Последняя проверка до завершения ожидания: " + lastCheck) &&
                    report.Contains("Автоматическое подтверждение успешного результата: " + (supported ? "да" : "нет")) &&
                    report.Contains("Подавление окна успешного результата для регистрации с телефона: " + (!supported ? "да" : "нет")) &&
                    report.Contains("Окно успешного результата подавлено для текущей операции: " + (supported ? "да" : "нет")),
                    "Reviewed completion capability/state/check was lost or created additional native work");
                Check(!report.Contains(Secret), "Raw success-window data entered connection diagnostics");
            }

            foreach (object malformed in new object[] { Secret, "waiting\r\n" + Secret, 1,
                new Dictionary<string, object> { { "windowText", Secret } }, new object[] { "completed", Secret }, null })
            {
                int calls = 0;
                var driver = new PanelRegistrationDriver((request, timeout) => {
                    calls++;
                    string type = BridgeValues.Text(request, "type");
                    Check(type == "hello" || type == "list_panels", "Malformed diagnostic caused a mutating native request");
                    return Reply(type == "hello" ? new Dictionary<string, object> {
                        { "version", "12.4" }, { "ready", true },
                        { "capabilities", new Dictionary<string, object> { { "autoConfirmSuccessResult", malformed },
                            { "suppressNativeSuccessDialog", malformed } } },
                        { "diagnostics", new Dictionary<string, object> { { "nativeCompletionState", malformed },
                            { "nativeCompletionCheck", malformed }, { "nativeCompletionLastCheck", malformed },
                            { "nativeSuccessDialogSuppressed", malformed }, { "windowHandle", Secret }, { "windowText", Secret } } }
                    } : new Dictionary<string, object> { { "panels", new object[0] } });
                }, 0);
                driver.ReadProjectPanels();
                string report = driver.LastDiagnosticReport;
                Check(calls == 2 && !report.Contains(Secret) && !report.Contains("Состояние завершения окна результата:") &&
                    !report.Contains("Проверка завершения окна результата:") &&
                    !report.Contains("Последняя проверка до завершения ожидания:") &&
                    !report.Contains("Подавление окна успешного результата для регистрации с телефона:") &&
                    !report.Contains("Окно успешного результата подавлено для текущей операции:") &&
                    !report.Contains("Автоматическое подтверждение успешного результата:"),
                    "Malformed completion telemetry was trusted, leaked or generated additional native work");
                Check(RegistrationDiagnosticData.Details(new Dictionary<string, object> {
                    { "nativeCompletionState", malformed }, { "nativeCompletionCheck", malformed },
                    { "nativeCompletionLastCheck", malformed }, { "nativeSuccessDialogSuppressed", malformed },
                    { "suppressNativeSuccessDialog", malformed },
                    { "autoConfirmSuccessResult", malformed }, { "windowText", Secret }, { "windowHandle", Secret }
                }).Count == 0, "Malformed native completion metadata could enter the persistent journal");
            }
        }

        private static void CheckFailure(PanelRegistrationDriver driver, string code, string name)
        {
            bool failed = false;
            try { driver.ReadProjectPanels(); }
            catch (IOException error) { failed = true; Check(!error.Message.Contains(Secret), name + ": secret in user message"); }
            Check(failed, name + ": failure was not reported");
            Check(driver.LastDiagnosticReport.Contains("Код: " + code), name + ": diagnostic code missing");
            Check(!driver.LastDiagnosticReport.Contains(Secret), name + ": secret in diagnostic report");
        }

        private static void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException(message); }
    }
}
