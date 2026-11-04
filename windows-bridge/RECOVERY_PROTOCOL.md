# Windows bridge 0.5.10 — read-only registration recovery

This command is carried inside the existing authenticated protocol-v3 request and response envelopes. It never dispatches native `start`, `cancel`, `Register`, device packets or native dialog automation. It does not infer that a device is unregistered from a missing history entry or an uninitialized project row.

## Request

In addition to `protocol: 3`, fresh `sentAt`, and a new UUID `requestId`, send:

```json
{
  "type": "recover_registration",
  "operationId": "original-operation-uuid",
  "projectFingerprint": "original-64-hex-portable-project-fingerprint",
  "recordId": "original-record-uuid",
  "expectedHardwareId": "original-12-hex-device-id",
  "panelId": "original-panel-profile-uuid"
}
```

The examples above use descriptive placeholders; UUIDs and hexadecimal fields must be valid. Do not send a new operation ID, credentials, device KEY or a replacement record identity. Use a response read timeout of at least 45 seconds: the bounded native sequence can take up to 35 seconds.

## Successful response

Every recovery success has the normal response `requestId`, `ok: true`, a top-level `operationId`, and a `recovery` object echoing all five original identity values above. It also contains `outcome`, `message` and boolean `manualReviewAllowed`.

| Outcome | `manualReviewAllowed` | `operation` property | Meaning |
|---|---:|---|---|
| `restored` | false | Present | A matching authoritative terminal result exists. Its identity and registration/stop evidence have been checked. Any recovered native result was committed to the Windows journal before this response. |
| `native_active` | false | Absent | A local worker or the original native operation/window is still active. The check does not stop it. If Master shows success, acknowledge `ОК`; to stop an active search, use that native window's `Отмена`, then check again. |
| `manual_review_required` | true | Absent | A fresh read-only probe verified the current native project, record and panel route, but the original result is absent/expired or the original operation stopped with an unknown result. An operator must verify the actual device and stopped search on the computer. This is not proof of cancellation or of an unregistered device. |

Nondefinitive recovery responses deliberately do not include a stale normal operation object; an old `unknown` cannot hide the new recovery outcome. Errors have `ok: false`, an `errorCode`, and a canonical top-level `operationId` when provided correctly. Recovery errors have neither a `recovery` object nor a normal `operation` object, and never permit manual completion.

Status and cancellation errors for a missing local journal entry also echo the original canonical `operationId`. They do not return `accepted: false`, infer cancellation or authorize manual review.

## Matching and persistence

The coordinator checks the original five-part identity against the current portable project and any existing local operation. Recovery does not reject a record merely because it is now initialized. A conflicting registered hardware ID, conflicting panel membership, or different project does reject recovery.

For absent/unknown local results, the production driver sends `hello`, `list_panels`, `status` for the **same original operation ID**, and a final `hello`. It checks the native process and current project stability; unique current record membership; exact native panel, host, port, full MK-IP ID and direct/terminal route. A retained result must match the original request identity and current verified route. The retained original native fingerprint must be valid but may differ from the current native fingerprint after successful subtype mutation.

Registered recovery uses the same proof helper as normal registration: expected full hardware ID, registration confirmation, confirmation sent, native success observed, and `native-fingerprint-record-and-panel` validation. Recovery waits for the native run and dialog to finish before restoring a new terminal result. Acknowledged cancellation/failure requires stopped run, closed dialog and no confirmation/success flags. Unknown/malformed/mismatched evidence cannot become a terminal success or cancellation.

A recovered definitive operation is journaled with `recoveredReadOnly: true` and a non-replay parameter hash; `begin_registration` for that original ID is refused with `recovered_operation_read_only`, including after bridge restart. There is no automatic retry of hardware registration.

If an authoritative registered result already exists but `projectSaved` is false, an explicit recovery request can retry only the normal local `saveConfirmed` callback after the same identity/current-project/duplicate-hardware guards. No native command is needed. A project save failure preserves `registered`, `registrationConfirmed` and the durable native evidence; it keeps `projectSaved` false. The client must never downgrade an already confirmed registration merely because another history query is missing or unavailable.

## State directory and diagnostics

The GUI acquires a `FileShare.None` lease for `%LOCALAPPDATA%\StreletzBridge\bridge-state.lock` before loading pairing settings, profiles or the journal. This serializes new bridge versions that cooperate with the lease. Close older bridge processes when updating; an older release does not know this lease.

The existing journal path remains `%LOCALAPPDATA%\StreletzBridge\registration-journal.json`. Atomic persistence keeps the previous validated bytes as `registration-journal.json.bak`. A content digest detects unexpected external modification or removal before another write. A corrupt current journal fails closed; the application does not silently load the backup, recreate history or treat missing rows as unstarted hardware operations.

Connection diagnostics include journal existence/startup absence, backup existence, operation/unknown/recovered counts and probe outcomes. Project/catalog/journal storage diagnostics report only a reviewed category, runtime exception type and HRESULT. Raw exception messages, project contents, device keys, passwords, pairing codes and full file paths are not copied into these diagnostics. User-facing storage messages provide a short explanation and the action `Сохранить копию → Открыть проект`; type/HRESULT details stay in the diagnostic report.

Version 0.5.10 retains `nativeCompletionLastCheck` separately from a terminal completion timeout. It accepts only the reviewed completion-check enum. `nativeSuccessDialogSuppressed` and capability `suppressNativeSuccessDialog` accept actual JSON booleans only; missing fields from an older plugin remain absent. These fields describe the previous native operation and do not prove a new registration, release a hardware operation, or authorize replay. They survive the operation journal and the saved diagnostic report.

`native_result_mode_unavailable` proves a pre-dispatch refusal when the native plugin cannot prepare the reviewed result-dialog mode. It is reported as a failed, stopped, unconfirmed new request. The bridge sends no automatic second start, status or cancel after this explicit refusal.

An `IOException` with exact HRESULT `0x80070497` is classified as `replace_target_remove_failed`. Windows could not remove the file being replaced; the specific cause still requires local inspection. The bridge recommends an explicit copy under a new name in an accessible local directory, followed by opening the copy. It does not delete the original or switch to a non-atomic overwrite. This catalog/project-save issue remains separate from a confirmed device registration. See [Microsoft ReplaceFileW return values](https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-replacefilew).

## Verification

`RegistrationRecoverySelfTests` uses the production driver and coordinator with a synthetic native transport and temporary files. It checks retained success/failure/cancellation, absent/expired/active/unknown states, original-vs-current fingerprint changes, identity/route/evidence mismatches, process/project drift, authentication envelope round trips, no start/cancel/KEY in recovery, durable-before-project-save ordering, no replay after restart, a blocked then writable project target, state-folder lease, valid backup preservation, corrupt/externally altered journals, and secret-free storage diagnostics. The existing coordinator/native routing tests are retained. These tests do not exercise a physical panel or the user's Master instance.
