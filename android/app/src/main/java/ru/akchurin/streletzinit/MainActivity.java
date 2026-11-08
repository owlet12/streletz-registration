// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;

import android.Manifest;
import android.app.Activity;
import android.app.AlertDialog;
import android.content.ContentResolver;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.Drawable;
import android.net.Uri;
import android.os.Bundle;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.text.Editable;
import android.text.TextUtils;
import android.text.TextWatcher;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.view.inputmethod.InputMethodManager;
import android.content.Context;
import android.widget.AdapterView;
import android.widget.ArrayAdapter;
import android.widget.BaseAdapter;
import android.widget.Button;
import android.widget.CheckBox;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.ListView;
import android.widget.ProgressBar;
import android.widget.ScrollView;
import android.widget.Spinner;
import android.widget.TextView;
import android.widget.Toast;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.BufferedWriter;
import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.io.OutputStreamWriter;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.text.SimpleDateFormat;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.Date;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Locale;
import java.util.HashMap;
import java.util.HashSet;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.ConcurrentHashMap;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

public class MainActivity extends Activity {
    private static final int EXPORT_BACKUP = 101;
    private static final int EXPORT_CSV = 102;
    private static final int IMPORT_BACKUP = 103;
    private static final int IMPORT_PROJECT = 104;
    private static final int EXPORT_PROJECT = 105;
    private static final int SCAN_QR = 106;
    private static final int EXPORT_PERFORMANCE = 107;
    private static final int SCAN_BATCH = 108, IMPORT_PLAN = 109, EXPORT_XLSX = 110, EXPORT_PDF = 111;
    private AlertDialog featureDialog;
    private String batchFingerprint = "", planFingerprint = "";
    private boolean previewOpen;

    private static final int LOCAL_NETWORK_PERMISSION = 302;
    private static final int DEFAULT_BRIDGE_PORT = 23019;
    // One storage transaction across Activity recreation; never interrupt its rollback.
    private static final java.util.concurrent.atomic.AtomicBoolean projectInstalling = new java.util.concurrent.atomic.AtomicBoolean();
    private static final ExecutorService projectStorage = Executors.newSingleThreadExecutor();
    private static volatile boolean projectStorageUncertain;
    private boolean screenReady;
    private AlertDialog projectProgress;
    private static final String PROJECT_FILE = "current-project.json";

    private static final String[] STAGE_LABELS = {
            "Не начато", "Найдено", "Инициализировано", "Проверено"
    };
    private static final String[] FILTER_LABELS = {
            "Все этапы", "Требуют работы", "Инициализировано", "Проверено"
    };

    private final List<Device> devices = new ArrayList<>();
    private final List<Device> visible = new ArrayList<>();
    private Map<String, Device> deviceIndex;
    private List<Device> indexedDevices;
    private int indexedDeviceCount = -1;
    private StageCounts stageCounts;
    private DeviceFilterIndex filterIndex;
    private final ExecutorService filterExecutor = Executors.newSingleThreadExecutor();
    private volatile long filterGeneration;
    private boolean filterPending;
    private String pendingScrollAnchor = "";
    private int pendingScrollOffset;
    private boolean startupRequested;
    private Intent pendingFileResult;
    private int pendingFileRequest;
    private int pendingFileResultCode;
    private boolean hasPendingFileResult;
    private Runnable pendingFileDispatch;
    private String pendingViewJson;
    private SharedPreferences prefs;
    private SharedPreferences bridgePrefs;
    private DeviceAdapter deviceAdapter;
    private EditText searchBox;
    private Spinner stageFilter;
    private Spinner groupFilter;
    private Spinner panelFilter;
    private Spinner typeFilter;
    private final List<String> typeFilterKeys = new ArrayList<>();
    private final List<String> panelFilterIds = new ArrayList<>();
    private ProjectPanelIndex panelIndex = new ProjectPanelIndex(Collections.emptyList(), Collections.emptyMap());
    private JSONArray projectPanels = new JSONArray();
    private JSONArray projectRecordMetadata = new JSONArray();
    private boolean projectPanelCatalogueComplete;
    private boolean panelCatalogueLoading;
    private int panelRequestGeneration;
    private long lastPanelRefresh;
    private TextView statsText;
    private TextView emptyText;
    private ProgressBar progressBar;
    private TextView menuStatsText;
    private String statsSummary = "";
    private int checkedProgress;
    private TextView bridgeText;
    private TextView siteText;
    private ListView deviceList;
    private TextView menuCatalogueText;
    private String catalogueStatusShort = "";
    private String catalogueStatusDetails = "";
    private String siteName = "Проект";
    private String projectSourceName = "встроенный проект";
    private JSONObject projectRoot;
    private Device scanTargetDevice;
    private EditText scanSerialField;
    private EditText scanKeyField;
    private Runnable pendingNetworkAction;
    private String projectFingerprint;
    private boolean projectSyncLoading;
    private boolean projectStateUncertain;
    private int projectSyncGeneration;
    private int projectUnchangedCount;
    private int projectFailureCount;
    private Runnable pendingSearch;
    private long searchVersion;
    private final SharedPreferences.OnSharedPreferenceChangeListener searchInvalidator =
            (settings, key) -> localPreferenceChanged(key);
    private long projectSyncRetryAfter;
    private Runnable projectSyncPoll;
    private final ExecutorService projectSyncExecutor = Executors.newSingleThreadExecutor();
    private final Handler mainHandler = new Handler(Looper.getMainLooper());
    private final ExecutorService bridgeExecutor = Executors.newFixedThreadPool(2);
    private final ExecutorService bridgeCancelExecutor = Executors.newFixedThreadPool(2);
    private final Set<BridgeTransport.Exchange> bridgeExchanges = ConcurrentHashMap.newKeySet();
    private final Map<String, OperationRequest> operationRequests = new HashMap<>();
    private final Map<String, RegistrationOperation> operations = new HashMap<>();
    private final Map<String, Runnable> operationPolls = new HashMap<>();
    private final Set<String> operationsInFlight = new HashSet<>();
    // If a durable retirement and its rollback both fail, activity recreation must remain fail closed.
    private static final Set<String> retirementStorageUncertain = ConcurrentHashMap.newKeySet();
    private DeviceCard activeCard;
    private static final FreshProjectSession projectSession = new FreshProjectSession();
    private boolean freshProjectLaunch;
    private boolean waitingForProject;
    private boolean foreground;
    private volatile boolean destroyed;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        freshProjectLaunch = projectSession.begin(savedInstanceState != null);
        if (Build.VERSION.SDK_INT >= 33)
            getOnBackInvokedDispatcher().registerOnBackInvokedCallback(
                    android.window.OnBackInvokedDispatcher.PRIORITY_DEFAULT, this::finish);
        if(savedInstanceState!=null) {batchFingerprint=savedInstanceState.getString("batchFingerprint", "");planFingerprint=savedInstanceState.getString("planFingerprint", "");}
        prefs = getSharedPreferences("commissioning", MODE_PRIVATE);
        bridgePrefs = getSharedPreferences("windows_bridge", MODE_PRIVATE);

        if (savedInstanceState != null && savedInstanceState.getBoolean("pending_file_result", false)) {
            hasPendingFileResult = true;
            pendingFileRequest = savedInstanceState.getInt("pending_file_request");
            pendingFileResultCode = savedInstanceState.getInt("pending_file_code");
            pendingFileResult = savedInstanceState.getParcelable("pending_file_data");
        }
        if (!freshProjectLaunch && savedInstanceState != null) pendingViewJson = savedInstanceState.getString("client_screen_state");
        initializeScreenWhenStorageReady();
    }

    @Override
    protected void onSaveInstanceState(Bundle state) {
        state.putString("batchFingerprint", batchFingerprint);state.putString("planFingerprint", planFingerprint);
        super.onSaveInstanceState(state);
        String view = saveScreenState();
        if (view != null) state.putString("client_screen_state", view);
        state.putBoolean("pending_file_result", hasPendingFileResult);
        if (hasPendingFileResult) {
            state.putInt("pending_file_request", pendingFileRequest);
            state.putInt("pending_file_code", pendingFileResultCode);
            state.putParcelable("pending_file_data", pendingFileResult);
        }
    }

    private String saveScreenState() {
        if (!screenReady || destroyed || waitingForProject || searchBox == null || projectFingerprint == null) return pendingViewJson;
        try {
            ClientScreenState state = new ClientScreenState();
            state.fingerprint = projectFingerprint;
            state.query = searchBox.getText().toString();
            state.stage = stageFilter.getSelectedItemPosition();
            state.group = String.valueOf(groupFilter.getSelectedItem());
            state.panel = selectedPanelFilter();
            state.deviceType = selectedTypeFilter();
            int first = deviceList.getFirstVisiblePosition();
            state.anchor = first >= 0 && first < visible.size() ? visible.get(first).id : pendingScrollAnchor;
            state.offset = deviceList.getChildCount() > 0 ? deviceList.getChildAt(0).getTop() : pendingScrollOffset;
            if (activeCard != null) {
                DeviceCard card = activeCard;
                state.card = card.device.id;
                state.recordUpdated = getUpdated(card.device);
                state.draftStage = card.stage.getSelectedItemPosition();
                state.serial = card.serial.getText().toString();
                state.deviceKey = card.deviceKey.getText().toString();
                state.note = card.note.getText().toString();
                state.description = card.description.getText().toString();
            }
            String raw = state.encode();
            bridgePrefs.edit().putString("view-state:" + projectFingerprint, raw).apply();
            return raw;
        } catch (Exception ignored) { return null; }
    }

    private void restoreScreenState() {
        ClientScreenState state = ClientScreenState.decode(pendingViewJson, projectFingerprint);
        if (state == null) state = ClientScreenState.decode(bridgePrefs.getString("view-state:" + projectFingerprint, ""), projectFingerprint);
        pendingViewJson = null;
        if (state == null) return;
        searchBox.setText(state.query);
        stageFilter.setSelection(state.stage);
        for (int i = 0; i < groupFilter.getCount(); i++)
            if (state.group.equals(String.valueOf(groupFilter.getItemAtPosition(i)))) groupFilter.setSelection(i);
        panelFilter.setSelection(Math.max(0, panelFilterIds.indexOf(state.panel)));
        selectTypeFilter(state.deviceType);
        pendingScrollAnchor = state.anchor;
        pendingScrollOffset = state.offset;
        Device device = findById(state.card);
        if (device == null) return;
        editDevice(device);
        DeviceCard card = activeCard;
        RegistrationOperation operation = operationFor(device);
        boolean unresolved = operation != null && operation.needsResolution();
        boolean storedUnresolved = operation == null && (!bridgePrefs.getString(operationPreferenceKey(projectFingerprint, device.id), "").isEmpty()
                || importedRegistrationNeedsResolution(device));
        if (!unresolved && !storedUnresolved && getUpdated(device) == state.recordUpdated) {
            card.serial.setText(state.serial);
            card.deviceKey.setText(state.deviceKey);
            card.stage.setSelection(state.draftStage);
            card.note.setText(state.note);
            card.description.setText(state.description);
        }
        scanTargetDevice = device;
        scanSerialField = card.serial;
        scanKeyField = card.deviceKey;
        refreshRegistrationCard(card);
    }

    private void dispatchPendingFileResult() {
        if (destroyed || !hasPendingFileResult) return;
        if (pendingFileDispatch != null) mainHandler.removeCallbacks(pendingFileDispatch);
        if (!screenReady || projectInstalling.get()) {
            pendingFileDispatch = this::dispatchPendingFileResult;
            mainHandler.postDelayed(pendingFileDispatch, 100);
            return;
        }
        hasPendingFileResult = false;
        Intent data = pendingFileResult;
        pendingFileResult = null;
        pendingFileDispatch = null;
        handleActivityResult(pendingFileRequest, pendingFileResultCode, data);
    }

    private void initializeScreenWhenStorageReady() {
        if (destroyed) return;
        if (projectInstalling.get()) {
            setContentView(new ProgressBar(this));
            mainHandler.postDelayed(this::initializeScreenWhenStorageReady, 100);
            return;
        }
        if (startupRequested) return;
        startupRequested = true;
        projectInstalling.set(true);
        setContentView(new ProgressBar(this));
        projectStorage.execute(() -> {
            long started = System.nanoTime();
            JSONObject loaded = null;
            String fingerprint = null, source = null;
            Exception failure = null;
            try {
                File target = new File(getFilesDir(), PROJECT_FILE);
                ProjectTransaction.recover(target, this::restoreProjectPreferences);
                if (freshProjectLaunch) clearProjectCache();
                if (target.isFile()) {
                    loaded = parseProject(ProjectTransfer.read(target));
                    source = bridgePrefs.getString("project_source", "проект текущего сеанса");
                    fingerprint = ProjectTransfer.fingerprint(loaded);
                    loadProject(loaded, fingerprint);
                } else {
                    loaded = new JSONObject().put("schemaVersion", 1).put("siteName", "Проект не загружен")
                            .put("records", new JSONArray());
                    source = "ожидание загрузки";
                    fingerprint = FreshProjectSession.EMPTY_FINGERPRINT;
                    loadProject(loaded, fingerprint, true);
                }
                ExportSnapshot.PreparedList list = captureExport().prepareList();
                filterIndex = list.index;
                stageCounts = list.counts;
                projectStorageUncertain = false;
            } catch (Exception error) { failure = error; projectStorageUncertain = true; }
            PerformanceMetrics.INSTANCE.record(PerformanceMetrics.Task.PROJECT_LOAD, System.nanoTime() - started, devices.size(), failure == null);
            String sourceName = source;
            Exception error = failure;
            new Handler(Looper.getMainLooper()).post(() -> {
                projectInstalling.set(false);
                if (destroyed) return;
                try {
                    if (error != null) throw error;
                    projectSourceName = sourceName;
                    projectStateUncertain = false;
                    prefs.registerOnSharedPreferenceChangeListener(searchInvalidator);
                    buildScreen();
                    screenReady = true;
                    if (!waitingForProject) restoreScreenState();
                    else setCatalogueStatus("Проект · ожидает загрузки", bridgeConfigured()
                            ? "При открытии загружается свежий проект Windows. Если связи нет, нажмите здесь для повторной загрузки."
                            : "Настройте Windows-мост или откройте файл проекта. После закрытия локальная база очищается.");
                    applyFilters();
                    dispatchPendingFileResult();
                    if (foreground && !projectInstalling.get()) resumeForegroundWork();
                } catch (Exception problem) { showFatalError("Не удалось восстановить или открыть проект", problem); }
            });
        });
    }

    @Override
    protected void onStart() {
        super.onStart();
        foreground = true;
        if (screenReady && !projectInstalling.get()) resumeForegroundWork();
    }

    private void resumeForegroundWork() {
        resumeRegistrations();
        if (activeCard != null) scheduleCardClock(activeCard);
        if (bridgeConfigured()) {
            if (autoProjectEnabled()) scheduleProjectSync(0);
            else if (activeCard == null) refreshProjectPanels(false);
        }
    }

    @Override
    protected void onStop() {
        saveScreenState();
        foreground = false;
        if (activeCard != null && activeCard.clock != null) mainHandler.removeCallbacks(activeCard.clock);
        invalidateProjectSync();
        for (Runnable callback : operationPolls.values()) mainHandler.removeCallbacks(callback);
        operationPolls.clear();
        super.onStop();
    }

    @Override
    protected void onDestroy() {
        destroyed = true;
        if (projectProgress != null) projectProgress.dismiss();
        if (prefs != null) prefs.unregisterOnSharedPreferenceChangeListener(searchInvalidator);
        mainHandler.removeCallbacksAndMessages(null);
        for (BridgeTransport.Exchange exchange : bridgeExchanges) exchange.abort();
        operationRequests.clear();
        operationsInFlight.clear();
        filterGeneration++;
        filterExecutor.shutdownNow();
        bridgeExecutor.shutdownNow();
        bridgeCancelExecutor.shutdownNow();
        projectSyncExecutor.shutdownNow();
        activeCard = null;
        if (isFinishing()) {
            projectSession.end();
            // Runs after any in-flight transaction/rollback. A killed process is cleaned on next launch.
            projectStorage.execute(() -> {
                try {
                    ProjectTransaction.recover(new File(getFilesDir(), PROJECT_FILE), this::restoreProjectPreferences);
                    clearProjectCache();
                } catch (Exception ignored) { /* Startup retries cleanup and fails closed if it cannot finish. */ }
            });
        }
        super.onDestroy();
    }

    private void buildScreen() {
        boolean compactHeight = getResources().getConfiguration().screenHeightDp < 480
                && getResources().getConfiguration().screenWidthDp
                > getResources().getConfiguration().screenHeightDp;
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setBackgroundColor(color("surface"));

        LinearLayout header = new LinearLayout(this);
        header.setOrientation(LinearLayout.HORIZONTAL);
        header.setGravity(Gravity.CENTER_VERTICAL);
        header.setMinimumHeight(dp(48));
        header.setPadding(dp(12), 0, dp(12), 0);
        header.setBackground(resourceDrawable("header_background"));

        LinearLayout heading = new LinearLayout(this);
        heading.setOrientation(LinearLayout.VERTICAL);
        heading.setPadding(0, dp(4), dp(6), dp(4));
        heading.addView(singleLineText("Стрелец · " + appVersionName(), 14, Color.WHITE, true), matchWrap());
        siteText = singleLineText(siteName, 13, Color.rgb(205, 220, 229), false);
        heading.addView(siteText, matchWrap());
        header.addView(heading, new LinearLayout.LayoutParams(0,
                ViewGroup.LayoutParams.WRAP_CONTENT, 1));

        bridgeText = singleLineText("", 13, compactHeight ? Color.rgb(205, 220, 229)
                : color("text_secondary"), false);
        bridgeText.setGravity(Gravity.CENTER_VERTICAL);
        bridgeText.setOnClickListener(v -> showCatalogueStatus());
        if (compactHeight) {
            bridgeText.setMinimumHeight(dp(48));
            bridgeText.setPadding(dp(4), 0, dp(8), 0);
            header.addView(bridgeText, new LinearLayout.LayoutParams(0,
                    ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        }

        Button menu = compactButton("Меню");
        menu.setTextSize(14);
        menu.setMinHeight(dp(48));
        menu.setMinimumHeight(dp(48));
        menu.setContentDescription("Меню: проект, связь и инструкция регистрации");
        header.addView(menu, new LinearLayout.LayoutParams(dp(72), ViewGroup.LayoutParams.WRAP_CONTENT));
        menu.setOnClickListener(v -> showProjectMenu());
        root.addView(header, matchWrap());

        LinearLayout controls = new LinearLayout(this);
        controls.setOrientation(LinearLayout.VERTICAL);
        controls.setPadding(dp(12), dp(4), dp(12), dp(4));

        searchBox = new EditText(this);
        searchBox.setHint(compactHeight ? "Поиск устройств" : "Поиск: название, описание, зона, ID");
        searchBox.setContentDescription("Поиск по номеру, названию, описанию, зоне, панели и серийному номеру");
        searchBox.setSingleLine(true);
        searchBox.setTextSize(15);
        searchBox.setBackground(resourceDrawable("search_background"));
        searchBox.setMinimumHeight(dp(48));
        searchBox.setMinHeight(dp(48));

        LinearLayout filters = new LinearLayout(this);
        filters.setOrientation(LinearLayout.HORIZONTAL);
        filters.setGravity(Gravity.CENTER_VERTICAL);
        stageFilter = new Spinner(this);
        stageFilter.setAdapter(filterAdapter(FILTER_LABELS));
        stageFilter.setMinimumHeight(dp(48));
        stageFilter.setContentDescription("Фильтр по этапу работы");
        groupFilter = new Spinner(this);
        groupFilter.setAdapter(filterAdapter(buildGroupLabels()));
        groupFilter.setMinimumHeight(dp(48));
        groupFilter.setContentDescription("Фильтр по группе");
        panelFilter = new Spinner(this);
        panelFilter.setMinimumHeight(dp(48));
        panelFilter.setContentDescription("Фильтр по панели проекта");
        rebuildPanelFilter();
        typeFilter = new Spinner(this);
        typeFilter.setMinimumHeight(dp(48));
        typeFilter.setContentDescription("Фильтр по типу устройства");
        rebuildTypeFilter(null);
        if (compactHeight) {
            filters.addView(searchBox, new LinearLayout.LayoutParams(0,
                    ViewGroup.LayoutParams.WRAP_CONTENT, 1.4f));
        } else {
            controls.addView(searchBox, matchWrap());
        }
        filters.addView(stageFilter, weighted());
        filters.addView(groupFilter, weightedWithStartMargin());
        controls.addView(filters, matchWrap());
        LinearLayout deviceFilters = new LinearLayout(this);
        deviceFilters.setOrientation(LinearLayout.HORIZONTAL);
        deviceFilters.setGravity(Gravity.CENTER_VERTICAL);
        deviceFilters.addView(panelFilter, weighted());
        deviceFilters.addView(typeFilter, weightedWithStartMargin());
        controls.addView(deviceFilters, matchWrap());

        statsText = singleLineText("", 14, color("text_primary"), true);
        statsText.setPadding(dp(2), dp(2), dp(2), dp(2));
        if (!compactHeight) {
            controls.addView(statsText, matchWrap());
            LinearLayout bridge = new LinearLayout(this);
            bridge.setOrientation(LinearLayout.HORIZONTAL);
            bridge.setGravity(Gravity.CENTER_VERTICAL);
            bridgeText.setMinimumHeight(dp(40));
            bridge.addView(bridgeText, new LinearLayout.LayoutParams(0,
                    ViewGroup.LayoutParams.WRAP_CONTENT, 1));
            controls.addView(bridge, matchWrap());
        }
        updateBridgeText();
        root.addView(controls, matchWrap());

        FrameLayout listFrame = new FrameLayout(this);
        ListView list = new ListView(this);
        deviceList = list;
        list.setDivider(null);
        list.setDividerHeight(dp(8));
        list.setPadding(dp(12), dp(4), dp(12), dp(18));
        list.setClipToPadding(false);
        deviceAdapter = new DeviceAdapter();
        list.setAdapter(deviceAdapter);
        list.setOnItemClickListener((parent, view, position, id) -> editDevice(visible.get(position)));
        listFrame.addView(list, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));

        emptyText = text("По выбранным условиям ничего не найдено", 15,
                color("text_secondary"), false);
        emptyText.setGravity(Gravity.CENTER);
        emptyText.setOnClickListener(v -> { if (waitingForProject) refreshProjectFromWindows(true); });
        emptyText.setVisibility(View.GONE);
        listFrame.addView(emptyText, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        root.addView(listFrame, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, 0, 1));
        setContentView(root);

        searchBox.addTextChangedListener(new TextWatcher() {
            public void beforeTextChanged(CharSequence s, int start, int count, int after) { }
            public void onTextChanged(CharSequence s, int start, int before, int count) {
                filterGeneration++; // Reject results for the previous text during the debounce interval.
                if (pendingSearch != null) mainHandler.removeCallbacks(pendingSearch);
                pendingSearch = () -> { pendingSearch = null; if (!destroyed) applyFilters(); };
                mainHandler.postDelayed(pendingSearch, 180);
            }
            public void afterTextChanged(Editable s) { }
        });
        AdapterView.OnItemSelectedListener listener = new AdapterView.OnItemSelectedListener() {
            public void onItemSelected(AdapterView<?> p, View v, int position, long id) { applyFilters(); }
            public void onNothingSelected(AdapterView<?> p) { }
        };
        stageFilter.setOnItemSelectedListener(listener);
        groupFilter.setOnItemSelectedListener(listener);
        typeFilter.setOnItemSelectedListener(listener);
        panelFilter.setOnItemSelectedListener(new AdapterView.OnItemSelectedListener() {
            public void onItemSelected(AdapterView<?> p, View v, int position, long id) {
                bridgePrefs.edit().putString("panel-filter:" + projectFingerprint, selectedPanelFilter()).apply();
                applyFilters();
            }
            public void onNothingSelected(AdapterView<?> p) { }
        });
    }

    private String appVersionName() {
        try {
            String version = getPackageManager().getPackageInfo(getPackageName(), 0).versionName;
            return version == null ? "—" : version;
        } catch (PackageManager.NameNotFoundException ignored) {
            return "—";
        }
    }

    private void showProjectMenu() {
        hideKeyboard(searchBox);
        LinearLayout form = new LinearLayout(this);
        form.setOrientation(LinearLayout.VERTICAL);
        form.setPadding(dp(20), 0, dp(20), dp(12));
        form.addView(text(siteName + "\n" + projectSourceName, 15,
                color("text_primary"), true), matchWrap());
        TextView summary = text(statsSummary, 13, color("text_secondary"), false);
        summary.setPadding(0, dp(8), 0, dp(4));
        menuStatsText = summary;
        form.addView(summary, matchWrap());
        ProgressBar progress = new ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal);
        progress.setMax(1000);
        progress.setProgress(checkedProgress);
        progressBar = progress;
        form.addView(progress, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(8)));

        ScrollView scroll = new ScrollView(this);
        scroll.addView(form);
        AlertDialog menu = new AlertDialog.Builder(this)
                .setTitle("Проект и связь")
                .setView(scroll)
                .setNegativeButton("Закрыть", null)
                .create();
        form.addView(fieldLabel("Проект и журнал"), matchWrap());
        form.addView(text("Для переноса работы из прежнего приложения выберите в нём «Передать в Windows», "
                        + "затем здесь «Открыть файл проекта». Копия журнала восстанавливается в соответствующий проект.",
                12, color("text_secondary"), false), matchWrap());
        addMenuAction(form, "Загрузить проект из Windows", () -> { menu.dismiss(); refreshProjectFromWindows(true); });
        form.addView(text("При закрытии база проекта очищается. При новом запуске она загружается с моста. Настройки связи и журнал работ сохраняются.",
                12, color("text_secondary"), false), matchWrap());
        addMenuAction(form, "Закрыть и очистить базу", () -> { menu.dismiss(); finish(); });
        addMenuAction(form, "Проекты текущего сеанса", () -> { menu.dismiss(); chooseSavedProject(); });
        addMenuAction(form, "Открыть файл проекта", () -> { menu.dismiss(); chooseProjectSource(); });
        addMenuAction(form, "Передать в Windows", () -> { menu.dismiss(); chooseProjectDestination(); });
        addMenuAction(form, "Сохранить копию журнала", () -> { menu.dismiss(); chooseBackupDestination(); });
        addMenuAction(form, "Восстановить журнал", () -> { menu.dismiss(); chooseBackupSource(); });
        addMenuAction(form, "Список QR · пакетное сканирование", () -> { menu.dismiss(); showBatch(); });
        addMenuAction(form, "Планы помещений", () -> { menu.dismiss(); showPlans(); });
        addMenuAction(form, "Отчёт Excel · все и незавершённые", () -> { menu.dismiss(); chooseWorkReport(false); });
        addMenuAction(form, "Отчёт PDF · все и незавершённые", () -> { menu.dismiss(); chooseWorkReport(true); });
        addMenuAction(form, "Отчёт CSV", () -> { menu.dismiss(); chooseCsvDestination(); });
        addMenuAction(form, "Отчёт о скорости", () -> { menu.dismiss(); choosePerformanceDestination(); });
        form.addView(fieldLabel("Связь"), matchWrap());
        TextView catalogueDetails = text(catalogueStatusDetails, 13, color("text_secondary"), false);
        menuCatalogueText = catalogueDetails;
        form.addView(catalogueDetails, matchWrap());
        CheckBox automatic = new CheckBox(this);
        automatic.setText("Обновлять проект, пока приложение открыто");
        automatic.setChecked(autoProjectEnabled());
        automatic.setOnCheckedChangeListener((button, enabled) -> {
            bridgePrefs.edit().putBoolean("auto_project", enabled).apply();
            invalidateProjectSync();
            if (enabled) refreshProjectFromWindows(true);
        });
        form.addView(automatic, matchWrap());
        addMenuAction(form, "Проверить подключение", () -> { menu.dismiss(); checkConnection(); });
        addMenuAction(form, "Настроить Windows-мост", () -> { menu.dismiss(); showBridgeSettings(null); });
        addMenuAction(form, "Обновить панели проекта", () -> { menu.dismiss(); refreshProjectPanels(true); });
        TextView connection = text("Облако Стрелец · порт 9010", 13, color("text_secondary"), false);
        form.addView(connection, matchWrap());
        Button cloud = menuButton("Проверить облако");
        form.addView(cloud, matchWrap());
        cloud.setOnClickListener(v -> checkCloudConnection(cloud, connection));
        form.addView(fieldLabel("Как зарегистрировать датчик"), matchWrap());
        form.addView(text("Настройте Windows-мост и откройте карточку устройства: панель определяется по проекту. "
                        + "Регистрация запускается из карточки; после состояния „Ожидание датчика“ нажмите ПРОГ.",
                14, color("text_secondary"), false), matchWrap());
        menu.setOnDismissListener(ignored -> {
            if (menuStatsText == summary) menuStatsText = null;
            if (progressBar == progress) progressBar = null;
            if (menuCatalogueText == catalogueDetails) menuCatalogueText = null;
        });
        menu.show();
    }

    private void addMenuAction(LinearLayout form, String label, Runnable action) {
        Button button = menuButton(label);
        button.setOnClickListener(v -> action.run());
        form.addView(button, matchWrap());
    }

    private Button menuButton(String label) {
        Button button = new Button(this);
        button.setText(label);
        button.setTextSize(15);
        button.setAllCaps(false);
        button.setMinHeight(dp(48));
        button.setMinimumHeight(dp(48));
        return button;
    }

    @Override
    public void onBackPressed() { finish(); }

    private void clearProjectCache() throws Exception {
        FreshProjectSession.clearFiles(getFilesDir());
        SharedPreferences.Editor edit = bridgePrefs.edit();
        for (String key : bridgePrefs.getAll().keySet())
            if (FreshProjectSession.cachedPreference(key)) edit.remove(key);
        if (!edit.putBoolean("auto_project", true).commit())
            throw new IOException("Не удалось очистить настройки локальной базы.");
    }

    private void loadProject(JSONObject loaded, String loadedFingerprint) throws Exception {
        loadProject(loaded, loadedFingerprint, false);
    }

    private void loadProject(JSONObject loaded, String loadedFingerprint, boolean emptySession) throws Exception {
        if (!emptySession) migrateLegacyJournal(loaded, loadedFingerprint);
        waitingForProject = emptySession;
        projectRoot = loaded;
        projectFingerprint = loadedFingerprint;
        siteName = projectRoot.optString("siteName", "Проект");
        devices.clear();
        deviceIndex = new HashMap<>();
        stageCounts = null;
        filterIndex = null;
        filterGeneration++;
        indexedDevices = devices;
        indexedDeviceCount = -1;
        projectRecordMetadata = new JSONArray();
        JSONArray records = projectRoot.getJSONArray("records");
        Set<String> ids = new LinkedHashSet<>();

        for (int i = 0; i < records.length(); i++) {
            JSONObject row = records.getJSONObject(i);
            String id = row.getString("id");
            if (!ids.add(id.toLowerCase(Locale.ROOT))) throw new JSONException("В проекте повторяется ID " + id);
            if (!"device".equals(row.optString("recordKind"))) continue;
            Device device = new Device();
            device.id = id;
            device.name = row.optString("name");
            device.description = row.optString("description");
            device.version = row.optString("version");
            device.zone = row.optString("zone");
            device.group = row.optString("group");
            device.type = row.optString("type");
            device.sourceRow = row.optInt("sourceRow");
            device.sourceInitialized = "+".equals(row.optString("initialized"));
            device.commissioning = row.optJSONObject("commissioning");
            devices.add(device);
            deviceIndex.put(id.toLowerCase(Locale.ROOT), device);
        }
        indexedDeviceCount = devices.size();
        SharedPreferences.Editor panelSelections = bridgePrefs.edit();
        for (int i = 0; i < records.length(); i++) {
            JSONObject row = records.getJSONObject(i);
            Device device = deviceIndex.get(row.optString("id").toLowerCase(Locale.ROOT));
            JSONObject commissioning = row.optJSONObject("commissioning");
            if (device != null && commissioning != null && !bridgePrefs.contains(panelPreferenceKey(device))) {
                String panelId = commissioning.optString("panelId", "");
                if (!panelId.isEmpty()) panelSelections.putString(panelPreferenceKey(device), panelId);
            }
        }
        panelSelections.apply();
        if (devices.isEmpty() && !emptySession) throw new JSONException("В проекте нет устройств для инициализации.");
        Collections.sort(devices, Comparator.comparingInt(d -> d.sourceRow));
        for (int i = 0; i < devices.size(); i++) devices.get(i).listOrder = i;
        installProjectCatalogue(projectRoot.optJSONArray("panels"), null, true);
        projectPanelCatalogueComplete = projectRoot.optBoolean("panelCatalogComplete", false)
                && projectFingerprint.equalsIgnoreCase(projectRoot.optString("panelMappingFingerprint", ""));
        String cached = bridgePrefs.getString(panelCataloguePreferenceKey(), "");
        if (!cached.isEmpty()) {
            try {
                JSONObject value = new JSONObject(cached);
                if (projectFingerprint.equals(value.optString("projectFingerprint"))) {
                    installProjectCatalogue(value.optJSONArray("panels"), value.optJSONArray("recordMetadata"), false);
                    projectPanelCatalogueComplete = value.optBoolean("catalogComplete", false);
                }
            } catch (JSONException | IllegalArgumentException ignored) {
                bridgePrefs.edit().remove(panelCataloguePreferenceKey()).apply();
            }
        }
        setCatalogueStatus(panelIndex.panels().isEmpty() ? "Панели ещё не загружены"
                        : "Панелей: " + panelIndex.panels().size() + " · сохранено",
                panelIndex.panels().isEmpty()
                        ? "Подключите Windows-мост и откройте в нём проект для получения панелей."
                        : "Показан сохранённый каталог проекта. Доступность регистрации уточняется через Windows-мост.");
    }

    private JSONObject parseProject(String raw) throws JSONException {
        JSONObject root = new JSONObject(raw);
        int schema = root.optInt("schemaVersion", 0);
        if (schema != 1 || !root.has("records")) {
            throw new JSONException("Неподдерживаемый файл проекта. Требуется формат проекта версии 1.");
        }
        root.getJSONArray("records");
        return root;
    }

    private void migrateLegacyJournal(JSONObject project, String fingerprint) throws JSONException, IOException {
        if (prefs.getBoolean("journal_scoped_v1", false)) return;
        SharedPreferences.Editor editor = prefs.edit();
        Map<String, ?> legacy = prefs.getAll();
        JSONArray rows = project.getJSONArray("records");
        for (int i = 0; i < rows.length(); i++) {
            JSONObject row = rows.getJSONObject(i);
            if (!"device".equals(row.optString("recordKind"))) continue;
            String id = row.getString("id");
            for (String field : new String[]{"stage", "serial", "device_key", "note", "updated"}) {
                String target = field + ":" + fingerprint + ":" + id.toLowerCase(Locale.ROOT);
                Object value = legacy.get(field + ":" + id);
                if (prefs.contains(target) || value == null) continue;
                if (value instanceof Integer) editor.putInt(target, (Integer) value);
                else if (value instanceof Long) editor.putLong(target, (Long) value);
                else if (value instanceof String) editor.putString(target, (String) value);
            }
        }
        // Legacy GUID-only data belongs only to the project open during this one-time migration.
        // It is never reused for another project's equal GUID.
        if (!editor.putBoolean("journal_scoped_v1", true).commit())
            throw new IOException("Не удалось сохранить журнал текущего проекта.");
    }

    private boolean autoProjectEnabled() { return bridgePrefs.getBoolean("auto_project", true); }

    private void invalidateProjectSync() {
        projectSyncGeneration++;
        projectSyncLoading = false;
        if (projectSyncPoll != null) mainHandler.removeCallbacks(projectSyncPoll);
        projectSyncPoll = null;
    }

    private void scheduleProjectSync(long delay) {
        if (projectSyncPoll != null) mainHandler.removeCallbacks(projectSyncPoll);
        projectSyncPoll = null;
        if (destroyed || !foreground || !autoProjectEnabled() || !bridgeConfigured()) return;
        projectSyncPoll = () -> {
            projectSyncPoll = null;
            refreshProjectFromWindows(false);
        };
        mainHandler.postDelayed(projectSyncPoll, delay);
    }

    private boolean hasUnresolvedRegistrations() {
        if (!operationsInFlight.isEmpty()) return true;
        for (Device device : devices) {
            RegistrationOperation operation = operationFor(device);
            if (operation != null && operation.needsResolution()) return true;
            if (operation == null && importedRegistrationNeedsResolution(device)) return true;
            if (operation == null && !bridgePrefs.getString(
                    operationPreferenceKey(projectFingerprint, device.id), "").isEmpty()) return true;
        }
        return false;
    }

    private void refreshProjectFromWindows(boolean requested) {
        if (destroyed || !foreground || projectFingerprint == null) return;
        if (!bridgeConfigured()) {
            if (requested) showBridgeSettings(() -> refreshProjectFromWindows(true));
            return;
        }
        if (projectSyncLoading) return;
        if (previewOpen || featureBusy() || activeCard != null || hasUnresolvedRegistrations()) {
            if (requested) toast("Проект обновится после закрытия карточки и завершения текущей регистрации.");
            scheduleProjectSync(ProjectTransfer.POLL_MILLIS);
            return;
        }
        if (!requested && System.currentTimeMillis() < projectSyncRetryAfter) {
            scheduleProjectSync(Math.max(ProjectTransfer.POLL_MILLIS,
                    projectSyncRetryAfter - System.currentTimeMillis()));
            return;
        }
        if (!hasLocalNetworkPermission()) {
            if (requested) ensureLocalNetworkPermission(() -> fetchProjectFromWindows(true));
            else {
                setCatalogueStatus("Проект · нужен доступ к сети", "Разрешите доступ к локальной сети: нажмите строку связи и «Загрузить проект».");
                scheduleProjectSync(ProjectTransfer.POLL_MILLIS);
            }
            return;
        }
        fetchProjectFromWindows(requested);
    }

    private void fetchProjectFromWindows(boolean requested) {
        if (destroyed || !screenReady || projectInstalling.get() || !foreground || projectSyncLoading || previewOpen || featureBusy() || activeCard != null || hasUnresolvedRegistrations()) {
            scheduleProjectSync(ProjectTransfer.POLL_MILLIS);
            return;
        }
        final BridgeConfig config;
        final JSONObject request;
        final String sentFingerprint = projectFingerprint;
        final String knownRevision;
        try {
            config = bridgeConfig();
            // Revisions are endpoint-bound. The pairing code is never copied into project files.
            String endpointKey = config.host + ":" + config.port;
            boolean sameSource = endpointKey.equals(bridgePrefs.getString("sync_endpoint", ""))
                    && sentFingerprint.equals(bridgePrefs.getString("sync_fingerprint", ""));
            knownRevision = sameSource ? bridgePrefs.getString("sync_revision", "") : "";
            request = newBridgeRequest("get_project", UUID.randomUUID().toString());
            request.put("knownProjectFingerprint", waitingForProject ? "" : sentFingerprint);
            request.put("knownProjectRevision", knownRevision);
        } catch (Exception error) {
            setCatalogueStatus("Проект · проверьте настройки", bridgeError(error));
            scheduleProjectSync(ProjectTransfer.POLL_MILLIS);
            return;
        }
        if (requested) { projectUnchangedCount = 0; projectFailureCount = 0; }
        final int generation = ++projectSyncGeneration;
        projectSyncLoading = true;
        if (requested || knownRevision.isEmpty())
            setCatalogueStatus("Проект · загрузка…", "Получение открытого проекта Windows. Локальные записи журнала сохраняются.");
        projectSyncExecutor.execute(() -> {
            JSONObject result = null;
            Exception failure = null;
            ProjectTransfer.Snapshot prepared = null;
            try {
                result = callBridge(config, request);
                prepared = ProjectTransfer.response(result, sentFingerprint, knownRevision);
            } catch (Exception error) { failure = error; }
            final ProjectTransfer.Snapshot snapshot = prepared;
            final JSONObject response = result;
            final Exception error = failure;
            onUiWhenStorageReady(() -> {
                if (destroyed || generation != projectSyncGeneration) return;
                projectSyncLoading = false;
                try {
                    if (!ProjectTransfer.accepts(generation, projectSyncGeneration, sentFingerprint,
                            projectFingerprint, config.sameEndpointAndCode(bridgeConfig()), foreground)) return;
                    if (previewOpen || featureBusy() || activeCard != null || hasUnresolvedRegistrations()) {
                        setCatalogueStatus("Проект · обновление отложено", "Закройте карточку и дождитесь результата текущей регистрации. Затем проект обновится автоматически.");
                        return;
                    }
                    if (error != null) throw error;
                    if (!snapshot.notModified) {
                        if (waitingForProject) {
                            installDownloadedProject(snapshot, problem -> {
                                if (problem != null) reportProjectDownloadFailure(response, problem, requested);
                                else {
                                    finishProjectDownload(config, snapshot, requested);
                                    resumeRegistrations();
                                }
                                scheduleProjectSync(ClientUxPolicy.projectDelay(projectUnchangedCount, projectFailureCount));
                            });
                            return;
                        }
                        previewProject(() -> snapshot.project, "из Windows", () -> {
                            setCatalogueStatus("Проект · изменения применены", "Выбранные изменения сохранены. Автозагрузка приостановлена, чтобы не заменить ваш выбор.");
                        });
                        return;
                    }
                    finishProjectDownload(config, snapshot, requested);
                } catch (Exception problem) {
                    reportProjectDownloadFailure(response, problem, requested);
                } finally {
                    if (!projectInstalling.get()) scheduleProjectSync(ClientUxPolicy.projectDelay(projectUnchangedCount, projectFailureCount));
                }
            });
        });
    }

    private void finishProjectDownload(BridgeConfig config, ProjectTransfer.Snapshot snapshot, boolean requested) {
        bridgePrefs.edit().putString("sync_endpoint", config.host + ":" + config.port)
                .putString("sync_fingerprint", snapshot.fingerprint)
                .putString("sync_revision", snapshot.revision).apply();
        projectSyncRetryAfter = 0;
        projectFailureCount = 0;
        projectUnchangedCount = snapshot.notModified ? Math.min(8, projectUnchangedCount + 1) : 0;
        setCatalogueStatus("Проект · " + devices.size() + " устройств · Windows",
                "Проект «" + siteName + "» " + (snapshot.notModified ? "актуален" : "загружен из Windows")
                        + ". Панелей: " + panelIndex.panels().size() + ".\n"
                        + (autoProjectEnabled() ? "Автозагрузка включена; изменения проверяются, пока приложение открыто."
                        : "Обновление приостановлено до следующего запуска.")
                        + "\nЖурнал телефона сохранён. Связь с оборудованием проверяется отдельно в карточке датчика.");
        if (requested || !snapshot.notModified)
            toast((snapshot.notModified ? "Проект уже актуален: " : "Проект загружен: ")
                    + siteName + ", устройств: " + devices.size());
    }


    private void reportProjectDownloadFailure(JSONObject response, Exception problem, boolean requested) {
        projectFailureCount = Math.min(8, projectFailureCount + 1);
        String code = response == null ? "" : response.optString("errorCode", "");
        boolean unsupported = "unsupported_request".equals(code) || "unknown_request".equals(code)
                || "unknown_command".equals(code) || "unsupported_command".equals(code)
                || "unsupported_type".equals(code);
        projectSyncRetryAfter = unsupported ? System.currentTimeMillis() + ProjectTransfer.UNSUPPORTED_RETRY_MILLIS : 0;
        String message = unsupported ? "Обновите Windows-мост до версии 0.5.5 или новее для автоматической загрузки проекта."
                : "project_missing".equals(code) ? "Откройте нужный проект в Windows-мосте. Он загрузится на телефон автоматически."
                : bridgeError(problem);
        setCatalogueStatus(unsupported ? "Проект · обновите Windows-мост"
                        : "project_missing".equals(code) ? "Проект · ожидаем Windows"
                        : "Проект · нет обновления",
                message + (projectStateUncertain
                        ? "\nРегистрация приостановлена до восстановления проекта."
                        : "\nТекущий проект и журнал остаются на телефоне."));
        if (requested) toast(message);
    }


    /** Run publication on the UI thread, but keep file IO and durable commits on storage worker. */
    private void publishOnUi(ProjectTransfer.Change change) throws Exception {
        java.util.concurrent.FutureTask<Void> task = new java.util.concurrent.FutureTask<>(() -> {
            change.run();
            return null;
        });
        // Not mainHandler: destroying this Activity removes its ordinary callbacks.
        new Handler(Looper.getMainLooper()).post(task);
        try { task.get(); }
        catch (java.util.concurrent.ExecutionException error) {
            Throwable cause = error.getCause();
            if (cause instanceof Exception) throw (Exception) cause;
            throw new IOException("Не удалось открыть полученный проект.", cause);
        }
    }

    private void onUiWhenStorageReady(Runnable action) {
        mainHandler.post(new Runnable() {
            @Override public void run() {
                if (destroyed) return;
                if (projectInstalling.get()) { mainHandler.postDelayed(this, 50); return; }
                action.run();
            }
        });
    }

    private DurablePreferenceChange.Store preferenceStore(SharedPreferences settings) {
        return new DurablePreferenceChange.Store() {
            public Object get(String key) { return settings.getAll().get(key); }
            public boolean write(Map<String, Object> values) {
                SharedPreferences.Editor edit = settings.edit();
                for (Map.Entry<String, Object> entry : values.entrySet()) {
                    Object value = entry.getValue();
                    if (value == null) edit.remove(entry.getKey());
                    else if (value instanceof String) edit.putString(entry.getKey(), (String) value);
                    else if (value instanceof Integer) edit.putInt(entry.getKey(), (Integer) value);
                    else if (value instanceof Long) edit.putLong(entry.getKey(), (Long) value);
                    else if (value instanceof Boolean) edit.putBoolean(entry.getKey(), (Boolean) value);
                    else throw new IllegalArgumentException("Unsupported preference type");
                }
                return edit.commit();
            }
        };
    }

    private void saveOperationAsync(RegistrationOperation operation, Runnable afterSave) {
        if (!isCurrentOperation(operation, operation.generation) || projectInstalling.get()) return;
        try {
            String key = operationPreferenceKey(operation.projectFingerprint, operation.recordId);
            Map<String, Object> previous = new HashMap<>(), changed = new HashMap<>();
            previous.put(key, bridgePrefs.getAll().get(key));
            changed.put(key, operation.toStored().toString());
            runFileWork("Сохранение операции", () -> DurablePreferenceChange.commit(preferenceStore(bridgePrefs), previous, changed), error -> {
                if (error != null) {
                    if (error instanceof DurablePreferenceChange.Failure && !((DurablePreferenceChange.Failure) error).rollbackSucceeded)
                        retirementStorageUncertain.add(key);
                    operations.remove(key);
                    refreshActiveRegistrationCard(operation);
                    reportManualFileResult(error, "");
                    return;
                }
                if (isCurrentOperation(operation, operation.generation)) afterSave.run();
            });
        } catch (Exception error) { reportManualFileResult(error, ""); }
    }

    private void installOperationAsync(RegistrationOperation operation, Runnable afterSave) throws Exception {
        if (!canInstallOperation(operation, null)) throw new IOException("Операция изменилась. Повторите сверку.");
        String key = operationPreferenceKey(operation.projectFingerprint, operation.recordId);
        Map<String, Object> previous = new HashMap<>(), changed = new HashMap<>();
        previous.put(key, bridgePrefs.getAll().get(key));changed.put(key, operation.toStored().toString());
        runFileWork("Сохранение перенесённой операции", () -> DurablePreferenceChange.commit(preferenceStore(bridgePrefs), previous, changed), error -> {
            if (error != null) { reportManualFileResult(error, ""); return; }
            operations.put(key, operation);
            afterSave.run();
        });
    }

    private interface ProjectReader { JSONObject read() throws Exception; }

    /** All file work uses one process-wide lane, surviving Activity recreation. */
    private void runFileWork(String title, ProjectTransfer.Change work,
                             java.util.function.Consumer<Exception> finished) throws IOException {
        if (!projectInstalling.compareAndSet(false, true)) throw new IOException("Дождитесь завершения работы с файлом.");
        try {
            invalidateProjectSync();
            projectSyncLoading = true;
            // Card writes keep the project identity. Let an in-flight panel read publish after the barrier.
            // Invalidating it here would leave the restored card permanently marked as loading.
            if (activeCard == null) {
                panelRequestGeneration++;
                panelCatalogueLoading = false;
                lastPanelRefresh = 0;
            }
            projectProgress = new AlertDialog.Builder(this).setTitle(title)
                    .setMessage("Пожалуйста, подождите…").setView(new ProgressBar(this)).setCancelable(false).show();
            projectStorage.execute(() -> {
                long storageStarted = System.nanoTime();
                Exception failure = null;
                try { work.run(); }
                catch (Exception error) {
                    failure = error;
                    if (error instanceof ProjectTransfer.InstallException)
                        projectStorageUncertain = !((ProjectTransfer.InstallException) error).rollbackSucceeded;
                }
                PerformanceMetrics.INSTANCE.record(PerformanceMetrics.Task.STORAGE, System.nanoTime() - storageStarted, 0, failure == null);
                Exception result = failure;
                new Handler(Looper.getMainLooper()).post(() -> {
                    projectInstalling.set(false);
                    if (destroyed) return;
                    projectSyncLoading = false;
                    projectStateUncertain = projectStorageUncertain;
                    if (projectProgress != null) { projectProgress.dismiss(); projectProgress = null; }
                    finished.accept(result);
                    if (foreground && screenReady && !projectInstalling.get()) {
                        resumeRegistrations();
                        scheduleProjectSync(ClientUxPolicy.projectDelay(projectUnchangedCount, projectFailureCount));
                    }
                });
            });
        } catch (Exception error) {
            projectInstalling.set(false);
            projectSyncLoading = false;
            if (projectProgress != null) { projectProgress.dismiss(); projectProgress = null; }
            throw new IOException("Не удалось начать работу с файлом.", error);
        }
    }

    private void restoreProjectPreferences(Map<String, Object> journal, Map<String, Object> bridge) throws Exception {
        restorePreferences(prefs, journal);
        restorePreferences(bridgePrefs, bridge);
    }

    private void flushProjectPreferences() throws IOException {
        // commit waits for earlier apply writes, including panel selections published with the model.
        if (!prefs.edit().commit() || !bridgePrefs.edit().commit())
            throw new IOException("Не удалось сохранить настройки проекта.");
    }

    private void requireIdleProject() throws IOException {
        if (activeCard != null || hasUnresolvedRegistrations() || !operationsInFlight.isEmpty())
            throw new IOException("Закройте карточку и дождитесь результата текущей регистрации.");
    }

    private void installDownloadedProject(ProjectTransfer.Snapshot snapshot,
                                          java.util.function.Consumer<Exception> finished) throws Exception {
        openProjectAsync(() -> snapshot.project, "загружен из Windows", false, finished);
    }

    private void openProjectAsync(ProjectReader reader, String source, boolean manual,
                                  java.util.function.Consumer<Exception> finished) throws Exception {
        requireIdleProject();
        JSONObject previous = projectRoot;
        boolean previousWasWaiting = waitingForProject;
        String previousFingerprint = projectFingerprint, previousSource = projectSourceName;
        Map<String, ?> oldJournal = new HashMap<>(prefs.getAll()), oldBridge = new HashMap<>(bridgePrefs.getAll());
        ExportSnapshot frozenJournal = captureExport();
        Map<String, Object> history = historicalJournalUpdates();
        ProjectViewState view = new ProjectViewState();
        final boolean[] changedProject = {false};
        runFileWork("Сохранение проекта", () -> {
            JSONObject incoming = reader.read();
            String fingerprint = ProjectTransfer.fingerprint(incoming);
            changedProject[0] = !previousFingerprint.equals(fingerprint);
            Map<String, Object> saved = new HashMap<>(oldJournal);
            saved.putAll(history);
            Map<String, Object> updates = new HashMap<>(history);
            updates.putAll(JournalMerge.carry(saved, previous, previousFingerprint, incoming, fingerprint));
            saved.putAll(updates);
            if (manual) updates.putAll(JournalMerge.importJournal(saved, incoming, fingerprint));
            JSONObject oldFields=Fieldwork.read((String)(oldJournal.containsKey(Fieldwork.key(previousFingerprint))?oldJournal.get(Fieldwork.key(previousFingerprint)):previous.optJSONObject("fieldwork")==null?"":previous.optJSONObject("fieldwork").toString()));
            JSONObject incomingFields=Fieldwork.read(incoming.optJSONObject("fieldwork")==null?"":incoming.optJSONObject("fieldwork").toString());
            JSONObject keptFields=Fieldwork.empty();
            Set<String> priorIds=ImportPreview.rows(previous).keySet();
            for(String id:ImportPreview.rows(incoming).keySet())if(priorIds.contains(id)){keptFields=oldFields;break;}
            String existingFields=(String)oldJournal.get(Fieldwork.key(fingerprint));
            if(existingFields!=null)keptFields=Fieldwork.read(existingFields);
            updates.put(Fieldwork.key(fingerprint),Fieldwork.merge(keptFields,incomingFields,incoming).toString());
            if (!previousWasWaiting) archiveProject(ProjectTransfer.archiveFile(getFilesDir(), previousFingerprint),
                    ProjectDocuments.portable(previous, frozenJournal.build()));
            ProjectTransaction.install(new File(getFilesDir(), PROJECT_FILE), incoming.toString(), oldJournal, oldBridge, () -> {
                applyJournalUpdates(updates);
                SharedPreferences.Editor bridge = bridgePrefs.edit().putString("project_source", source).remove("sync_revision");
                if (manual) bridge.putBoolean("auto_project", false);
                JSONArray panels = incoming.optJSONArray("panels");
                if ((panels != null && panels.length() > 0) || (incoming.optBoolean("panelCatalogComplete")
                        && fingerprint.equals(incoming.optString("panelMappingFingerprint"))))
                    bridge.remove("project-panels:" + fingerprint);
                if (!bridge.commit()) throw new IOException("Не удалось сохранить настройки проекта.");
                publishOnUi(() -> {
                    if (destroyed) throw new IOException("Экран закрыт; восстановлен предыдущий проект.");
                    loadProject(incoming, fingerprint);
                    projectSourceName = source;
                });
                flushProjectPreferences();
            }, (journal, bridge) -> {
                restoreProjectPreferences(journal, bridge);
                publishOnUi(() -> {
                    if (!destroyed) { loadProject(previous, previousFingerprint, previousWasWaiting); projectSourceName = previousSource; }
                });
                flushProjectPreferences();
            });
            projectStorageUncertain = false;
        }, error -> {
            if (!projectStateUncertain) view.restore(error == null && changedProject[0]);
            finished.accept(error);
        });
    }

    private final class ProjectViewState {
        final String query = searchBox == null ? "" : searchBox.getText().toString();
        final int stage = stageFilter == null ? 0 : stageFilter.getSelectedItemPosition();
        final String group = groupFilter == null ? "Все группы" : String.valueOf(groupFilter.getSelectedItem());
        final String deviceType = selectedTypeFilter();
        final int first = deviceList == null ? -1 : deviceList.getFirstVisiblePosition();
        final String anchor = first >= 0 && first < visible.size() ? visible.get(first).id : "";
        final int offset = deviceList != null && deviceList.getChildCount() > 0 ? deviceList.getChildAt(0).getTop() : 0;
        void restore(boolean newProject) {
            if (siteText == null) return;
            siteText.setText(siteName);
            searchBox.setText(newProject ? "" : query);
            stageFilter.setSelection(newProject ? 0 : stage);
            String[] groups = buildGroupLabels();
            groupFilter.setAdapter(filterAdapter(groups));
            if (!newProject) for (int i = 0; i < groups.length; i++) if (groups[i].equals(group)) groupFilter.setSelection(i);
            rebuildTypeFilter(newProject ? null : deviceType);
            if (newProject) panelFilterIds.clear();
            rebuildPanelFilter();
            stageCounts = null;
            applyFilters();
            pendingScrollAnchor = newProject ? "" : anchor;
            pendingScrollOffset = offset;
        }
    }

    private void archiveProject(File file, JSONObject document) throws Exception {
        ProjectTransfer.write(file, document.toString());
        SavedProjectCatalog.remember(file, document);
    }

    private void archiveCurrentProject() throws Exception {
        if (waitingForProject || projectRoot == null || projectFingerprint == null) return;
        archiveProject(ProjectTransfer.archiveFile(getFilesDir(), projectFingerprint), createPortableProject());
    }

    private Map<String, Object> prepareLocalJournal(JSONObject incoming, String incomingFingerprint, boolean manualImport)
            throws Exception {
        Map<String, Object> saved = new HashMap<>(prefs.getAll());
        Map<String, Object> historicalUpdates = historicalJournalUpdates();
        saved.putAll(historicalUpdates);
        Map<String, Object> updates = new HashMap<>(historicalUpdates);
        updates.putAll(JournalMerge.carry(saved, projectRoot, projectFingerprint, incoming, incomingFingerprint));
        saved.putAll(updates);
        if (manualImport) updates.putAll(JournalMerge.importJournal(saved, incoming, incomingFingerprint));
        return updates;
    }

    private Map<String, Object> historicalJournalUpdates() throws JSONException {
        Map<String, Object> historicalUpdates = new HashMap<>();
        for (Device device : devices) if (operationFor(device) != null || hasReviewedRegistrationHistory(device)) {
            JSONObject historical = new JSONObject();
            addRegistrationSummary(historical, device);
            if (historical.optJSONObject("registration") != null) {
                String key = key("registration_summary", device);
                String summary = historical.getJSONObject("registration").toString();
                historicalUpdates.put(key, summary);
            }
        }
        return historicalUpdates;
    }

    private void carryAndImportLocalJournal(JSONObject incoming, String incomingFingerprint, boolean manualImport)
            throws Exception {
        applyJournalUpdates(prepareLocalJournal(incoming, incomingFingerprint, manualImport));
    }

    private void applyJournalUpdates(Map<String, Object> updates) throws IOException {
        if (updates.isEmpty()) return;
        SharedPreferences.Editor changes = prefs.edit();
        for (Map.Entry<String, Object> entry : updates.entrySet()) {
            Object value = entry.getValue();
            if (value instanceof Integer) changes.putInt(entry.getKey(), (Integer) value);
            else if (value instanceof Long) changes.putLong(entry.getKey(), (Long) value);
            else if (value instanceof String) changes.putString(entry.getKey(), (String) value);
        }
        if (!changes.commit()) throw new IOException("Не удалось сохранить перенесённый журнал. Предыдущий проект сохранён в «Проекты на телефоне».");
    }

    private void restorePreferences(SharedPreferences target, Map<String, ?> snapshot) throws IOException {
        SharedPreferences.Editor restore = target.edit().clear();
        for (Map.Entry<String, ?> entry : snapshot.entrySet()) {
            Object value = entry.getValue();
            if (value instanceof String) restore.putString(entry.getKey(), (String) value);
            else if (value instanceof Integer) restore.putInt(entry.getKey(), (Integer) value);
            else if (value instanceof Long) restore.putLong(entry.getKey(), (Long) value);
            else if (value instanceof Boolean) restore.putBoolean(entry.getKey(), (Boolean) value);
            else if (value instanceof Float) restore.putFloat(entry.getKey(), (Float) value);
            else if (value instanceof Set<?>) {
                Set<String> strings = new HashSet<>();
                for (Object item : (Set<?>) value) strings.add((String) item);
                restore.putStringSet(entry.getKey(), strings);
            } else throw new IOException("Не удалось восстановить сохранённую настройку.");
        }
        if (!restore.commit()) throw new IOException("Не удалось восстановить журнал после ошибки сохранения.");
    }

    private String journalConflict(Device device) { return prefs.getString(key("journal_conflict", device), ""); }

    private JSONObject projectRecord(Device device) throws JSONException {
        JSONArray rows = projectRoot.getJSONArray("records");
        for (int i = 0; i < rows.length(); i++) {
            JSONObject row = rows.getJSONObject(i);
            if (device.id.equalsIgnoreCase(row.optString("id"))) return row;
        }
        throw new JSONException("Запись отсутствует в проекте.");
    }

    private void reviewJournalConflict(DeviceCard card) {
        if (activeCard != card || journalConflict(card.device).isEmpty()) return;
        try {
            JSONObject row = projectRecord(card.device);
            int windowsStage = (Integer) JournalMerge.base(row, "stage");
            String windowsSerial = String.valueOf(JournalMerge.base(row, "serial"));
            new AlertDialog.Builder(this).setTitle("Сверка журнала")
                    .setMessage("Телефон: " + STAGE_LABELS[getStage(card.device)] + "\nID: " + valueOrDash(getSerial(card.device))
                            + "\n\nWindows: " + STAGE_LABELS[windowsStage] + "\nID: " + valueOrDash(windowsSerial)
                            + "\n\nВыбор меняет только журнал телефона. Ключи и примечания берутся из выбранной версии; запись в оборудование не выполняется.")
                    .setPositiveButton("Оставить телефон", (dialog, which) -> resolveJournalConflict(card, false))
                    .setNeutralButton("Принять Windows", (dialog, which) -> resolveJournalConflict(card, true))
                    .setNegativeButton("Позже", null).show();
        } catch (Exception error) { toast("Не удалось сверить запись. " + bridgeError(error)); }
    }

    private void resolveJournalConflict(DeviceCard card, boolean useWindows) {
        if (activeCard != card || projectInstalling.get()) return;
        RegistrationOperation operation = operationFor(card.device);
        if (operation != null && operation.needsResolution()) return;
        try {
            File archive = ProjectTransfer.archiveFile(getFilesDir(), projectFingerprint);
            File revision = new File(archive.getParentFile(), projectFingerprint + "."
                    + System.currentTimeMillis() + ".s3project");
            JSONObject base = projectRoot;
            ExportSnapshot snapshot = captureExport();
            Map<String, Object> changes = new HashMap<>(), previous = new HashMap<>();
            if (useWindows) for (String field : JournalMerge.LOCAL_FIELDS) changes.put(key(field, card.device), null);
            String manual = preservedManualSummary(card.device);
            if (manual != null) changes.put(key("registration_summary", card.device), manual);
            changes.put(key("journal_conflict", card.device), null);
            changes.put(key("journal_acknowledged_base", card.device), JournalMerge.baseSignature(projectRecord(card.device)));
            Map<String, ?> saved = prefs.getAll();
            for (String key : changes.keySet()) previous.put(key, saved.get(key));
            runFileWork("Сохранение выбранной версии", () -> {
                archiveProject(revision, ProjectDocuments.portable(base, snapshot.build()));
                DurablePreferenceChange.commit(preferenceStore(prefs), previous, changes);
            }, error -> {
                if (error != null) {
                    if (error instanceof DurablePreferenceChange.Failure && !((DurablePreferenceChange.Failure) error).rollbackSucceeded)
                        projectStateUncertain = projectStorageUncertain = true;
                    reportManualFileResult(error, ""); return;
                }
                if (activeCard != card) return;
                card.stage.setSelection(getStage(card.device));
                card.serial.setText(getSerial(card.device));
                card.deviceKey.setText(getDeviceKey(card.device));
                card.note.setText(getNote(card.device));
                card.originalDescription = getDescription(card.device);
                card.description.setText(card.originalDescription);
                refreshRegistrationCard(card);
                refreshDevice(card.device);
            });
        } catch (Exception error) { toast("Выбор не сохранён. " + bridgeError(error)); }
    }

    private void reportManualFileResult(Exception error, String success) {
        if (error != null) {
            new AlertDialog.Builder(this).setTitle("Операция не выполнена").setMessage(bridgeError(error))
                    .setPositiveButton("Понятно", null).show();
        } else toast(success);
    }

    private void chooseSavedProject() {
        try {
            requireIdleProject();
            JSONObject base = projectRoot;
            ExportSnapshot snapshot = captureExport();
            String fingerprint = projectFingerprint;
            List<File> projects = new ArrayList<>();
            List<String> labels = new ArrayList<>();
            runFileWork("Проекты на телефоне", () -> {
                long archiveStarted = System.nanoTime();
                File archive = ProjectTransfer.archiveFile(getFilesDir(), fingerprint);
                String revision = ArchiveFreshness.revision(base, snapshot);
                boolean changed = ArchiveFreshness.saveIfChanged(archive, revision,
                        () -> archiveProject(archive, ProjectDocuments.portable(base, snapshot.build())));
                PerformanceMetrics.INSTANCE.record(PerformanceMetrics.Task.ARCHIVE, System.nanoTime() - archiveStarted,
                        changed ? snapshot.devices.size() : 0, true);
                for (SavedProjectCatalog.Entry entry : SavedProjectCatalog.list(new File(getFilesDir(), "projects"),
                        file -> parseProject(ProjectTransfer.read(file)))) {
                    projects.add(entry.file);
                    labels.add(entry.file == null ? entry.name + " · файл повреждён"
                            : entry.name + " · " + entry.count + " записей · " + entry.identity.substring(0, 8)
                            + " · " + new SimpleDateFormat("dd.MM.yyyy HH:mm", Locale.ROOT).format(new Date(entry.modified))
                            + (entry.file.getName().equals(fingerprint + ".s3project") ? " · открыт" : ""));
                }
            }, error -> {
                if (error != null) { reportManualFileResult(error, ""); return; }
                if (projects.isEmpty()) { toast("Сохранённых проектов пока нет."); return; }
                new AlertDialog.Builder(this).setTitle("Проекты на телефоне").setItems(labels.toArray(new String[0]), (dialog, which) -> {
                    File selected = projects.get(which);
                    if (selected == null) { toast("Повреждённую копию открыть не удалось; выберите другую."); return; }
                    try {
                        openProjectAsync(() -> parseProject(ProjectTransfer.read(selected)), "сохранён на телефоне", true,
                                result -> reportManualFileResult(result, "Проект открыт. Автозагрузка Windows приостановлена."));
                    } catch (Exception problem) { reportManualFileResult(problem, ""); }
                }).setNegativeButton("Закрыть", null).show();
            });
        } catch (Exception error) { reportManualFileResult(error, ""); }
    }

    private String[] buildGroupLabels() {
        Set<String> groups = new LinkedHashSet<>();
        groups.add("Все группы");
        for (Device d : devices) groups.add(d.group);
        return groups.toArray(new String[0]);
    }

    private String panelCataloguePreferenceKey() { return "project-panels:" + projectFingerprint; }

    private void installProjectCatalogue(JSONArray rows, JSONArray metadata, boolean useProjectPointers) throws JSONException {
        List<ProjectPanelIndex.Panel> panels = new ArrayList<>();
        if (rows != null) for (int i = 0; i < rows.length(); i++) {
            JSONObject row = rows.getJSONObject(i);
            List<String> records = new ArrayList<>();
            JSONArray members = row.optJSONArray("recordIds");
            if (members != null) for (int n = 0; n < members.length(); n++) records.add(members.getString(n));
            Map<String, String> routeMetadata = new HashMap<>();
            for (String field : ProjectPanelIndex.ROUTE_FIELDS)
                if (row.has(field)) routeMetadata.put(field, row.optString(field, ""));
            panels.add(new ProjectPanelIndex.Panel(row.optString("projectPanelId", row.optString("panelId", "")),
                    row.optString("name", row.optString("displayName", "")), row.optString("nativePanelId", ""),
                    row.optString("segmentId", ""), row.optString("host", ""), row.optInt("port", 6344),
                    row.optString("moduleId", ""), records, routeMetadata, row.optBoolean("viaPanelTerminal", false)));
        }
        Map<String, String> pointers = new HashMap<>();
        if (useProjectPointers) {
            JSONArray records = projectRoot.getJSONArray("records");
            for (int i = 0; i < records.length(); i++) {
                JSONObject record = records.getJSONObject(i);
                if (!record.optString("projectPanelId", "").isEmpty())
                    pointers.put(record.getString("id"), record.getString("projectPanelId"));
            }
        }
        ProjectPanelIndex replacement = new ProjectPanelIndex(panels, pointers);
        JSONArray safeRows = new JSONArray();
        for (ProjectPanelIndex.Panel panel : replacement.panels()) {
            Set<String> members = new LinkedHashSet<>(panel.recordIds);
            for (Device device : devices) if (replacement.forRecord(device.id).contains(panel))
                members.add(ProjectPanelIndex.normalized(device.id));
            JSONObject row = new JSONObject();
            row.put("panelId", panel.id).put("projectPanelId", panel.id).put("nativePanelId", panel.nativePanelId)
                    .put("segmentId", panel.segmentId).put("name", panel.name).put("host", panel.host)
                    .put("port", panel.port).put("moduleId", panel.moduleId).put("recordIds", new JSONArray(members))
                    .put("source", "project").put("viaPanelTerminal", panel.viaPanelTerminal);
            for (Map.Entry<String, String> field : panel.routeMetadata.entrySet()) row.put(field.getKey(), field.getValue());
            // A saved catalogue is useful for filtering; it is never live registration authorization.
            safeRows.put(row);
        }
        Map<String, String> descriptions = new HashMap<>();
        JSONArray safeMetadata = null;
        if (metadata != null) {
            for (int i = 0; i < metadata.length(); i++) {
                JSONObject row = metadata.getJSONObject(i);
                Device device = findById(row.optString("recordId"));
                if (device != null && row.has("description")) descriptions.put(device.id, row.optString("description", ""));
            }
            safeMetadata = new JSONArray();
            for (Device device : devices) safeMetadata.put(new JSONObject().put("recordId", device.id)
                    .put("description", descriptions.containsKey(device.id) ? descriptions.get(device.id) : device.description));
        }
        // Publish the replacement only after every panel and metadata row has been validated.
        panelIndex = replacement;
        projectPanels = safeRows;
        if (safeMetadata != null) projectRecordMetadata = safeMetadata;
        for (Device device : devices) if (descriptions.containsKey(device.id)) device.description = descriptions.get(device.id);
        invalidateSearchIndex();
    }

    private void cacheProjectCatalogue() throws JSONException {
        JSONObject cache = new JSONObject();
        cache.put("projectFingerprint", projectFingerprint).put("panels", projectPanels)
                .put("recordMetadata", projectRecordMetadata).put("catalogComplete", projectPanelCatalogueComplete)
                .put("updatedAt", System.currentTimeMillis());
        bridgePrefs.edit().putString(panelCataloguePreferenceKey(), cache.toString()).apply();
    }

    private boolean applyProjectCatalogueResponse(JSONObject response) throws JSONException, IOException {
        if (!response.optBoolean("ok", false))
            throw new IOException(response.optString("message", "Не удалось получить панели проекта"));
        if (response.optInt("panelMappingVersion", response.optInt("panelCatalogVersion", 0)) != 1)
            throw new IOException("Обновите Windows-мост: нужен каталог панелей с принадлежностью датчиков.");
        if (!projectFingerprint.equalsIgnoreCase(response.optString("projectFingerprint", "")))
            throw new IOException("В Windows открыт другой проект. Откройте одинаковый .s3project на телефоне и компьютере.");
        JSONArray catalogue = response.getJSONArray("projectPanels");
        boolean complete = response.optBoolean("catalogComplete", false);
        // An authenticated current project snapshot supersedes an older phone catalogue even without Master.
        // An unqualified empty fallback means the bridge has no map; it must not erase a usable phone map.
        boolean replace = isNativeCatalogue(response) || complete || catalogue.length() > 0;
        if (replace) {
            installProjectCatalogue(catalogue, response.optJSONArray("recordMetadata"), false);
            projectPanelCatalogueComplete = complete;
            cacheProjectCatalogue();
        }
        return replace;
    }

    private boolean isNativeCatalogue(JSONObject response) {
        return "native_project".equals(response.optString("catalogSource"))
                && response.optBoolean("nativeAvailable", true);
    }

    private String selectedTypeFilter() {
        int selected = typeFilter == null ? -1 : typeFilter.getSelectedItemPosition();
        return selected >= 0 && selected < typeFilterKeys.size() ? typeFilterKeys.get(selected) : null;
    }

    private void selectTypeFilter(String key) {
        typeFilter.setSelection(Math.max(0, typeFilterKeys.indexOf(key == null ? null : DeviceFilterIndex.typeKey(key))));
    }

    private void rebuildTypeFilter(String selected) {
        if (typeFilter == null) return;
        Map<String, String> types = new java.util.TreeMap<>();
        for (Device device : devices) {
            String key = DeviceFilterIndex.typeKey(device.type);
            if (!types.containsKey(key)) types.put(key, key.isEmpty() ? "Тип не указан" : device.type.trim());
        }
        List<String> labels = new ArrayList<>();
        typeFilterKeys.clear();
        typeFilterKeys.add(null); labels.add("Все типы");
        for (Map.Entry<String, String> entry : types.entrySet()) {
            typeFilterKeys.add(entry.getKey()); labels.add(entry.getValue());
        }
        typeFilter.setAdapter(filterAdapter(labels.toArray(new String[0])));
        selectTypeFilter(selected);
    }

    private String selectedPanelFilter() {
        int selected = panelFilter == null ? -1 : panelFilter.getSelectedItemPosition();
        return selected >= 0 && selected < panelFilterIds.size() ? panelFilterIds.get(selected) : "";
    }

    private void rebuildPanelFilter() {
        if (panelFilter == null) return;
        String selected = panelFilterIds.isEmpty()
                ? bridgePrefs.getString("panel-filter:" + projectFingerprint, "") : selectedPanelFilter();
        List<String> labels = new ArrayList<>();
        panelFilterIds.clear();
        panelFilterIds.add(""); labels.add("Все панели");
        for (ProjectPanelIndex.Panel panel : panelIndex.panels()) {
            panelFilterIds.add(panel.id); labels.add(panel.displayName());
        }
        panelFilterIds.add("?"); labels.add("Панель не определена");
        panelFilter.setAdapter(filterAdapter(labels.toArray(new String[0])));
        int selectedPosition = panelFilterIds.indexOf(selected);
        panelFilter.setSelection(Math.max(0, selectedPosition));
    }

    private String projectPanelLabel(Device device) {
        ProjectPanelIndex.Panel panel = panelIndex.uniqueForRecord(device.id);
        if (panel != null) return panel.displayName();
        return panelIndex.forRecord(device.id).isEmpty() ? "Панель не определена" : "Несколько панелей в проекте";
    }

    private DeviceFilterIndex.Row filterRow(Device d) {
        ProjectPanelIndex.Panel panel = panelIndex.uniqueForRecord(d.id);
        return new DeviceFilterIndex.Row(d.id, d.group, panel == null ? "?" : panel.id, getStage(d), searchText(d), d.type);
    }

    private void applyFilters() {
        if (deviceAdapter == null || destroyed) return;
        if (filterIndex == null) {
            ExportSnapshot snapshot = captureExport();
            long building = ++filterGeneration;
            filterPending = true;
            filterExecutor.execute(() -> {
                if (destroyed || building != filterGeneration) return;
                ExportSnapshot.PreparedList prepared = snapshot.prepareList();
                mainHandler.post(() -> {
                    if (destroyed || building != filterGeneration || !snapshot.projectFingerprint.equals(projectFingerprint)) return;
                    filterIndex = prepared.index;
                    stageCounts = prepared.counts;
                    applyFilters();
                });
            });
            return;
        }
        DeviceFilterIndex index = filterIndex;
        String query = searchBox.getText().toString();
        int stage = stageFilter.getSelectedItemPosition();
        String selectedGroup = String.valueOf(groupFilter.getSelectedItem());
        String group = "Все группы".equals(selectedGroup) ? null : selectedGroup;
        String panel = selectedPanelFilter(), fingerprint = projectFingerprint;
        String deviceType = selectedTypeFilter();
        long generation = ++filterGeneration;
        long searchStarted = System.nanoTime();
        filterPending = true;
        filterExecutor.execute(() -> {
            List<String> matches = index.query(query, stage, group, panel, deviceType,
                    () -> destroyed || generation != filterGeneration);
            PerformanceMetrics.INSTANCE.record(PerformanceMetrics.Task.SEARCH, System.nanoTime() - searchStarted,
                    matches == null ? 0 : matches.size(), matches != null);
            if (matches == null) return;
            mainHandler.post(() -> {
                if (destroyed || generation != filterGeneration || !fingerprint.equals(projectFingerprint)) return;
                filterPending = false;
                visible.clear();
                for (String id : matches) { Device d = findById(id); if (d != null) visible.add(d); }
                deviceAdapter.notifyDataSetChanged();
                emptyText.setText(waitingForProject
                        ? "Проект не загружен.\nНажмите, чтобы загрузить его с Windows-моста."
                        : "По выбранным условиям ничего не найдено");
                emptyText.setVisibility(visible.isEmpty() ? View.VISIBLE : View.GONE);
                updateStats();
                restoreScrollAnchor();
            });
        });
    }

    private void restoreScrollAnchor() {
        if (pendingScrollAnchor.isEmpty()) return;
        for (int i = 0; i < visible.size(); i++) if (pendingScrollAnchor.equalsIgnoreCase(visible.get(i).id)) {
            deviceList.setSelectionFromTop(i, pendingScrollOffset); break;
        }
        pendingScrollAnchor = "";
    }

    private String searchText(Device d) {
        if (d.searchIndex == null || d.searchVersion != searchVersion) {
            d.searchVersion = searchVersion;
            d.searchIndex = (d.name + " " + getDescription(d) + " " + d.zone
                    + " " + d.type + " " + projectPanelLabel(d) + " " + getSerial(d) + " " + getDeviceKey(d)
                    + " " + getNote(d)).toLowerCase(Locale.ROOT);
        }
        return d.searchIndex;
    }

    private void invalidateSearchIndex() {
        searchVersion++;
        filterIndex = null;
        filterGeneration++;
    }

    private void localPreferenceChanged(String key) {
        if (key == null) { stageCounts = null; invalidateSearchIndex(); return; }
        String[] parts = key.split(":", 3);
        if (parts.length != 3 || !parts[1].equals(projectFingerprint)) return;
        Device device = findById(parts[2]);
        if (device == null) return;
        device.searchIndex = null;
        if (stageCounts != null && "stage".equals(parts[0])) stageCounts.set(device.id, getStage(device));
        if (filterIndex != null) { filterIndex.put(filterRow(device)); if (filterPending) applyFilters(); }
        else if (filterPending && deviceAdapter != null) applyFilters();
    }

    private void refreshDevice(Device device) {
        device.searchIndex = null;
        if (filterIndex != null) filterIndex.put(filterRow(device));
        // Invalidate an older worker result; a new query follows the immediate row update.
        filterGeneration++;
        if (stageCounts != null) stageCounts.set(device.id, getStage(device));
        if (deviceAdapter == null) return;
        int position = visible.indexOf(device);
        boolean matches = matchesCurrentFilters(device);
        if (position >= 0 && matches) {
            int child = position - deviceList.getFirstVisiblePosition();
            if (child >= 0 && child < deviceList.getChildCount())
                deviceAdapter.bind(deviceList.getChildAt(child), device);
        } else if (position >= 0 || matches) {
            if (position >= 0) visible.remove(position);
            else {
                int insert = 0;
                while (insert < visible.size() && visible.get(insert).listOrder < device.listOrder) insert++;
                visible.add(insert, device);
            }
            // A structural change needs a dataset notification, a field edit does not.
            deviceAdapter.notifyDataSetChanged();
        }
        emptyText.setVisibility(visible.isEmpty() ? View.VISIBLE : View.GONE);
        updateStats();
        if (filterPending) applyFilters();
    }

    private boolean matchesCurrentFilters(Device device) {
        int stage = getStage(device), selected = stageFilter.getSelectedItemPosition();
        String group = String.valueOf(groupFilter.getSelectedItem());
        String query = searchBox.getText().toString().trim().toLowerCase(Locale.ROOT);
        return (selected == 0 || (selected == 1 && stage < 2) || (selected == 2 && stage == 2) || (selected == 3 && stage == 3))
                && ("Все группы".equals(group) || group.equals(device.group))
                && (selectedTypeFilter() == null || selectedTypeFilter().equals(DeviceFilterIndex.typeKey(device.type)))
                && panelIndex.matchesFilter(device.id, selectedPanelFilter()) && (query.isEmpty() || searchText(device).contains(query));
    }

    private void updateStats() {
        if (stageCounts == null || stageCounts.total() != devices.size()) {
            stageCounts = new StageCounts();
            for (Device device : devices) stageCounts.set(device.id, getStage(device));
        }
        int found = stageCounts.found(), initialized = stageCounts.initialized(), checked = stageCounts.checked();
        int total = devices.size();
        statsSummary = "Показано " + visible.size() + " из " + total
                + "   •   найдено " + found
                + "   •   инициализировано " + initialized
                + "   •   проверено " + checked;
        statsText.setText(visible.size() + "/" + total + "  •  Иниц. " + initialized + "  •  Проверено " + checked);
        statsText.setContentDescription(statsSummary);
        checkedProgress = total == 0 ? 0 : (int) Math.round(1000.0 * checked / total);
        if (menuStatsText != null) menuStatsText.setText(statsSummary);
        if (progressBar != null) progressBar.setProgress(checkedProgress);
    }

    private void editDevice(Device device) { editDevice(device, false); }

    private void editDevice(Device device, boolean scanImmediately) {
        DeviceCard card = new DeviceCard(device);
        activeCard = card;
        LinearLayout form = new LinearLayout(this);
        form.setOrientation(LinearLayout.VERTICAL);
        form.setPadding(dp(20), dp(4), dp(20), 0);
        form.setFocusableInTouchMode(true);

        TextView details = text(
                getDisplayName(device) + "\nЗона: " + valueOrDash(device.zone)
                        + "\nГруппа: " + device.group
                        + (device.version.isEmpty() ? "" : "   •   " + device.version),
                14, color("text_secondary"), false);
        details.setPadding(0, 0, 0, dp(14));
        form.addView(details, matchWrap());

        form.addView(fieldLabel("Панель по проекту"), matchWrap());
        card.panelSpinner = new Spinner(this);
        card.panelSpinner.setAdapter(filterAdapter(new String[]{projectPanelLabel(device)}));
        form.addView(card.panelSpinner, matchWrap());
        card.refreshPanels = new Button(this);
        card.refreshPanels.setText("Обновить панель из проекта Windows");
        card.refreshPanels.setAllCaps(false);
        form.addView(card.refreshPanels, matchWrap());
        card.panelHint = text(bridgeConfigured() ? "Уточняем панель выбранного датчика в проекте Windows."
                        : "Панель показана по сохранённому проекту. Настройте Windows-мост для проверки связи.",
                12, color("text_secondary"), false);
        form.addView(card.panelHint, matchWrap());

        form.addView(fieldLabel("Описание"), matchWrap());
        card.description = new EditText(this);
        card.description.setMinLines(1);
        card.description.setMaxLines(4);
        card.description.setHint("Описание из проекта");
        card.originalDescription = getDescription(device);
        card.description.setText(card.originalDescription);
        form.addView(card.description, matchWrap());
        Button printDescription = new Button(this);
        printDescription.setText("Печать описания");
        printDescription.setOnClickListener(v -> {
            String label = card.description.getText().toString().trim();
            if (label.length() > 2000) { toast("Для этикетки нужно описание до 2000 символов."); return; }
            Intent printIntent = new Intent(this, LabelPrintActivity.class);
            printIntent.putExtra("description", label);
            startActivity(printIntent);
        });
        form.addView(printDescription, matchWrap());
        Button openDevicePlan = new Button(this);
        openDevicePlan.setText("Открыть план");
        openDevicePlan.setAllCaps(false);
        openDevicePlan.setOnClickListener(v -> showDevicePlan(device));
        form.addView(openDevicePlan, matchWrap());



        form.addView(fieldLabel("Этап работы"), matchWrap());
        Spinner stage = new Spinner(this);
        stage.setAdapter(simpleAdapter(STAGE_LABELS));
        stage.setSelection(getStage(device));
        card.stage = stage;
        form.addView(stage, matchWrap());

        form.addView(fieldLabel("Серийный номер"), matchWrap());
        EditText serial = new EditText(this);
        serial.setSingleLine(true);
        serial.setHint("12 шестнадцатеричных символов с наклейки");
        serial.setText(getSerial(device));
        serial.setSelectAllOnFocus(false);
        form.addView(serial, matchWrap());
        card.serial = serial;

        form.addView(fieldLabel("Ключ устройства"), matchWrap());
        EditText deviceKey = new EditText(this);
        deviceKey.setSingleLine(true);
        deviceKey.setHint("Например: A1B2C3D4");
        deviceKey.setText(getDeviceKey(device));
        deviceKey.setSelectAllOnFocus(false);
        form.addView(deviceKey, matchWrap());
        card.deviceKey = deviceKey;

        Button scanQr = new Button(this);
        scanQr.setText("Сканировать QR-код");
        scanQr.setAllCaps(false);
        form.addView(scanQr, matchWrap());
        card.scanQr = scanQr;

        form.addView(fieldLabel("Примечание"), matchWrap());
        EditText note = new EditText(this);
        note.setMinLines(2);
        note.setMaxLines(4);
        note.setHint("Место установки, результат проверки");
        note.setText(getNote(device));
        form.addView(note, matchWrap());
        card.note = note;

        Button queueInitialization = new Button(this);
        queueInitialization.setText("Начать регистрацию на панели");
        queueInitialization.setAllCaps(false);
        queueInitialization.setEnabled(false);
        card.begin = queueInitialization;
        LinearLayout.LayoutParams queueLp = matchWrap();
        queueLp.topMargin = dp(14);
        form.addView(queueInitialization, queueLp);

        card.status = text("Начните регистрацию; после состояния «Ожидание датчика» нажмите ПРОГ на датчике.",
                13, color("text_secondary"), false);
        form.addView(card.status, matchWrap());
        card.timing = text("", 12, color("text_secondary"), false);
        form.addView(card.timing, matchWrap());
        card.next = menuButton("Следующее устройство · сканировать");
        card.next.setVisibility(View.GONE);
        form.addView(card.next, matchWrap());
        card.next.setOnClickListener(v -> advanceToNextDevice(card));
        card.cancel = new Button(this);
        card.cancel.setText("Отменить регистрацию");
        card.cancel.setAllCaps(false);
        card.cancel.setVisibility(View.GONE);
        form.addView(card.cancel, matchWrap());
        card.checkStatus = new Button(this);
        card.checkStatus.setText("Проверить результат");
        card.checkStatus.setAllCaps(false);
        card.checkStatus.setVisibility(View.GONE);
        form.addView(card.checkStatus, matchWrap());
        card.recover = new Button(this);
        card.recover.setText("Сверить незавершённую операцию");
        card.recover.setAllCaps(false);
        card.recover.setVisibility(View.GONE);
        form.addView(card.recover, matchWrap());

        card.journalHint = text(journalConflict(device), 13, color("warning"), true);
        form.addView(card.journalHint, matchWrap());
        card.reviewJournal = menuButton("Сверить журнал с Windows");
        card.reviewJournal.setOnClickListener(v -> reviewJournalConflict(card));
        form.addView(card.reviewJournal, matchWrap());

        TextView source = text(
                "Исходный проект: " + (device.sourceInitialized ? "инициализировано" : "не инициализировано")
                        + "  •  строка " + device.sourceRow,
                12, color("text_secondary"), false);
        source.setPadding(0, dp(12), 0, 0);
        form.addView(source, matchWrap());

        ScrollView scroll = new ScrollView(this);
        scroll.addView(form);
        form.requestFocus();
        AlertDialog dialog = new AlertDialog.Builder(this)
                .setTitle(device.type.isEmpty() ? getDisplayName(device) : device.type)
                .setView(scroll)
                .setNegativeButton("Закрыть", null)
                .setPositiveButton("Сохранить", null)
                .create();
        card.dialog = dialog;
        dialog.setOnDismissListener(ignored -> {
            if (card.clock != null) mainHandler.removeCallbacks(card.clock);
            if (activeCard == card) {
                activeCard = null;
                if (!destroyed) saveScreenState();
                scheduleProjectSync(0);
            }
        });
        scanQr.setOnClickListener(v -> {
            scanTargetDevice = device;
            scanSerialField = serial;
            scanKeyField = deviceKey;
            startActivityForResult(new Intent(this, ScannerActivity.class), SCAN_QR);
        });
        dialog.setOnShowListener(ignored -> dialog.getButton(AlertDialog.BUTTON_POSITIVE)
                .setOnClickListener(v -> {
                    String normalizedSerial = serial.getText().toString().trim().toUpperCase(Locale.ROOT);
                    String normalizedKey = deviceKey.getText().toString().trim().toUpperCase(Locale.ROOT);
                    Device duplicate = findBySerial(normalizedSerial, device.id);
                    if (duplicate != null) {
                        serial.setError("Этот номер уже указан у «" + duplicate.name + "»");
                        serial.requestFocus();
                        return;
                    }
                    if (!normalizedSerial.isEmpty() && !normalizedSerial.matches("[0-9A-Z._/-]{4,40}")) {
                        serial.setError("Проверьте номер: допустимы буквы, цифры, точка, дефис и / ");
                        serial.requestFocus();
                        return;
                    }
                    if (!normalizedKey.isEmpty() && !normalizedKey.matches("[0-9A-F]{8,32}")) {
                        deviceKey.setError("Ключ должен содержать от 8 до 32 шестнадцатеричных символов");
                        deviceKey.requestFocus();
                        return;
                    }
                    int selectedStage = stage.getSelectedItemPosition();
                    if (selectedStage >= 2 && getStage(device) < 2) {
                        toast("Этап «Инициализировано» появится после подтверждённой регистрации на панели.");
                        stage.setSelection(getStage(device));
                        return;
                    }
                    RegistrationOperation previousOperation = operationFor(device);
                    if (previousOperation != null && previousOperation.registrationConfirmed
                            && !previousOperation.expectedHardwareId.equalsIgnoreCase(normalizedSerial)) {
                        selectedStage = Math.min(selectedStage, 1);
                    }
                    SharedPreferences.Editor changes = prefs.edit()
                            .putInt(key("stage", device), selectedStage)
                            .putString(key("serial", device), normalizedSerial)
                            .putString(key("device_key", device), normalizedKey)
                            .putString(key("note", device), note.getText().toString().trim())
                            .putLong(key("updated", device), System.currentTimeMillis());
                    saveDescriptionIfChanged(changes, card);
                    changes.apply();
                    hideKeyboard(serial);
                    dialog.dismiss();
                    refreshDevice(device);
                    Toast.makeText(this, "Запись сохранена", Toast.LENGTH_SHORT).show();
                    Runnable afterSave = card.afterSave;
                    card.afterSave = null;
                    if (afterSave != null) afterSave.run();
                }));
        dialog.show();
        queueInitialization.setOnClickListener(v -> beginRegistrationFromCard(card));
        card.refreshPanels.setOnClickListener(v -> requestPanelList(card));
        card.cancel.setOnClickListener(v -> {
            RegistrationOperation operation = operationFor(device);
            if (operation != null) cancelRegistration(operation);
        });
        card.checkStatus.setOnClickListener(v -> {
            RegistrationOperation operation = operationFor(device);
            if (operation != null) ensureLocalNetworkPermission(() -> pollRegistration(operation));
        });
        card.recover.setOnClickListener(v -> {
            RegistrationOperation operation = operationFor(device);
            if (operation != null) recoverRegistration(card, operation);
            else prepareImportedRegistrationRecovery(card);
        });
        card.panelSpinner.setOnItemSelectedListener(new AdapterView.OnItemSelectedListener() {
            public void onItemSelected(AdapterView<?> parent, View view, int position, long id) {
                PanelProfile selected = selectedPanel(card);
                if (selected != null && !card.loadingPanels) {
                    bridgePrefs.edit().putString(panelPreferenceKey(device), selected.panelId).apply();
                }
                refreshRegistrationCard(card);
            }
            public void onNothingSelected(AdapterView<?> parent) { }
        });
        refreshRegistrationCard(card);
        if (bridgeConfigured()) requestPanelList(card);
        scheduleCardClock(card);
        if (scanImmediately) mainHandler.post(() -> {
            if (!destroyed && activeCard == card && card.scanQr.isEnabled()) card.scanQr.performClick();
        });
    }

    private boolean mayAdvance(DeviceCard card) {
        RegistrationOperation operation = operationFor(card.device);
        return operation != null && !operation.localResolved
                && operation.expectedHardwareId.equalsIgnoreCase(card.serial.getText().toString().trim())
                && ClientUxPolicy.canAdvance(operation.registrationConfirmed, operation.projectSaved,
                operation.terminal, operation.needsResolution(), operationsInFlight.contains(operation.operationId));
    }

    private Device nextDevice(DeviceCard card) {
        ProjectPanelIndex.Panel panel = panelIndex.uniqueForRecord(card.device.id);
        if (panel == null) return null;
        Device first = null;
        for (Device candidate : visible) {
            if (candidate == card.device || getStage(candidate) >= 2 || !journalConflict(candidate).isEmpty()) continue;
            ProjectPanelIndex.Panel target = panelIndex.uniqueForRecord(candidate.id);
            if (target == null || !panel.id.equals(target.id)) continue;
            RegistrationOperation op = operationFor(candidate);
            if (op != null && (op.needsResolution() || op.registrationConfirmed)) continue;
            if (importedRegistrationNeedsResolution(candidate) || manualRegistrationBlocksStart(candidate)) continue;
            if (op == null && !bridgePrefs.getString(operationPreferenceKey(projectFingerprint, candidate.id), "").isEmpty()) continue;
            if (first == null) first = candidate;
            if (candidate.sourceRow > card.device.sourceRow) return candidate;
        }
        return first;
    }

    private void advanceToNextDevice(DeviceCard card) {
        if (activeCard != card || !mayAdvance(card)) return;
        Device next = nextDevice(card);
        if (next == null) { toast("В текущем фильтре больше нет устройств этой панели для регистрации."); return; }
        card.afterSave = () -> { if (!destroyed) editDevice(next, true); };
        card.dialog.getButton(AlertDialog.BUTTON_POSITIVE).performClick();
        card.afterSave = null; // A validation failure must not leave a delayed navigation armed.
    }

    private void scheduleCardClock(DeviceCard card) {
        if (card.clock != null) mainHandler.removeCallbacks(card.clock);
        card.clock = () -> {
            if (destroyed || !foreground || activeCard != card) return;
            updateCardTiming(card);
            mainHandler.postDelayed(card.clock, 1000);
        };
        if (foreground) card.clock.run();
    }

    private void updateCardTiming(DeviceCard card) {
        RegistrationOperation operation = operationFor(card.device);
        if (operation == null) { card.timing.setText(""); return; }
        long now = System.currentTimeMillis();
        String contact = operation.lastReplyAt == 0 ? "Ответ Windows ещё не получен"
                : "Последний ответ Windows: " + ClientUxPolicy.age(now - operation.lastReplyAt) + " назад";
        boolean active = operation.needsResolution();
        String timing = active && operation.startedAt > 0
                ? "Ожидание: " + ClientUxPolicy.age(now - operation.startedAt) + " · " : "";
        boolean stale = operation.lastReplyAt > 0 && now - operation.lastReplyAt > 15000;
        card.timing.setText(timing + contact + (active && stale ? "\nСвежего статуса нет. Проверяем ту же операцию." : ""));
    }

    private Device findBySerial(String serial, String exceptId) {
        if (serial.isEmpty()) return null;
        for (Device d : devices) {
            if (!d.id.equals(exceptId) && serial.equalsIgnoreCase(getSerial(d))) return d;
        }
        return null;
    }

    private void chooseBackupDestination() {
        Intent intent = new Intent(Intent.ACTION_CREATE_DOCUMENT);
        intent.addCategory(Intent.CATEGORY_OPENABLE);
        intent.setType("application/json");
        intent.putExtra(Intent.EXTRA_TITLE, fileStamp("streletz-progress", ".json"));
        startActivityForResult(intent, EXPORT_BACKUP);
    }

    private void checkCloudConnection(Button button, TextView connectionText) {
        button.setEnabled(false);
        connectionText.setText("Облако Стрелец · проверка…");
        new Thread(() -> {
            boolean connected = false;
            try (Socket socket = new Socket()) {
                socket.connect(new InetSocketAddress("proxy.streletz.ru", 9010), 5000);
                connected = true;
            } catch (IOException ignored) { }
            boolean result = connected;
            onUiWhenStorageReady(() -> {
                button.setEnabled(true);
                if (result) {
                    connectionText.setText("Облако Стрелец · доступно");
                    connectionText.setTextColor(color("success"));
                    toast("Облачный сервер доступен. Команды панели не отправлялись.");
                } else {
                    connectionText.setText("Облако Стрелец · нет связи");
                    connectionText.setTextColor(color("accent"));
                    toast("Проверьте интернет на телефоне и повторите попытку.");
                }
            });
        }, "cloud-connection-check").start();
    }

    private boolean bridgeConfigured() {
        String code = bridgePrefs.getString("pairing_code", "").trim()
                .replace("-", "").toUpperCase(Locale.ROOT);
        return !bridgePrefs.getString("host", "").trim().isEmpty()
                && code.matches("[0-9A-F]{16}");
    }

    private void ensureLocalNetworkPermission(Runnable action) {
        if (httpsConfigured()) { action.run(); return; }
        if (Build.VERSION.SDK_INT >= 37
                && checkSelfPermission(Manifest.permission.ACCESS_LOCAL_NETWORK) != PackageManager.PERMISSION_GRANTED) {
            pendingNetworkAction = action;
            requestPermissions(new String[]{Manifest.permission.ACCESS_LOCAL_NETWORK}, LOCAL_NETWORK_PERMISSION);
            return;
        }
        action.run();
    }

    @Override
    public void onRequestPermissionsResult(int requestCode, String[] permissions, int[] grantResults) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != LOCAL_NETWORK_PERMISSION) return;
        Runnable action = pendingNetworkAction;
        pendingNetworkAction = null;
        if (grantResults.length > 0 && grantResults[0] == PackageManager.PERMISSION_GRANTED) {
            if (action != null) action.run();
        } else {
            new AlertDialog.Builder(this)
                    .setTitle("Нет доступа к локальной сети")
                    .setMessage("Разрешите доступ к локальной сети, чтобы передавать задания в Windows-мост.")
                    .setPositiveButton("Понятно", null)
                    .show();
        }
    }

    private void updateBridgeText() {
        if (menuCatalogueText != null) menuCatalogueText.setText(catalogueStatusDetails);
        if (bridgeText == null) return;
        String host = bridgePrefs.getString("host", "").trim();
        int port = bridgePrefs.getInt("port", DEFAULT_BRIDGE_PORT);
        bridgeText.setText(host.isEmpty()
                ? "Мост · не настроен"
                : catalogueStatusShort.isEmpty() ? "Мост · " + BridgeEndpoint.display(host, port) : catalogueStatusShort);
        bridgeText.setContentDescription("Windows-мост: " + (host.isEmpty()
                ? "не настроен" : BridgeEndpoint.display(host, port)) + ". " + catalogueStatusDetails + " Нажмите для подробностей.");
    }

    private void setCatalogueStatus(String shortText, String details) {
        catalogueStatusShort = shortText;
        catalogueStatusDetails = details;
        updateBridgeText();
    }

    private void showCatalogueStatus() {
        if (!bridgeConfigured()) { showBridgeSettings(null); return; }
        String endpoint = BridgeEndpoint.display(bridgePrefs.getString("host", ""), bridgePrefs.getInt("port", DEFAULT_BRIDGE_PORT));
        new AlertDialog.Builder(this).setTitle("Проект и связь")
                .setMessage(catalogueStatusDetails + "\n\nWindows-мост: " + endpoint)
                .setPositiveButton("Загрузить проект", (d, which) -> refreshProjectFromWindows(true))
                .setNeutralButton("Подключение", (d, which) -> showBridgeSettings(null))
                .setNegativeButton("Закрыть", null).show();
    }

    private String catalogueResponseDetails(JSONObject response, boolean replaced) {
        String message = response.optString("catalogMessage", "").trim();
        if (message.isEmpty()) message = response.optString("message", "").trim();
        if (message.isEmpty()) message = "Каталог панелей получен из проекта Windows.";
        String nativeReason = response.optString("nativeReason", "").trim();
        if (!isNativeCatalogue(response) && !nativeReason.isEmpty() && !message.contains(nativeReason))
            message += "\nРегистрация недоступна: " + nativeReason;
        if (!replaced) message = (panelIndex.panels().isEmpty() ? "Карта панелей проекта пока не получена.\n"
                : "Windows пока не передал карту панелей. На телефоне оставлен ранее сохранённый каталог.\n") + message;
        else if (projectCatalogueMissing(response)) message = "Карта панелей проекта пока не получена.\n" + message;
        return message;
    }

    private boolean projectCatalogueMissing(JSONObject response) {
        JSONArray panels = response.optJSONArray("projectPanels");
        return !isNativeCatalogue(response) && !response.optBoolean("catalogComplete", false)
                && panels != null && panels.length() == 0;
    }

    private void reportCatalogueResponse(JSONObject response, boolean replaced) {
        String source = !replaced ? "прошлый каталог" : isNativeCatalogue(response) ? "из Master" : "из проекта";
        setCatalogueStatus(projectCatalogueMissing(response) && panelIndex.panels().isEmpty()
                        ? "Панели · карта не получена" : "Панелей: " + panelIndex.panels().size() + " · " + source,
                catalogueResponseDetails(response, replaced));
    }

    private void reportCatalogueFailure(JSONObject response, Exception error) {
        String code = response == null ? "" : response.optString("errorCode", "");
        String shortText = "project_missing".equals(code) ? "Панели · откройте проект"
                : "project_mismatch".equals(code) ? "Панели · другой проект"
                : error instanceof java.net.ConnectException ? "Мост · нет связи"
                : error instanceof java.net.SocketTimeoutException ? "Мост · нет ответа"
                : "Панели · ошибка загрузки";
        setCatalogueStatus(shortText, "Панели не обновлены. " + bridgeError(error)
                + (panelIndex.panels().isEmpty() ? "" : "\nРанее сохранённый каталог доступен для просмотра."));
    }

    private void showBridgeSettings(Runnable afterSave) {
        LinearLayout form = new LinearLayout(this);
        form.setOrientation(LinearLayout.VERTICAL);
        form.setPadding(dp(22), dp(4), dp(22), 0);
        TextView hint = text("Укажите IP/домен для прямого TCP-подключения или полный HTTPS-адрес KeenDNS. Для HTTPS откройте «KeenDNS» в Windows-мосте 0.5.17 и включите встроенный HTTP-доступ. Если указан только https://домен, используется путь /streletz-bridge/. Проект загрузится автоматически.",
                13, color("text_secondary"), false);
        form.addView(hint, matchWrap());
        form.addView(fieldLabel("IP, домен или HTTPS-адрес KeenDNS"), matchWrap());
        EditText host = new EditText(this);
        host.setSingleLine(true);
        host.setHint("192.168.0.10 или https://мост.keenetic.pro");
        host.setInputType(android.text.InputType.TYPE_CLASS_TEXT | android.text.InputType.TYPE_TEXT_VARIATION_URI);
        host.setText(bridgePrefs.getString("host", ""));
        form.addView(host, matchWrap());
        form.addView(fieldLabel("TCP-порт (для HTTPS берётся из адреса)"), matchWrap());
        EditText port = new EditText(this);
        port.setSingleLine(true);
        port.setInputType(android.text.InputType.TYPE_CLASS_NUMBER);
        port.setText(String.valueOf(bridgePrefs.getInt("port", DEFAULT_BRIDGE_PORT)));
        form.addView(port, matchWrap());
        form.addView(fieldLabel("Код подключения"), matchWrap());
        EditText code = new EditText(this);
        code.setSingleLine(true);
        code.setHint("16 символов из Windows-моста");
        code.setText(bridgePrefs.getString("pairing_code", ""));
        form.addView(code, matchWrap());

        ScrollView scroll = new ScrollView(this);
        scroll.addView(form);
        AlertDialog settings = new AlertDialog.Builder(this)
                .setTitle("Связь с Windows-мостом")
                .setView(scroll)
                .setNegativeButton("Отмена", null)
                .setPositiveButton("Сохранить", null)
                .create();
        settings.setOnShowListener(ignored -> settings.getButton(AlertDialog.BUTTON_POSITIVE)
                .setOnClickListener(v -> {
                    String hostValue = host.getText().toString().trim();
                    String codeValue = code.getText().toString().trim().replace("-", "").toUpperCase(Locale.ROOT);
                    int portValue;
                    try { portValue = Integer.parseInt(port.getText().toString().trim()); }
                    catch (NumberFormatException error) { portValue = -1; }
                    try {
                        BridgeEndpoint endpoint = BridgeEndpoint.parse(hostValue, portValue);
                        hostValue = endpoint.address;
                        portValue = endpoint.port;
                    } catch (IllegalArgumentException error) {
                        host.setError(error.getMessage());
                        return;
                    }
                    if (!codeValue.matches("[0-9A-F]{16}")) {
                        code.setError("Введите 16-значный код из Windows-моста");
                        return;
                    }
                    bridgePrefs.edit()
                            .putString("host", hostValue)
                            .putInt("port", portValue)
                            .putString("pairing_code", codeValue)
                            .apply();
                    bridgePrefs.edit().putBoolean("auto_project", true).apply();
                    invalidateProjectSync();
                    projectSyncRetryAfter = 0;
                    setCatalogueStatus("Проект · подключение…", "Настройки Windows-моста сохранены. Автоматическая загрузка проекта…");
                    panelRequestGeneration++;
                    panelCatalogueLoading = false;
                    lastPanelRefresh = 0;
                    if (activeCard != null) {
                        activeCard.loadingPanels = false;
                        activeCard.config = null;
                    }
                    hideKeyboard(host);
                    settings.dismiss();
                    scheduleProjectSync(0);
                    if (afterSave != null) afterSave.run();
                    else if (activeCard != null) requestPanelList(activeCard);
                    else refreshProjectFromWindows(true);
                }));
        settings.show();
    }

    private JSONObject newBridgeRequest(String type, String requestId) throws JSONException {
        JSONObject request = new JSONObject();
        request.put("protocol", ProtocolV3.VERSION);
        request.put("type", type);
        request.put("requestId", requestId);
        request.put("sentAt", System.currentTimeMillis());
        return request;
    }

    private JSONObject callBridge(BridgeConfig config, JSONObject request) throws Exception {
        return callBridge(config, request, new BridgeTransport.Exchange());
    }

    private JSONObject callBridge(BridgeConfig config, JSONObject request, BridgeTransport.Exchange exchange) throws Exception {
        bridgeExchanges.add(exchange);
        try {
            if (destroyed) { exchange.abort(); throw new IOException("Приложение закрыто."); }
            String line = exchange.perform(config.host, config.port, () -> {
                // Queuing, DNS and VPN connection time must not age an otherwise fresh authenticated request.
                // Only the send timestamp changes; the durable operation ID/request ID are never replaced.
                JSONObject outbound = new JSONObject(request.toString()).put("sentAt", System.currentTimeMillis());
                ProtocolV3.Envelope sealed = ProtocolV3.encryptRequest(
                        outbound.toString().getBytes(StandardCharsets.UTF_8), config.pairingCode);
                return new JSONObject().put("protocol", ProtocolV3.VERSION).put("type", "bridge-request")
                        .put("iv", sealed.iv).put("data", sealed.data).put("mac", sealed.mac).toString();
            }, ProtocolV3.MAX_ENVELOPE_BYTES, ProtocolV3.MAX_RESPONSE_ENVELOPE_BYTES);
            JSONObject outer = new JSONObject(line);
            if (outer.optInt("protocol") != ProtocolV3.VERSION
                    || !"bridge-response".equals(outer.optString("type"))) {
                throw new IOException("Нужен Windows-мост с защищённым протоколом v3");
            }
            byte[] plain = ProtocolV3.decryptResponse(new ProtocolV3.Envelope(
                    outer.getString("iv"), outer.getString("data"), outer.getString("mac")), config.pairingCode);
            JSONObject response = new JSONObject(new String(plain, StandardCharsets.UTF_8));
            ProtocolV3.verifyResponseMetadata(response.optInt("protocol"), response.optString("type"),
                    request.getString("requestId"), response.optString("requestId"));
            return response;
        } finally { bridgeExchanges.remove(exchange); }
    }

    private BridgeConfig bridgeConfig() {
        return new BridgeConfig(bridgePrefs.getString("host", "").trim(),
                bridgePrefs.getInt("port", DEFAULT_BRIDGE_PORT),
                ProtocolV3.normalizePairingCode(bridgePrefs.getString("pairing_code", "")));
    }

    private BridgeConfig operationBridgeConfig(RegistrationOperation operation) {
        // A changed global endpoint must not redirect a saved operation to another Windows computer.
        String code = operation.pairingCode;
        if (operation.host.equals(bridgePrefs.getString("host", "").trim())
                && operation.port == bridgePrefs.getInt("port", DEFAULT_BRIDGE_PORT)) {
            code = bridgePrefs.getString("pairing_code", code);
        }
        return new BridgeConfig(operation.host, operation.port, ProtocolV3.normalizePairingCode(code));
    }

    private void requestPanelList(DeviceCard card) {
        if (activeCard != card || destroyed) return;
        Runnable fetch = () -> ensureLocalNetworkPermission(() -> fetchPanelList(card));
        if (!bridgeConfigured()) showBridgeSettings(fetch);
        else fetch.run();
    }

    private void refreshProjectPanels(boolean requested) {
        if (destroyed || projectFingerprint == null || panelCatalogueLoading) return;
        if (!requested && (activeCard != null || System.currentTimeMillis() - lastPanelRefresh < 30000)) return;
        Runnable fetch = () -> ensureLocalNetworkPermission(() -> fetchPanelCatalogue(null, requested));
        if (!bridgeConfigured()) {
            if (requested) showBridgeSettings(fetch);
        } else fetch.run();
    }

    private void fetchPanelList(DeviceCard card) {
        if (activeCard != card || destroyed || card.loadingPanels) return;
        fetchPanelCatalogue(card, false);
    }

    private void fetchPanelCatalogue(DeviceCard card, boolean requested) {
        if (destroyed || (card != null && activeCard != card)) return;
        final BridgeConfig config;
        final JSONObject request;
        final String fingerprint = projectFingerprint;
        try {
            config = bridgeConfig();
            request = newBridgeRequest("list_panels", UUID.randomUUID().toString());
            request.put("projectFingerprint", fingerprint);
            if (card != null) request.put("recordId", card.device.id);
        } catch (Exception error) {
            if (card != null) card.panelHint.setText("Настройте адрес и код Windows-моста.");
            return;
        }
        final int generation = ++panelRequestGeneration;
        panelCatalogueLoading = true;
        lastPanelRefresh = System.currentTimeMillis();
        setCatalogueStatus("Панели · загрузка…", "Получение каталога проекта из Windows-моста…");
        if (card != null) {
            card.loadingPanels = true;
            card.config = null;
            card.panelHint.setText("Получение панели датчика из проекта Windows…");
            refreshRegistrationCard(card);
        }
        bridgeExecutor.execute(() -> {
            JSONObject result = null;
            Exception failure = null;
            try { result = callBridge(config, request); }
            catch (Exception error) { failure = error; }
            JSONObject response = result;
            Exception error = failure;
            onUiWhenStorageReady(() -> {
                if (destroyed || generation != panelRequestGeneration || !fingerprint.equals(projectFingerprint)) return;
                panelCatalogueLoading = false;
                boolean currentCard = card != null && activeCard == card;
                if (currentCard) {
                    card.loadingPanels = false;
                    card.panels.clear();
                    card.config = null;
                    card.projectMatches = false;
                }
                try {
                    if (!config.sameEndpointAndCode(bridgeConfig())) return;
                    if (error != null) throw error;
                    boolean live = isNativeCatalogue(response);
                    boolean replaced = applyProjectCatalogueResponse(response);
                    reportCatalogueResponse(response, replaced);
                    rebuildPanelFilter();
                    applyFilters();
                    if (currentCard) {
                        JSONArray rows = response.getJSONArray("panels");
                        Set<String> seen = new HashSet<>();
                        ProjectPanelIndex.Panel projectPanel = panelIndex.uniqueForRecord(card.device.id);
                        for (int i = 0; i < rows.length(); i++) {
                            PanelProfile profile = new PanelProfile(rows.getJSONObject(i), live, card.device.id);
                            if (!seen.add(profile.panelId)) throw new IOException("Windows-мост вернул повторяющиеся профили панелей");
                            // The full catalogue and the operation profile must independently name this record.
                            boolean catalogued = false;
                            for (ProjectPanelIndex.Panel membership : panelIndex.forRecord(card.device.id))
                                if (membership.id.equals(profile.projectPanelId)) catalogued = true;
                            if (catalogued && profile.recordIds.contains(ProjectPanelIndex.normalized(card.device.id)))
                                card.panels.add(profile);
                        }
                        card.projectMatches = projectPanel != null;
                        if (live && card.projectMatches) card.config = config;
                        card.panelListMessage = !replaced
                                ? (projectPanel == null ? "Панель не загружена.\n" : "Показана ранее сохранённая панель.\n")
                                + catalogueResponseDetails(response, false)
                                : projectCatalogueMissing(response) ? catalogueResponseDetails(response, true)
                                : projectPanel == null
                                ? panelIndex.forRecord(card.device.id).isEmpty()
                                ? "В полученном проекте не определена панель этого датчика.\n" + catalogueResponseDetails(response, true)
                                : "У датчика несколько панелей в проекте. Уточните принадлежность в Стрелец-Мастере."
                                : !live ? "Панель загружена из файла проекта.\n" + catalogueResponseDetails(response, true)
                                : card.panels.isEmpty() ? "Панель датчика найдена в проекте, но рабочее подключение не определено. Проверьте маршрут в Windows."
                                : "Панель выбрана автоматически по принадлежности датчика в проекте.";
                        if (!prefs.contains(descriptionPreferenceKey(card.device))
                                && card.description.getText().toString().equals(card.originalDescription)) {
                            card.originalDescription = getDescription(card.device);
                            card.description.setText(card.originalDescription);
                        }
                        populateCardPanels(card);
                    } else if (requested) {
                        toast(!replaced ? (panelIndex.panels().isEmpty()
                                ? "Карта панелей не получена. Нажмите строку связи, чтобы увидеть причину."
                                : "В Windows пока нет карты панелей. На телефоне оставлен прежний каталог.")
                                : projectCatalogueMissing(response) ? "Карта панелей не получена. Нажмите строку связи, чтобы увидеть причину."
                                : "Панели проекта обновлены: " + panelIndex.panels().size()
                                + (live ? "" : ". Для регистрации требуется связь со Стрелец-Мастером."));
                    }
                } catch (Exception problem) {
                    reportCatalogueFailure(response, problem);
                    if (currentCard) {
                        card.panels.clear();
                        card.config = null;
                        card.projectMatches = false;
                        card.panelListMessage = "Панель не проверена. " + bridgeError(problem);
                        populateCardPanels(card);
                    } else if (requested) toast("Панели не обновлены. " + bridgeError(problem));
                }
                if (currentCard) refreshRegistrationCard(card);
            });
        });
    }

    private void populateCardPanels(DeviceCard card) {
        String[] labels = new String[card.panels.size() + 1];
        labels[0] = card.panels.isEmpty() ? projectPanelLabel(card.device) : "Выберите панель";
        int selection = 0;
        ProjectPanelIndex.Panel projectPanel = panelIndex.uniqueForRecord(card.device.id);
        RegistrationOperation pending = operationFor(card.device);
        for (int i = 0; i < card.panels.size(); i++) {
            PanelProfile panel = card.panels.get(i);
            labels[i + 1] = panel.label();
            if (pending != null && pending.needsResolution()) {
                if (panel.panelId.equals(pending.panelId)) selection = i + 1;
            } else if (card.panels.size() == 1 && projectPanel != null
                    && projectPanel.id.equals(panel.projectPanelId)) selection = i + 1;
        }
        card.panelSpinner.setAdapter(filterAdapter(labels));
        card.panelSpinner.setSelection(selection);
        card.panelHint.setText(card.panelListMessage);
    }

    private PanelProfile selectedPanel(DeviceCard card) {
        int index = card.panelSpinner.getSelectedItemPosition() - 1;
        return index >= 0 && index < card.panels.size() ? card.panels.get(index) : null;
    }

    private String panelPreferenceKey(Device device) {
        return "panel:" + projectFingerprint + ":" + device.id.toLowerCase(Locale.ROOT);
    }

    private String operationPreferenceKey(String fingerprint, String recordId) {
        return "registration:" + fingerprint + ":" + recordId.toLowerCase(Locale.ROOT);
    }

    private RegistrationOperation operationFor(Device device) {
        if (projectFingerprint == null) return null;
        String key = operationPreferenceKey(projectFingerprint, device.id);
        if (operations.containsKey(key)) return operations.get(key);
        String stored = bridgePrefs.getString(key, "");
        if (stored.isEmpty()) return null;
        try {
            RegistrationOperation operation = RegistrationOperation.fromStored(new JSONObject(stored));
            if (!projectFingerprint.equals(operation.projectFingerprint)
                    || !device.id.equalsIgnoreCase(operation.recordId)) return null;
            if (retirementStorageUncertain.contains(key)) return null;
            if (operation.localResolved) validateRetiredArchive(operation);
            operations.put(key, operation);
            return operation;
        } catch (Exception error) {
            // Keep the persisted value. Losing recovery data must never enable a fresh begin implicitly.
            return null;
        }
    }

    private boolean saveOperation(RegistrationOperation operation, boolean durable) {
        if (!isCurrentOperation(operation, operation.generation)) return false;
        try {
            String key = operationPreferenceKey(operation.projectFingerprint, operation.recordId);
            SharedPreferences.Editor editor = bridgePrefs.edit().putString(key, operation.toStored().toString());
            if (durable) return editor.commit();
            editor.apply();
            return true;
        } catch (JSONException error) {
            return false;
        }
    }

    private boolean isCurrentOperation(RegistrationOperation operation, long generation) {
        if (operation == null || operation.localResolved || operation.generation != generation) return false;
        String key = operationPreferenceKey(operation.projectFingerprint, operation.recordId);
        if (retirementStorageUncertain.contains(key) || operations.get(key) != operation) return false;
        try {
            JSONObject saved = new JSONObject(bridgePrefs.getString(key, ""));
            return operation.operationId.equals(saved.optString("operationId"))
                    && generation == saved.optLong("generation", 1) && !saved.optBoolean("localResolved", false);
        } catch (Exception error) { return false; }
    }

    private boolean canInstallOperation(RegistrationOperation operation, RegistrationOperation previous) throws JSONException {
        String key = operationPreferenceKey(operation.projectFingerprint, operation.recordId);
        if (retirementStorageUncertain.contains(key) || operations.get(key) != previous
                || (previous != null && (previous.needsResolution() || previous.manuallyRegistered()
                || operationsInFlight.contains(previous.operationId)))) return false;
        Device device = operation.projectFingerprint.equals(projectFingerprint) ? findById(operation.recordId) : null;
        if (device != null && manualRegistrationBlocksStart(device)) return false;
        String stored = bridgePrefs.getString(key, "");
        if (previous == null && !stored.isEmpty()) return false;
        if (previous != null && !previous.operationId.equals(new JSONObject(stored).optString("operationId"))) return false;
        return true;
    }

    private boolean installOperation(RegistrationOperation operation, RegistrationOperation previous) throws JSONException {
        if (!canInstallOperation(operation, previous)) return false;
        String key = operationPreferenceKey(operation.projectFingerprint, operation.recordId);
        String stored = bridgePrefs.getString(key, "");
        if (!bridgePrefs.edit().putString(key, operation.toStored().toString()).commit()) {
            bridgePrefs.edit().putString(key, stored).commit();
            return false;
        }
        operations.put(key, operation);
        return true;
    }

    private JSONObject importedRegistrationSummary(Device device) throws JSONException {
        String stored = prefs.getString(key("registration_summary", device), "");
        return !stored.isEmpty() ? new JSONObject(stored)
                : device.commissioning == null ? null : device.commissioning.optJSONObject("registration");
    }

    private boolean importedRegistrationNeedsResolution(Device device) {
        try {
            JSONObject summary = importedRegistrationSummary(device);
            // A reviewed operation remains an audit of its ORIGINAL project. Adding records changes the
            // current project hash, but must neither reopen that review nor rewrite its five identities.
            if (summary != null && summary.optBoolean("localResolved"))
                return !device.id.equalsIgnoreCase(summary.optString("recordId"))
                        || !RegistrationRecovery.validArchivedManualReview(summary);
            return RegistrationRecovery.unresolvedSummary(summary);
        }
        catch (JSONException error) { return true; }
    }

    private boolean manualRegistrationBlocksStart(Device device) {
        RegistrationOperation operation = operationFor(device);
        if (operation != null && operation.manuallyRegistered()) return true;
        try {
            JSONObject summary = importedRegistrationSummary(device);
            return RegistrationRecovery.manualRegisteredSummary(summary)
                    && device.id.equalsIgnoreCase(summary.optString("recordId"));
        } catch (JSONException error) { return false; } // Corrupt imported history is separately blocked as unresolved.
    }

    private boolean hasReviewedRegistrationHistory(Device device) {
        try {
            JSONObject summary = importedRegistrationSummary(device);
            return summary != null && device.id.equalsIgnoreCase(summary.optString("recordId"))
                    && RegistrationRecovery.validArchivedManualReview(summary);
        } catch (JSONException error) { return false; }
    }

    private void preserveManualRegistration(SharedPreferences.Editor changes, Device device) throws JSONException {
        String summary = preservedManualSummary(device);
        if (summary != null) changes.putString(key("registration_summary", device), summary);
    }

    private String preservedManualSummary(Device device) throws JSONException {
        if (!manualRegistrationBlocksStart(device)) return null;
        JSONObject historical = new JSONObject();
        addRegistrationSummary(historical, device);
        JSONObject summary = historical.getJSONObject("registration");
        if (!RegistrationRecovery.manualRegisteredSummary(summary))
            throw new JSONException("Не удалось сохранить запрет повторной регистрации из истории сверки.");
        return summary.toString();
    }

    private void prepareImportedRegistrationRecovery(DeviceCard card) {
        if (activeCard != card || operationFor(card.device) != null || !importedRegistrationNeedsResolution(card.device)) return;
        if (!bridgePrefs.getString(operationPreferenceKey(projectFingerprint, card.device.id), "").isEmpty()) {
            toast("Сохранённая операция повреждена. Сохраните журнал и сверьте результат в Windows.");
            return;
        }
        try {
            JSONObject summary = importedRegistrationSummary(card.device);
            if (summary == null) throw new IOException("В журнале нет идентификатора операции.");
            if ((summary.has("projectFingerprint") && !projectFingerprint.equals(summary.getString("projectFingerprint")))
                    || (summary.has("recordId") && !card.device.id.equalsIgnoreCase(summary.getString("recordId"))))
                throw new IOException("История относится к другому проекту или записи. Сверьте исходный проект в Windows.");
            RegistrationRecovery.Identity identity = new RegistrationRecovery.Identity(summary.getString("operationId"),
                    projectFingerprint, card.device.id, summary.getString("expectedHardwareId"), summary.getString("panelId"));
            PanelProfile panel = selectedPanel(card);
            if (card.loadingPanels || !card.projectMatches || card.config == null || panel == null
                    || !panel.available || !panel.panelId.equals(identity.panelId)
                    || !panel.recordIds.contains(card.device.id.toLowerCase(Locale.ROOT)))
                throw new IOException("Обновите панель из Windows. Для сверки нужна подтверждённая принадлежность этой записи "
                        + "точно той панели, которая указана в перенесённой операции.");
            BridgeConfig current = bridgeConfig();
            if (!current.sameEndpointAndCode(card.config)) throw new IOException("Настройки Windows изменились. Обновите панель.");
            boolean originalEndpoint = summary.has("host") && summary.has("port");
            String host = originalEndpoint ? summary.getString("host") : current.host;
            int port = originalEndpoint ? summary.getInt("port") : current.port;
            if (host.trim().isEmpty() || port < 1 || port > 65535) throw new IOException("В истории повреждён адрес Windows.");
            RegistrationOperation candidate = new RegistrationOperation();
            candidate.operationId = identity.operationId;
            candidate.panelId = identity.panelId;
            candidate.panelLabel = summary.optString("panelLabel", panel.label());
            candidate.projectFingerprint = identity.projectFingerprint;
            candidate.recordId = identity.recordId;
            candidate.expectedHardwareId = identity.expectedHardwareId;
            candidate.host = host;
            candidate.port = port;
            candidate.pairingCode = host.equals(current.host) && port == current.port ? current.pairingCode : "";
            candidate.connectionSource = originalEndpoint ? "imported_original_endpoint" : "user_selected_endpoint_for_imported_history";
            candidate.state = "unknown";
            candidate.uncertain = true;
            candidate.bridgeHistoryMissing = summary.optBoolean("bridgeHistoryMissing", false);
            candidate.startedAt = summary.optLong("startedAt", 0);
            candidate.updatedAt = summary.optLong("updatedAt", 0);
            candidate.message = "Незавершённая операция перенесена из журнала. Результат требует сверки.";
            new AlertDialog.Builder(this).setTitle("Подключение для сверки перенесённой операции")
                    .setMessage("Запись: " + getDisplayName(card.device) + "\nGUID: " + candidate.recordId
                            + "\nПолный ID: " + candidate.expectedHardwareId + "\nПанель: " + candidate.panelLabel
                            + "\nID панели: " + candidate.panelId + "\nОперация: " + candidate.operationId
                            + "\nWindows: " + BridgeEndpoint.display(host, port) + "\n\n" + (originalEndpoint
                            ? "Используется исходный адрес Windows из истории."
                            : "Исходный адрес Windows в старой копии не сохранён. Вы выбираете показанный адрес только для сверки. "
                                    + "Проект и GUID записи восстановлены из содержащей эту историю записи.")
                            + "\nБудет прочитан результат той же операции. Регистрация и отмена не запускаются.")
                    .setNegativeButton("Вернуться", null)
                    .setPositiveButton("Сверить на этом Windows", (dialog, which) -> {
                        if (activeCard != card || !candidate.projectFingerprint.equals(projectFingerprint)) return;
                        try {
                            JSONObject latest = importedRegistrationSummary(card.device);
                            PanelProfile latestPanel = selectedPanel(card);
                            if (latest == null || !candidate.operationId.equals(latest.optString("operationId"))
                                    || !candidate.expectedHardwareId.equalsIgnoreCase(latest.optString("expectedHardwareId"))
                                    || !candidate.panelId.equals(latest.optString("panelId")) || card.loadingPanels
                                    || !card.projectMatches || latestPanel == null || !latestPanel.available
                                    || !candidate.panelId.equals(latestPanel.panelId)
                                    || !current.sameEndpointAndCode(bridgeConfig()))
                                throw new IOException("Данные проекта или подключения изменились. Откройте сверку заново.");
                            installOperationAsync(candidate, () -> {
                                refreshRegistrationCard(card);
                                if (activeCard == card && foreground) recoverRegistration(card, candidate);
                            });
                        } catch (Exception error) { toast(bridgeError(error)); }
                    }).show();
        } catch (Exception error) { toast(bridgeError(error)); }
    }

    private void beginRegistrationFromCard(DeviceCard card) {
        if (projectInstalling.get()) return;
        if (activeCard != card || destroyed) return;
        if (projectStateUncertain) { toast("Сначала восстановите проект из «Проекты на телефоне»."); return; }
        if (manualRegistrationBlocksStart(card.device)) {
            toast("Полный ID уже зарегистрирован по ручной сверке. Повторная регистрация для этой записи недоступна.");
            return;
        }
        RegistrationOperation previous = operationFor(card.device);
        if (previous != null && previous.registrationConfirmed) {
            toast("Регистрация уже подтверждена. При необходимости сохраните результат в Windows.");
            return;
        }
        if (previous != null && previous.needsResolution()) {
            ensureLocalNetworkPermission(() -> pollRegistration(previous));
            return;
        }
        String storedKey = operationPreferenceKey(projectFingerprint, card.device.id);
        if (previous == null && !bridgePrefs.getString(storedKey, "").isEmpty()) {
            toast("Данные предыдущей операции не удалось прочитать. Проверьте её результат в Windows перед новой регистрацией.");
            return;
        }
        if (previous == null && importedRegistrationNeedsResolution(card.device)) {
            toast("В перенесённом журнале осталась незавершённая операция. Сначала выполните её сверку.");
            return;
        }
        if (!journalConflict(card.device).isEmpty()) {
            reviewJournalConflict(card);
            return;
        }
        if (getStage(card.device) >= 2) {
            toast("Устройство уже отмечено как инициализированное.");
            return;
        }
        PanelProfile panel = selectedPanel(card);
        if (panel == null || !panel.available || card.config == null || !card.projectMatches) {
            toast("Получите список панелей и выберите доступный профиль нужного проекта.");
            return;
        }
        try {
            if (!card.config.sameEndpointAndCode(bridgeConfig())) {
                requestPanelList(card);
                toast("Настройки Windows изменились. Выберите панель из обновлённого списка.");
                return;
            }
        } catch (Exception error) {
            showBridgeSettings(() -> requestPanelList(card));
            return;
        }
        String hardwareId = card.serial.getText().toString().trim().toUpperCase(Locale.ROOT);
        String deviceKey = card.deviceKey.getText().toString().trim().toUpperCase(Locale.ROOT);
        if (!hardwareId.matches("[0-9A-F]{12}")) {
            card.serial.setError("Нужен аппаратный ID из 12 шестнадцатеричных символов");
            card.serial.requestFocus();
            return;
        }
        Device duplicate = findBySerial(hardwareId, card.device.id);
        if (duplicate != null) {
            card.serial.setError("Этот ID уже указан у «" + duplicate.name + "»");
            return;
        }
        if (!deviceKey.isEmpty() && !deviceKey.matches("[0-9A-F]{8}")) {
            card.deviceKey.setError("Для регистрации KEY должен содержать 8 HEX-символов; пусто — использовать ключ проекта Windows");
            return;
        }
        ensureLocalNetworkPermission(() -> {
            if (destroyed || activeCard != card) return;
            if (manualRegistrationBlocksStart(card.device)) return;
            RegistrationOperation current = operationFor(card.device);
            if (current != null && current.registrationConfirmed) return;
            if (current != null && current.needsResolution()) {
                pollRegistration(current);
                return;
            }
            if (current == null && importedRegistrationNeedsResolution(card.device)) return;
            try {
                RegistrationOperation operation = new RegistrationOperation();
                operation.operationId = UUID.randomUUID().toString();
                operation.panelId = panel.panelId;
                operation.panelLabel = panel.label();
                operation.recordId = card.device.id;
                operation.expectedHardwareId = hardwareId;
                operation.projectFingerprint = projectFingerprint;
                operation.host = card.config.host;
                operation.port = card.config.port;
                operation.pairingCode = card.config.pairingCode;
                operation.state = "connecting";
                operation.message = "Подключение к панели через Windows-мост…";
                operation.startedAt = System.currentTimeMillis();
                operation.expiresAt = operation.startedAt + 90000;
                JSONObject request = newBridgeRequest("begin_registration", operation.operationId);
                request.put("projectFingerprint", projectFingerprint);
                request.put("panelId", panel.panelId);
                request.put("recordId", card.device.id);
                request.put("expectedHardwareId", hardwareId);
                request.put("deviceKey", deviceKey);
                request.put("timeoutSeconds", 90);
                request.put("note", card.note.getText().toString().trim());
                if (!canInstallOperation(operation, current))
                    throw new IOException("Операция изменилась. Регистрация не отправлена.");
                String storedKeyForStart = operationPreferenceKey(operation.projectFingerprint, operation.recordId);
                String before = bridgePrefs.getString(storedKeyForStart, "");
                String prepared = operation.toStored().toString();
                JSONObject failed = new JSONObject(prepared).put("state", "failed").put("terminal", true)
                        .put("message", "Запись на телефоне не завершена. Регистрация не отправлена.");
                SharedPreferences.Editor recordChanges = prefs.edit().putInt(key("stage", card.device), 1)
                        .putString(key("serial", card.device), hardwareId)
                        .putString(key("device_key", card.device), deviceKey)
                        .putString(key("note", card.device), card.note.getText().toString().trim())
                        .putLong(key("updated", card.device), System.currentTimeMillis());
                saveDescriptionIfChanged(recordChanges, card);
                runFileWork("Подготовка инициализации", () -> {
                    RegistrationStartStore.prepare(new RegistrationStartStore.Store() {
                        public String readOperation() { return bridgePrefs.getString(storedKeyForStart, ""); }
                        public boolean writeOperation(String value) {
                            SharedPreferences.Editor edit = bridgePrefs.edit();
                            return (value.isEmpty() ? edit.remove(storedKeyForStart) : edit.putString(storedKeyForStart, value)).commit();
                        }
                        public boolean writeRecord() { return recordChanges.commit(); }
                    }, before, prepared, failed.toString());
                }, error -> {
                    operations.remove(storedKeyForStart);
                    if (error != null) {
                        refreshRegistrationCard(card);
                        reportManualFileResult(error, "");
                        return;
                    }
                    operations.put(storedKeyForStart, operation);
                    refreshDevice(card.device);
                    // Closing/recreating the screen never resends begin; recovery only asks for status.
                    if (destroyed || !foreground || activeCard != card || !operation.projectFingerprint.equals(projectFingerprint)) return;
                    try {
                        PanelProfile latestPanel = selectedPanel(card);
                        if (card.config == null || !card.config.sameEndpointAndCode(bridgeConfig())
                                || !card.projectMatches || latestPanel == null || !latestPanel.available
                                || !panel.panelId.equals(latestPanel.panelId)
                                || !hardwareId.equalsIgnoreCase(card.serial.getText().toString().trim())
                                || !deviceKey.equalsIgnoreCase(card.deviceKey.getText().toString().trim())
                                || !isCurrentOperation(operation, operation.generation)) {
                            refreshRegistrationCard(card); return;
                        }
                        bridgePrefs.edit().putString(panelPreferenceKey(card.device), panel.panelId).apply();
                        hideKeyboard(card.serial);
                        card.stage.setSelection(1);
                        sendOperationRequest(operation, request, "begin_registration");
                    } catch (Exception problem) { card.status.setText(bridgeError(problem)); }
                });
            } catch (Exception error) {
                card.status.setText(bridgeError(error));
            }
        });
    }

    private void resumeRegistrations() {
        if (destroyed || projectInstalling.get() || projectFingerprint == null) return;
        for (Device device : devices) {
            RegistrationOperation operation = operationFor(device);
            if (operation != null && operation.registrationConfirmed) recordConfirmedRegistration(operation);
            if (operation != null && operation.needsResolution()) scheduleRegistrationPoll(operation, 0);
        }
    }

    private boolean httpsConfigured() {
        return bridgePrefs.getString("host", "").trim().regionMatches(true, 0, "https://", 0, 8);
    }

    private boolean hasLocalNetworkPermission() {
        if (httpsConfigured()) return true;
        return Build.VERSION.SDK_INT < 37
                || checkSelfPermission(Manifest.permission.ACCESS_LOCAL_NETWORK) == PackageManager.PERMISSION_GRANTED;
    }

    private static final class OperationRequest {
        final String type;
        final BridgeTransport.Exchange exchange = new BridgeTransport.Exchange();
        OperationRequest(String type) { this.type = type; }
        boolean readOnly() { return "registration_status".equals(type) || "recover_registration".equals(type); }
    }

    private OperationRequest startOperationRequest(RegistrationOperation operation, String type) {
        OperationRequest request = new OperationRequest(type);
        operationRequests.put(operation.operationId, request);
        operationsInFlight.add(operation.operationId);
        return request;
    }

    private boolean finishOperationRequest(RegistrationOperation operation, OperationRequest request) {
        // A cancelled read may complete after the dedicated cancellation request has started.
        // Its callback must not remove that newer request's lock or publish a stale status.
        if (operationRequests.get(operation.operationId) != request) return false;
        operationRequests.remove(operation.operationId);
        operationsInFlight.remove(operation.operationId);
        return true;
    }

    private boolean preemptReadForCancel(RegistrationOperation operation) {
        OperationRequest request = operationRequests.get(operation.operationId);
        if (request == null || !request.readOnly()) return false;
        operationRequests.remove(operation.operationId);
        operationsInFlight.remove(operation.operationId);
        request.exchange.abort();
        return true;
    }

    private void scheduleRegistrationPoll(RegistrationOperation operation, long delay) {
        Runnable old = operationPolls.remove(operation.operationId);
        if (old != null) mainHandler.removeCallbacks(old);
        if (destroyed || !foreground || !isCurrentOperation(operation, operation.generation)
                || !operation.shouldAutoPoll() || !hasLocalNetworkPermission()) return;
        final long generation = operation.generation;
        Runnable callback = () -> {
            operationPolls.remove(operation.operationId);
            if (!isCurrentOperation(operation, generation) || !operation.shouldAutoPoll()) return;
            pollRegistration(operation);
        };
        operationPolls.put(operation.operationId, callback);
        mainHandler.postDelayed(callback, delay);
    }

    private void pollRegistration(RegistrationOperation operation) {
        if (destroyed || projectInstalling.get() || !isCurrentOperation(operation, operation.generation)
                || operationsInFlight.contains(operation.operationId) || !hasLocalNetworkPermission()) return;
        try {
            String type = operation.cancelQueued ? "cancel_registration" : "registration_status";
            JSONObject request = newBridgeRequest(type, UUID.randomUUID().toString());
            request.put("panelId", operation.panelId);
            request.put("operationId", operation.operationId);
            if (operation.cancelQueued) {
                operation.cancelQueued = false;
                operation.cancelRequested = true;
                saveOperationAsync(operation, () -> sendOperationRequest(operation, request, type));
                return;
            }
            sendOperationRequest(operation, request, type);
        } catch (Exception error) {
            operation.pollFailures = Math.min(8, operation.pollFailures + 1);
            operation.uncertain = !operation.registrationConfirmed;
            operation.deliveryMessage = "Результат не получен. " + bridgeError(error);
            saveOperation(operation, false);
            refreshActiveRegistrationCard(operation);
            scheduleRegistrationPoll(operation, ClientUxPolicy.operationDelay(operation.pollFailures));
        }
    }

    private void cancelRegistration(RegistrationOperation operation) {
        if (projectInstalling.get() || !isCurrentOperation(operation, operation.generation) || !operation.needsResolution()
                || operation.bridgeHistoryMissing) return;
        final long generation = operation.generation;
        ensureLocalNetworkPermission(() -> {
            if (!isCurrentOperation(operation, generation) || operation.bridgeHistoryMissing) return;
            OperationRequest pending = operationRequests.get(operation.operationId);
            if (pending != null && "cancel_registration".equals(pending.type)) return;
            operation.cancelQueued = true;
            operation.cancelRequested = true;
            operation.deliveryMessage = pending != null && "begin_registration".equals(pending.type)
                    ? "Отмена поставлена в очередь. Ожидается ответ на уже отправленный запуск; после него отмена уйдёт автоматически. Через VPN ответ может занять до двух минут."
                    : "Запрошена отмена. Ожидается подтверждение Windows-моста; через VPN ответ может занять до двух минут.";
            saveOperationAsync(operation, () -> {
                preemptReadForCancel(operation);
                refreshActiveRegistrationCard(operation);
                if (!operationsInFlight.contains(operation.operationId)) pollRegistration(operation);
            });
        });
    }

    private void sendOperationRequest(RegistrationOperation operation, JSONObject request, String requestType) {
        if (projectInstalling.get() || !isCurrentOperation(operation, operation.generation)
                || operationsInFlight.contains(operation.operationId) || destroyed) return;
        final long generation = operation.generation;
        operation.recoveryProof = null;
        final BridgeConfig config;
        try { config = operationBridgeConfig(operation); }
        catch (Exception error) {
            operation.pollFailures = Math.min(8, operation.pollFailures + 1);
            operation.uncertain = !operation.registrationConfirmed;
            operation.deliveryMessage = "Результат не получен. Проверьте код подключения Windows.";
            saveOperation(operation, false);
            refreshActiveRegistrationCard(operation);
            scheduleRegistrationPoll(operation, ClientUxPolicy.operationDelay(operation.pollFailures));
            return;
        }
        final OperationRequest pending = startOperationRequest(operation, requestType);
        refreshActiveRegistrationCard(operation);
        ExecutorService executor = "cancel_registration".equals(requestType) ? bridgeCancelExecutor : bridgeExecutor;
        executor.execute(() -> {
            JSONObject result = null;
            Exception failure = null;
            try { result = callBridge(config, request, pending.exchange); }
            catch (Exception error) { failure = error; }
            JSONObject response = result;
            Exception error = failure;
            onUiWhenStorageReady(() -> {
                if (!finishOperationRequest(operation, pending)) return;
                if (destroyed) return;
                if (!isCurrentOperation(operation, generation)) { resumeRegistrations(); return; }
                try {
                    if (error != null) throw error;
                    operation.lastReplyAt = System.currentTimeMillis();
                    operation.pollFailures = 0;
                    JSONObject answer = response.optJSONObject("operation");
                    if (answer != null) {
                        applyOperationAnswer(operation, answer);
                    } else if (RegistrationRecovery.authenticatedHistoryMissing(requestType, operation.operationId, response)) {
                        applyMissingHistory(operation, response);
                    } else if (ProtocolV3.isExplicitPredispatchRejection(requestType,
                            response.optBoolean("ok", false),
                            response.opt("accepted") instanceof Boolean ? (Boolean) response.opt("accepted") : null,
                            response.optString("errorCode", ""))) {
                        // HMAC proves the response's origin, not that no hardware action occurred.
                        // Require an explicit accepted:false plus a known pre-dispatch code.
                        operation.state = "failed";
                        operation.terminal = true;
                        operation.registrationConfirmed = false;
                        operation.uncertain = false;
                        operation.message = response.optString("message", "Windows-мост отклонил регистрацию");
                        operation.deliveryMessage = "";
                        operation.cancelQueued = false;
                    } else if (operation.registrationConfirmed) {
                        operation.deliveryMessage = "Регистрация уже подтверждена. Windows не вернул её сохранённый результат; "
                                + "подтверждение оборудования сохранено на телефоне.";
                    } else {
                        operation.state = "unknown";
                        operation.terminal = false;
                        operation.registrationConfirmed = false;
                        operation.confirmedHardwareId = "";
                        operation.uncertain = true;
                        operation.message = response.optString("message", "Мост не вернул состояние операции");
                        operation.deliveryMessage = operation.bridgeHistoryMissing
                                ? "Автоматическая проверка остановлена. Нажмите «Сверить незавершённую операцию»."
                                : "Результат не получен. Проверка сохранённой операции продолжается.";
                    }
                } catch (Exception problem) {
                    operation.pollFailures = Math.min(8, operation.pollFailures + 1);
                    operation.uncertain = !operation.registrationConfirmed;
                    operation.deliveryMessage = "Результат не получен. " + bridgeError(problem)
                            + (operation.registrationConfirmed
                            ? "\nПодтверждённая регистрация сохранена на телефоне. При необходимости нажмите «Сохранить результат в Windows»."
                            : operation.bridgeHistoryMissing
                            ? "\nНажмите «Сверить незавершённую операцию»."
                            : "\nПроверка той же операции продолжится; новая регистрация не отправляется.");
                }
                Runnable completed = () -> {
                    if (operation.registrationConfirmed && "registered".equals(operation.state)) recordConfirmedRegistration(operation);
                    refreshActiveRegistrationCard(operation);
                    scheduleRegistrationPoll(operation, operation.cancelQueued ? 0 : ClientUxPolicy.operationDelay(operation.pollFailures));
                };
                if (operation.bridgeHistoryMissing) saveOperationAsync(operation, completed);
                else { saveOperation(operation, false); completed.run(); }
            });
        });
    }

    private void recoverRegistration(DeviceCard card, RegistrationOperation operation) {
        if (projectInstalling.get() || activeCard != card || (!operation.canRecover() && !operation.needsWindowsSave())
                || !isCurrentOperation(operation, operation.generation)) return;
        final long generation = operation.generation;
        final boolean wasConfirmed = operation.registrationConfirmed;
        ensureLocalNetworkPermission(() -> {
            if (destroyed || activeCard != card || !isCurrentOperation(operation, generation)
                    || operationsInFlight.contains(operation.operationId)) return;
            final BridgeConfig config;
            final JSONObject request;
            try {
                config = operationBridgeConfig(operation);
                request = operation.identity().write(newBridgeRequest("recover_registration", UUID.randomUUID().toString()));
            } catch (Exception error) {
                operation.deliveryMessage = "Сверка недоступна. " + bridgeError(error)
                        + "\nУкажите действующий код подключения исходного Windows-моста.";
                saveOperation(operation, false);
                refreshRegistrationCard(card);
                return;
            }
            operation.recoveryProof = null;
            final OperationRequest pending = startOperationRequest(operation, "recover_registration");
            refreshRegistrationCard(card);
            bridgeExecutor.execute(() -> {
                JSONObject result = null;
                Exception failure = null;
                try { result = callBridge(config, request, pending.exchange); }
                catch (Exception error) { failure = error; }
                final JSONObject response = result;
                final Exception error = failure;
                onUiWhenStorageReady(() -> {
                    if (!finishOperationRequest(operation, pending)) return;
                    if (destroyed) return;
                    if (!isCurrentOperation(operation, generation)) { resumeRegistrations(); return; }
                    try {
                        if (error != null) throw error;
                        if (!response.optBoolean("ok", false)) throw new IOException(response.optString("message",
                                "Windows-мост не разрешил сверку. Проверьте связь с Мастером и версии приложений."));
                        operation.lastReplyAt = System.currentTimeMillis();
                        operation.pollFailures = 0;
                        RegistrationRecovery.Proof proof = RegistrationRecovery.authenticatedProof(response,
                                operation.identity(), generation, System.currentTimeMillis());
                        if (wasConfirmed && !"restored".equals(proof.outcome))
                            throw new IOException("Регистрация уже подтверждена на телефоне, но Windows не восстановил подтверждённый результат. "
                                    + "Сохранение проекта не подтверждено. Проверьте проект в Стрелец-Мастере; повторная регистрация не требуется.");
                        if ("restored".equals(proof.outcome)) {
                            applyOperationAnswer(operation, response.getJSONObject("operation"));
                            saveOperationAsync(operation, () -> {
                                if (operation.registrationConfirmed) recordConfirmedRegistration(operation);
                                refreshActiveRegistrationCard(operation);
                                scheduleRegistrationPoll(operation, ClientUxPolicy.operationDelay(operation.pollFailures));
                            });
                            return;
                        } else {
                            operation.state = "unknown";
                            operation.uncertain = true;
                            operation.registrationConfirmed = false;
                            operation.confirmedHardwareId = "";
                            operation.message = proof.message;
                            operation.deliveryMessage = "native_active".equals(proof.outcome)
                                    ? "В Стрелец-Мастере ещё открыта операция. Если Мастер показывает успешное добавление — нажмите «ОК» "
                                            + "и дождитесь закрытия окна. Для остановки ещё идущего поиска — «Отмена». "
                                            + "Затем повторите сверку. Карточка остаётся заблокированной."
                                    : "Результат в оборудовании автоматически не подтверждён. Для завершения ожидания на телефоне нужна ручная сверка.";
                            saveOperationAsync(operation, () -> {
                                operation.recoveryProof = proof;
                                if (proof.manualReviewAllowed && activeCard == card) showManualRegistrationReview(card, operation, proof);
                                refreshActiveRegistrationCard(operation);
                                scheduleRegistrationPoll(operation, ClientUxPolicy.operationDelay(operation.pollFailures));
                            });
                            return;
                        }
                    } catch (Exception problem) {
                        operation.pollFailures = Math.min(8, operation.pollFailures + 1);
                        operation.recoveryProof = null;
                        operation.deliveryMessage = "Сверка не завершена. " + bridgeError(problem)
                                + "\nОперация сохранена; повторный запуск остаётся заблокирован.";
                        saveOperation(operation, false);
                    }
                    refreshActiveRegistrationCard(operation);
                    scheduleRegistrationPoll(operation, ClientUxPolicy.operationDelay(operation.pollFailures));
                });
            });
        });
    }

    private void showManualRegistrationReview(DeviceCard card, RegistrationOperation operation, RegistrationRecovery.Proof proof) {
        showManualRegistrationReview(card, operation, proof, false);
    }

    private void showManualRegistrationReview(DeviceCard card, RegistrationOperation operation,
            RegistrationRecovery.Proof proof, boolean registered) {
        if (activeCard != card || !isCurrentOperation(operation, proof.generation)) return;
        LinearLayout body = new LinearLayout(this);
        body.setOrientation(LinearLayout.VERTICAL);
        body.setPadding(dp(20), dp(8), dp(20), dp(8));
        TextView info = text("Сверяйте исходную операцию, даже если в карточке введён другой ID.\n\n"
                + "Запись: " + getDisplayName(card.device) + " · строка " + card.device.sourceRow
                + "\nGUID записи: " + operation.recordId + "\nПолный ID: " + operation.expectedHardwareId
                + "\nПанель: " + operation.panelLabel + "\nID панели: " + operation.panelId
                + "\nWindows: " + operation.host + ":" + operation.port
                + "\nОперация: " + operation.operationId
                + "\n\nСтатус «не инициализировано» в исходном файле сам по себе не доказывает отсутствие регистрации. "
                + "Проверьте фактическое состояние в Мастере и на нужной панели.", 13, color("text_secondary"), false);
        info.setTextIsSelectable(true);
        body.addView(info, matchWrap());
        CheckBox stopped = new CheckBox(this);
        stopped.setText("Поиск/инициализация этой операции в Стрелец-Мастере остановлены; активного окна поиска нет.");
        body.addView(stopped, matchWrap());
        CheckBox verified = new CheckBox(this);
        verified.setText("Проверил фактическое состояние в Мастере/на панели: полный ID "
                + operation.expectedHardwareId + (registered
                ? " зарегистрирован именно на панели «" + operation.panelLabel + "»."
                : " не зарегистрирован."));
        body.addView(verified, matchWrap());
        body.addView(text("Будет завершено только ожидание на телефоне с сохранением истории. "
                + "Автоматическое подтверждение оборудования не получено; этап, ID, KEY и заметки останутся прежними. "
                + (registered ? "Повторная регистрация этой записи будет заблокирована. После закрытия карточки можно синхронизировать проект Windows. "
                : "Это не команда отмены в оборудовании. Новая операция автоматически не запускается. ")
                + "Проверка связи действует две минуты; если нужно больше времени, повторите сверку перед завершением ожидания.",
                13, color("text_secondary"), false), matchWrap());
        ScrollView scroll = new ScrollView(this);
        scroll.addView(body);
        AlertDialog review = new AlertDialog.Builder(this).setTitle(registered
                        ? "Сверка уже зарегистрированного устройства" : "Сверка незавершённой операции")
                .setView(scroll).setNegativeButton("Вернуться", null)
                .setNeutralButton(registered ? "Не зарегистрирован" : "Датчик зарегистрирован",
                        (dialog, which) -> showManualRegistrationReview(card, operation, proof, !registered))
                .setPositiveButton("Завершить ожидание на телефоне", null).create();
        review.setOnShowListener(ignored -> {
            Button finish = review.getButton(AlertDialog.BUTTON_POSITIVE);
            finish.setEnabled(false);
            stopped.setOnCheckedChangeListener((button, checked) -> finish.setEnabled(stopped.isChecked() && verified.isChecked()));
            verified.setOnCheckedChangeListener((button, checked) -> finish.setEnabled(stopped.isChecked() && verified.isChecked()));
            finish.setOnClickListener(v -> {
                if (activeCard != card) { review.dismiss(); return; }
                try {
                    if (projectInstalling.get()) return;
                    RetirementPlan plan = prepareRetirement(operation, proof, stopped.isChecked(), verified.isChecked(), registered, System.currentTimeMillis());
                    finish.setEnabled(false);
                    runFileWork("Сохранение ручной сверки", () -> {
                        if (!proof.canReview(operation.identity(), operation.generation, System.currentTimeMillis()))
                            throw new IOException("Срок сверки истёк. Повторите проверку связи.");
                        plan.persist();
                    }, error -> {
                        if (error != null) {
                            if (retirementStorageUncertain.contains(plan.key)) operations.remove(plan.key);
                            reportManualFileResult(error, "");refreshRegistrationCard(card);return;
                        }
                        plan.publish();
                        Runnable pending = operationPolls.remove(operation.operationId);
                        if (pending != null) mainHandler.removeCallbacks(pending);
                        review.dismiss();
                        refreshRegistrationCard(card);
                        toast("Ожидание на телефоне завершено после сверки. История сохранена.");
                    });
                } catch (Exception error) {
                    finish.setEnabled(false);
                    toast(bridgeError(error));
                    refreshRegistrationCard(card);
                }
            });
        });
        review.show();
    }

    private String operationHistoryKey(RegistrationOperation operation) {
        return "registration-history:" + operation.projectFingerprint + ":" + operation.recordId.toLowerCase(Locale.ROOT);
    }

    private void validateRetiredArchive(RegistrationOperation operation) throws JSONException {
        validateRetiredArchive(operation, new JSONArray(bridgePrefs.getString(operationHistoryKey(operation), "[]")));
    }

    static void validateRetiredArchive(RegistrationOperation operation, JSONArray history) throws JSONException {
        for (int i = 0; i < history.length(); i++) {
            JSONObject item = history.getJSONObject(i);
            if (operation.identity().matches(item) && item.optBoolean("localResolved")
                    && operation.localResolvedAt == item.optLong("localResolvedAt")
                    && "unknown".equals(item.optString("state")) && !item.optBoolean("registrationConfirmed")
                    && RegistrationRecovery.validManualReview(item.optJSONObject("manualReview"), operation.localResolvedAt)
                    && operation.manualReview.getString("outcome").equals(item.getJSONObject("manualReview").getString("outcome"))
                    && operation.manualReview.getString("requestId").equals(item.getJSONObject("manualReview").getString("requestId"))) return;
        }
        throw new JSONException("Архив ручной сверки не найден; операция остаётся заблокированной.");
    }

    private RegistrationOperation retireReviewedOperation(RegistrationOperation operation, RegistrationRecovery.Proof proof,
            boolean searchStopped, boolean fullIdNotRegistered, long now) throws Exception {
        return retireReviewedOperation(operation, proof, searchStopped, fullIdNotRegistered, false, now);
    }

    private final class RetirementPlan {
        final RegistrationOperation original, retired;
        final String key;
        final Map<String, Object> previous, changed;
        RetirementPlan(RegistrationOperation original, RegistrationOperation retired, String key,
                       Map<String, Object> previous, Map<String, Object> changed) {
            this.original = original;this.retired = retired;this.key = key;this.previous = previous;this.changed = changed;
        }
        void persist() throws Exception {
            try { DurablePreferenceChange.commit(preferenceStore(bridgePrefs), previous, changed); }
            catch (DurablePreferenceChange.Failure error) {
                if (!error.rollbackSucceeded) retirementStorageUncertain.add(key);
                throw error;
            }
        }
        RegistrationOperation publish() {
            original.recoveryProof = null;
            operations.put(key, retired);
            return retired;
        }
    }

    private RegistrationOperation retireReviewedOperation(RegistrationOperation operation, RegistrationRecovery.Proof proof,
            boolean searchStopped, boolean fullIdVerified, boolean registered, long now) throws Exception {
        RetirementPlan plan = prepareRetirement(operation, proof, searchStopped, fullIdVerified, registered, now);
        plan.persist();
        return plan.publish();
    }

    private RetirementPlan prepareRetirement(RegistrationOperation operation, RegistrationRecovery.Proof proof,
            boolean searchStopped, boolean fullIdVerified, boolean registered, long now) throws Exception {
        if (proof == null || operation.registrationConfirmed || operation.recoveryProof != proof || !operation.canRecover()
                || !isCurrentOperation(operation, proof.generation) || operationsInFlight.contains(operation.operationId)
                || !proof.canReview(operation.identity(), operation.generation, now) || !searchStopped || !fullIdVerified)
            throw new IOException("Сверка устарела или не завершена. Повторите «Сверить незавершённую операцию» и проверьте оба условия.");
        RegistrationOperation retired = RegistrationOperation.fromStored(operation.toStored());
        retired.state = "unknown";
        retired.uncertain = true;
        retired.terminal = false;
        retired.registrationConfirmed = false;
        retired.confirmedHardwareId = "";
        retired.registrationEvidence = "";
        retired.cancelQueued = false;
        retired.localResolved = true;
        retired.localResolvedAt = now;
        retired.generation++;
        retired.manualReview = new JSONObject().put("outcome", registered
                        ? "registered_verified_by_user" : "not_registered_verified_by_user")
                .put("source", "manual_review_after_readonly_recovery").put("requestId", proof.requestId)
                .put("reviewedAt", now).put("recoveryReceivedAt", proof.receivedAt)
                .put("searchStopped", true).put("fullIdNotRegistered", !registered).put("fullIdRegistered", registered);
        retired.message = "Ожидание на телефоне завершено после ручной сверки. Исходный результат операции остался неизвестным.";
        retired.deliveryMessage = registered
                ? "Поиск остановлен; регистрация полного ID на указанной панели проверена пользователем. "
                        + "Автоматического подтверждения нет. Повторная регистрация этой записи недоступна; проект можно синхронизировать с Windows."
                : "Поиск остановлен; отсутствие регистрации полного ID проверено пользователем. Отмена в оборудовании не подтверждалась.";
        String key = operationPreferenceKey(operation.projectFingerprint, operation.recordId);
        String historyKey = operationHistoryKey(operation);
        String original = bridgePrefs.getString(key, "");
        String oldHistory = bridgePrefs.getString(historyKey, "");
        JSONArray history = oldHistory.isEmpty() ? new JSONArray() : new JSONArray(oldHistory);
        history.put(RegistrationRecovery.safeSummary(retired.toStored()));
        Map<String, Object> previous = new HashMap<>(), changes = new HashMap<>();
        previous.put(key, bridgePrefs.getAll().get(key));previous.put(historyKey, bridgePrefs.getAll().get(historyKey));
        changes.put(key, retired.toStored().toString());changes.put(historyKey, history.toString());
        return new RetirementPlan(operation, retired, key, previous, changes);
    }

    private void applyMissingHistory(RegistrationOperation operation, JSONObject response) {
        if (operation.registrationConfirmed) {
            operation.bridgeHistoryMissing = true;
            operation.deliveryMessage = "Windows-мост не нашёл операцию в своём журнале. Подтверждённая регистрация сохранена на телефоне. "
                    + "Если проект Windows не сохранён, нажмите «Сохранить результат в Windows». Повторная регистрация не запускается.";
            return;
        }
        operation.state = "unknown";
        operation.terminal = false;
        operation.registrationConfirmed = false;
        operation.confirmedHardwareId = "";
        operation.registrationEvidence = "";
        operation.uncertain = true;
        operation.bridgeHistoryMissing = true;
        operation.cancelQueued = false;
        operation.message = response.optString("message", "Операция не найдена в журнале Windows-моста.");
        operation.deliveryMessage = "Автоматическая проверка остановлена. Отсутствие записи не подтверждает отмену или результат в оборудовании. "
                + "Нажмите «Сверить незавершённую операцию».";
    }

    private void applyOperationAnswer(RegistrationOperation operation, JSONObject answer) throws Exception {
        if (!operation.operationId.equals(answer.getString("operationId"))
                || !operation.panelId.equals(answer.getString("panelId"))
                || !operation.recordId.equalsIgnoreCase(answer.getString("recordId"))
                || !operation.expectedHardwareId.equalsIgnoreCase(answer.getString("expectedHardwareId"))) {
            throw new IOException("Получен статус другого устройства или другой операции");
        }
        if (answer.has("projectFingerprint") && !operation.projectFingerprint.equals(answer.getString("projectFingerprint")))
            throw new IOException("Получен статус другого проекта");
        String state = answer.getString("state");
        if (!RegistrationOperation.validState(state)) throw new IOException("Неизвестное состояние регистрации");
        long updated = answer.optLong("updatedAt", 0);
        if (updated > 0 && operation.updatedAt > updated) return;
        boolean confirmed = ProtocolV3.confirmsRegistration(operation.operationId, operation.panelId,
                operation.recordId, operation.expectedHardwareId, answer.getString("operationId"),
                answer.getString("panelId"), answer.getString("recordId"), answer.optString("confirmedHardwareId", ""),
                state, answer.optBoolean("registrationConfirmed", false));
        if (operation.registrationConfirmed && !confirmed)
            throw new IOException("Windows не вернул прежнее подтверждение регистрации. Оно сохранено на телефоне; "
                    + "сохранение проекта Windows требует проверки.");
        operation.lastReplyAt = System.currentTimeMillis();
        operation.pollFailures = 0;
        operation.bridgeHistoryMissing = false;
        operation.state = "registered".equals(state) && !confirmed ? "unknown" : state;
        operation.terminal = answer.optBoolean("terminal", false);
        operation.registrationConfirmed = confirmed;
        operation.confirmedHardwareId = confirmed ? answer.getString("confirmedHardwareId").toUpperCase(Locale.ROOT) : "";
        operation.registrationEvidence = answer.optString("registrationEvidence", "");
        operation.projectSaved = answer.optBoolean("projectSaved", false);
        operation.updatedAt = updated;
        operation.expiresAt = answer.optLong("expiresAt", operation.expiresAt);
        operation.message = answer.optString("message", "");
        operation.uncertain = "unknown".equals(operation.state);
        operation.deliveryMessage = operation.uncertain
                ? "Результат регистрации пока не подтверждён. Проверка сохранённой операции продолжается." : "";
        if (operation.terminal && !operation.uncertain) operation.cancelQueued = false;
    }

    private void recordConfirmedRegistration(RegistrationOperation operation) {
        if (!isCurrentOperation(operation, operation.generation) || !operation.projectFingerprint.equals(projectFingerprint)
                || !operation.registrationConfirmed
                || !operation.expectedHardwareId.equalsIgnoreCase(operation.confirmedHardwareId)) return;
        Device device = findById(operation.recordId);
        if (device == null || !operation.expectedHardwareId.equalsIgnoreCase(getSerial(device))) return;
        prefs.edit().putInt(key("stage", device), Math.max(2, getStage(device)))
                .putLong(key("updated", device), System.currentTimeMillis()).apply();
        refreshDevice(device);
    }

    private void refreshActiveRegistrationCard(RegistrationOperation operation) {
        if (activeCard != null && operation.projectFingerprint.equals(projectFingerprint)
                && operation.recordId.equalsIgnoreCase(activeCard.device.id)) refreshRegistrationCard(activeCard);
    }

    private void refreshRegistrationCard(DeviceCard card) {
        if (activeCard != card || card.dialog == null) return;
        RegistrationOperation operation = operationFor(card.device);
        boolean unresolved = operation != null && operation.needsResolution();
        boolean importedUnresolved = operation == null && importedRegistrationNeedsResolution(card.device);
        boolean manuallyRegistered = manualRegistrationBlocksStart(card.device);
        boolean corrupt = operation == null && !bridgePrefs.getString(
                operationPreferenceKey(projectFingerprint, card.device.id), "").isEmpty();
        PanelProfile panel = selectedPanel(card);
        boolean journalConflict = !journalConflict(card.device).isEmpty();
        card.begin.setEnabled(!projectStateUncertain && !card.loadingPanels && !unresolved && !importedUnresolved && !manuallyRegistered && !corrupt && !journalConflict && getStage(card.device) < 2
                && (operation == null || !operation.registrationConfirmed)
                && card.config != null && card.projectMatches && panel != null && panel.available);
        card.next.setVisibility(mayAdvance(card) ? View.VISIBLE : View.GONE);
        card.next.setEnabled(mayAdvance(card));
        updateCardTiming(card);
        card.begin.setText(manuallyRegistered ? "Зарегистрировано по ручной сверке" : journalConflict ? "Сначала сверьте журнал с Windows"
                : importedUnresolved || (operation != null && operation.canRecover()) ? "Требуется сверка результата"
                : getStage(card.device) >= 2 ? "Устройство уже инициализировано"
                : operation != null ? operation.actionLabel() : "Начать регистрацию на панели");
        if (card.journalHint != null) {
            card.journalHint.setText(journalConflict(card.device));
            card.journalHint.setVisibility(journalConflict ? View.VISIBLE : View.GONE);
            card.reviewJournal.setVisibility(journalConflict ? View.VISIBLE : View.GONE);
            card.reviewJournal.setEnabled(!unresolved && !importedUnresolved && !corrupt);
        }
        card.panelSpinner.setEnabled(!unresolved && !corrupt && !card.loadingPanels);
        card.refreshPanels.setEnabled(!card.loadingPanels);
        card.refreshPanels.setText(card.loadingPanels ? "Получение панели…" : "Обновить панель из проекта Windows");
        if (!card.loadingPanels && !card.panelListMessage.isEmpty()) {
            card.panelHint.setText(panel != null && !panel.available && !panel.availableReason.isEmpty()
                    ? "Панель недоступна: " + panel.availableReason : card.panelListMessage);
        }
        card.serial.setEnabled(!unresolved && !corrupt && !importedUnresolved);
        card.deviceKey.setEnabled(!unresolved && !corrupt && !importedUnresolved);
        card.scanQr.setEnabled(!unresolved && !corrupt && !importedUnresolved);
        card.stage.setEnabled(!unresolved && !corrupt && !importedUnresolved);
        if (operation != null && operation.registrationConfirmed) card.stage.setSelection(getStage(card.device));
        card.cancel.setVisibility(unresolved && !operation.bridgeHistoryMissing ? View.VISIBLE : View.GONE);
        card.cancel.setEnabled(unresolved && !operation.cancelQueued && !"cancelling".equals(operation.state));
        card.checkStatus.setVisibility(operation != null && !operation.localResolved ? View.VISIBLE : View.GONE);
        card.checkStatus.setEnabled(operation != null && !operation.localResolved && !operationsInFlight.contains(operation.operationId));
        card.recover.setText(operation != null && operation.needsWindowsSave()
                ? "Сохранить результат в Windows" : "Сверить незавершённую операцию");
        card.recover.setVisibility(importedUnresolved || (operation != null && (operation.canRecover() || operation.needsWindowsSave())) ? View.VISIBLE : View.GONE);
        card.recover.setEnabled(!corrupt && !card.loadingPanels
                && (operation == null || !operationsInFlight.contains(operation.operationId)));
        if (corrupt) {
            card.status.setText("Сохранённую операцию не удалось прочитать. Проверьте результат в Windows; повторный запуск заблокирован.");
        } else if (importedUnresolved) {
            card.status.setText("В перенесённом журнале есть незавершённая операция. Повторная регистрация заблокирована. "
                    + "Обновите панель из проекта Windows и нажмите «Сверить незавершённую операцию». "
                    + "Кнопка «Закрыть» закрывает карточку и не отменяет операцию.");
        } else if (operation == null && manuallyRegistered) {
            card.status.setText("В перенесённой истории сохранена ручная сверка: полный ID зарегистрирован на указанной панели. "
                    + "Автоматическое подтверждение операции не получено. Повторная регистрация этой записи недоступна; "
                    + "синхронизация проекта Windows разрешена после закрытия карточки.");
        } else if (operation != null) {
            String status = operation.manuallyRegistered() ? "Зарегистрировано по ручной сверке"
                    : operation.localResolved ? "Ожидание на телефоне завершено после сверки"
                    : operation.registrationConfirmed ? (operation.projectSaved
                    ? "Зарегистрировано на панели · сохранено в Windows"
                    : "Зарегистрировано на панели · ожидает сохранения в Windows")
                    : operation.canRecover() ? "Требуется сверка результата" : registrationStateLabel(operation.state);
            if (!operation.message.isEmpty()) status += "\n" + operation.message;
            if (!operation.deliveryMessage.isEmpty()) status += "\n" + operation.deliveryMessage;
            if ("waiting_for_sensor".equals(operation.state)) status += "\nНажмите физическую кнопку ПРОГ на датчике с указанным ID.";
            if (operation.registrationConfirmed && !operation.projectSaved)
                status += "\nПанель подтвердила регистрацию. Сохранение проекта в Windows ещё не подтверждено.";
            status += "\nПанель: " + operation.panelLabel + "\nID: " + operation.expectedHardwareId
                    + "\nWindows: " + operation.host + ":" + operation.port
                    + "\nОперация: " + operation.operationId;
            card.status.setText(status);
            card.status.setTextColor(operation.registrationConfirmed ? color("success") : color("text_secondary"));
        }
    }

    private String registrationStateLabel(String state) {
        switch (state) {
            case "connecting": return "Подключение к панели";
            case "waiting_for_sensor": return "Ожидание датчика";
            case "detected": return "Датчик обнаружен";
            case "registering": return "Регистрация датчика на панели";
            case "registered": return "Регистрация подтверждена";
            case "cancelling": return "Отмена регистрации";
            case "cancelled": return "Регистрация отменена";
            case "timed_out": return "Время ожидания датчика истекло";
            case "failed": return "Регистрация не завершена";
            default: return "Результат регистрации неизвестен";
        }
    }

    private String bridgeError(Exception error) {
        if (error instanceof java.net.SocketTimeoutException) return "Windows-мост не ответил вовремя. Проверьте VPN и связь с компьютером.";
        if (error instanceof java.net.ConnectException) return "Нет соединения с Windows-мостом. Проверьте сеть и приём APK.";
        if (error instanceof JSONException) return "Windows-мост вернул ответ неподдерживаемого формата.";
        String message = error.getMessage();
        return message == null || message.isEmpty() ? "Ошибка связи с Windows-мостом." : message;
    }

    private static final class BridgeConfig {
        final String host;
        final int port;
        final String pairingCode;
        BridgeConfig(String host, int port, String pairingCode) {
            BridgeEndpoint endpoint = BridgeEndpoint.parse(host, port);
            this.host = endpoint.address;
            this.port = endpoint.port;
            this.pairingCode = pairingCode;
        }
        boolean sameEndpointAndCode(BridgeConfig other) {
            return host.equals(other.host) && port == other.port && pairingCode.equals(other.pairingCode);
        }
    }

    private static final class PanelProfile {
        final String panelId;
        final String projectPanelId;
        final Set<String> recordIds = new HashSet<>();
        final String name;
        final String host;
        final int port;
        final String moduleId;
        final boolean available;
        final String availableReason;
        final Map<String, String> routeMetadata = new HashMap<>();
        final boolean viaPanelTerminal;
        PanelProfile(JSONObject value, boolean liveCatalogue, String recordId) throws JSONException {
            panelId = value.getString("panelId");
            if (panelId.isEmpty()) throw new JSONException("Профиль без panelId");
            projectPanelId = ProjectPanelIndex.normalized(value.optString("projectPanelId", ""));
            JSONArray members = value.optJSONArray("recordIds");
            if (members != null) for (int i = 0; i < members.length(); i++)
                recordIds.add(ProjectPanelIndex.normalized(members.getString(i)));
            name = value.optString("name", panelId);
            host = value.optString("host", "");
            port = value.optInt("port", 6344);
            moduleId = value.optString("moduleId", "");
            for (String field : ProjectPanelIndex.ROUTE_FIELDS)
                if (value.has(field)) routeMetadata.put(field, value.optString(field, ""));
            viaPanelTerminal = value.optBoolean("viaPanelTerminal", false);
            JSONArray verified = value.optJSONArray("verifiedRecordIds");
            boolean recordVerified = !value.has("verifiedRecordIds");
            if (verified != null) for (int i = 0; i < verified.length(); i++)
                if (ProjectPanelIndex.normalized(recordId).equals(ProjectPanelIndex.normalized(verified.getString(i))))
                    recordVerified = true;
            available = liveCatalogue && recordVerified && !projectPanelId.isEmpty() && value.optBoolean("available", false);
            String reason = value.optString("availableReason", "");
            availableReason = !recordVerified && reason.isEmpty()
                    ? "Принадлежность выбранного датчика ещё не подтверждена в открытом проекте Стрелец-Мастера." : reason;
        }
        String label() {
            String terminal = ProjectPanelIndex.terminalRouteLabel(routeMetadata, viaPanelTerminal);
            return name + (terminal.isEmpty() ? "" : " · " + terminal)
                    + (host.isEmpty() || port <= 0 ? " · IP-маршрут не указан" : " · " + BridgeEndpoint.display(host, port))
                    + (moduleId.isEmpty() ? "" : " · " + moduleId)
                    + (projectPanelId.isEmpty() ? "" : " · " + projectPanelId.substring(0, Math.min(8, projectPanelId.length())))
                    + (available ? "" : " · недоступна");
        }
    }

    private static final class DeviceCard {
        final Device device;
        final List<PanelProfile> panels = new ArrayList<>();
        Spinner stage;
        Spinner panelSpinner;
        EditText serial;
        EditText deviceKey;
        EditText note;
        EditText description;
        String originalDescription;
        TextView journalHint;
        Button reviewJournal;
        Button scanQr;
        Button begin;
        Button cancel;
        Button checkStatus;
        Button recover;
        Button next;
        TextView timing;
        Runnable clock;
        Runnable afterSave;
        Button refreshPanels;
        TextView status;
        TextView panelHint;
        AlertDialog dialog;
        BridgeConfig config;
        boolean loadingPanels;
        boolean projectMatches = true;
        String panelListMessage = "";
        DeviceCard(Device device) { this.device = device; }
    }

    static final class RegistrationOperation {
        String operationId;
        String panelId;
        String panelLabel;
        String recordId;
        String expectedHardwareId;
        String confirmedHardwareId = "";
        String registrationEvidence = "";
        String projectFingerprint;
        String host;
        int port;
        String pairingCode;
        String state = "connecting";
        String message = "";
        String deliveryMessage = "";
        long startedAt;
        long updatedAt;
        long expiresAt;
        long lastReplyAt;
        int pollFailures;
        boolean terminal;
        boolean registrationConfirmed;
        boolean projectSaved;
        boolean uncertain;
        boolean cancelQueued;
        boolean cancelRequested;
        boolean bridgeHistoryMissing;
        boolean localResolved;
        long localResolvedAt;
        long generation = 1;
        String connectionSource = "original_operation";
        JSONObject manualReview;
        RegistrationRecovery.Proof recoveryProof;

        boolean needsResolution() { return !localResolved && (!terminal || uncertain || "unknown".equals(state) || cancelQueued); }
        boolean shouldAutoPoll() { return needsResolution() && !bridgeHistoryMissing && recoveryProof == null; }
        boolean canRecover() { return !localResolved && !registrationConfirmed && (bridgeHistoryMissing || uncertain || "unknown".equals(state)); }
        boolean needsWindowsSave() { return !localResolved && registrationConfirmed && "registered".equals(state) && !projectSaved; }
        boolean manuallyRegistered() { return localResolved && RegistrationRecovery.validManualReview(manualReview, localResolvedAt)
                && "registered_verified_by_user".equals(manualReview.optString("outcome")); }
        String actionLabel() { return manuallyRegistered() ? "Зарегистрировано по ручной сверке"
                : registrationConfirmed ? "Устройство уже инициализировано" : canRecover() ? "Требуется сверка результата"
                : needsResolution() ? "Регистрация выполняется" : "Начать регистрацию на панели"; }
        RegistrationRecovery.Identity identity() throws JSONException {
            return new RegistrationRecovery.Identity(operationId, projectFingerprint, recordId, expectedHardwareId, panelId);
        }

        static boolean validState(String state) {
            return "connecting".equals(state) || "waiting_for_sensor".equals(state) || "detected".equals(state)
                    || "registering".equals(state) || "registered".equals(state) || "cancelling".equals(state)
                    || "cancelled".equals(state) || "failed".equals(state) || "timed_out".equals(state)
                    || "unknown".equals(state);
        }

        JSONObject toStored() throws JSONException {
            JSONObject value = new JSONObject();
            value.put("operationId", operationId).put("panelId", panelId).put("panelLabel", panelLabel)
                    .put("recordId", recordId).put("expectedHardwareId", expectedHardwareId)
                    .put("projectFingerprint", projectFingerprint).put("host", host).put("port", port)
                    .put("pairingCode", pairingCode).put("state", state).put("message", message)
                    .put("deliveryMessage", deliveryMessage).put("startedAt", startedAt)
                    .put("updatedAt", updatedAt).put("expiresAt", expiresAt).put("lastReplyAt", lastReplyAt).put("terminal", terminal)
                    .put("registrationConfirmed", registrationConfirmed).put("projectSaved", projectSaved)
                    .put("confirmedHardwareId", confirmedHardwareId).put("registrationEvidence", registrationEvidence)
                    .put("uncertain", uncertain).put("cancelQueued", cancelQueued).put("cancelRequested", cancelRequested)
                    .put("bridgeHistoryMissing", bridgeHistoryMissing).put("localResolved", localResolved)
                    .put("localResolvedAt", localResolvedAt).put("generation", generation)
                    .put("connectionSource", connectionSource).put("manualReview", manualReview);
            return value;
        }

        static RegistrationOperation fromStored(JSONObject value) throws JSONException {
            RegistrationOperation result = new RegistrationOperation();
            result.operationId = value.getString("operationId");
            UUID.fromString(result.operationId);
            result.panelId = value.getString("panelId");
            result.panelLabel = value.optString("panelLabel", result.panelId);
            result.recordId = value.getString("recordId");
            result.expectedHardwareId = value.getString("expectedHardwareId");
            result.projectFingerprint = value.getString("projectFingerprint");
            result.host = value.getString("host");
            result.port = value.getInt("port");
            String code = value.getString("pairingCode");
            result.pairingCode = code.isEmpty() ? "" : ProtocolV3.normalizePairingCode(code);
            result.state = value.getString("state");
            if (result.panelId.isEmpty() || result.recordId.isEmpty() || result.host.isEmpty()
                    || result.port < 1 || result.port > 65535 || !validState(result.state)
                    || !result.expectedHardwareId.matches("[0-9A-F]{12}")
                    || !result.projectFingerprint.matches("[0-9a-f]{64}")) throw new JSONException("Операция повреждена");
            result.message = value.optString("message", "");
            result.deliveryMessage = value.optString("deliveryMessage", "");
            result.startedAt = value.optLong("startedAt", 0);
            result.updatedAt = value.optLong("updatedAt", 0);
            result.expiresAt = value.optLong("expiresAt", 0);
            result.lastReplyAt = value.optLong("lastReplyAt", 0);
            result.terminal = value.optBoolean("terminal", false);
            result.registrationConfirmed = value.optBoolean("registrationConfirmed", false);
            result.confirmedHardwareId = value.optString("confirmedHardwareId", "");
            result.registrationEvidence = value.optString("registrationEvidence", "");
            if ((result.registrationConfirmed && (!"registered".equals(result.state)
                    || !result.expectedHardwareId.equalsIgnoreCase(result.confirmedHardwareId)))
                    || ("registered".equals(result.state) && !result.registrationConfirmed)) {
                result.registrationConfirmed = false;
                result.state = "unknown";
            }
            result.projectSaved = value.optBoolean("projectSaved", false);
            result.uncertain = value.optBoolean("uncertain", false);
            result.cancelQueued = value.optBoolean("cancelQueued", false);
            result.cancelRequested = value.optBoolean("cancelRequested", false);
            result.bridgeHistoryMissing = value.optBoolean("bridgeHistoryMissing", false);
            result.localResolved = value.optBoolean("localResolved", false);
            result.localResolvedAt = value.optLong("localResolvedAt", 0);
            result.generation = value.optLong("generation", 1);
            result.connectionSource = value.optString("connectionSource", "original_operation");
            result.manualReview = value.optJSONObject("manualReview");
            if (result.generation < 1 || (result.pairingCode.isEmpty() && !"imported_original_endpoint".equals(result.connectionSource)
                    && !"user_selected_endpoint_for_imported_history".equals(result.connectionSource)))
                throw new JSONException("Операция повреждена");
            if (result.localResolved && (!"unknown".equals(result.state) || result.registrationConfirmed
                    || !result.uncertain || result.terminal || result.cancelQueued || result.localResolvedAt <= 0
                    || !RegistrationRecovery.validManualReview(result.manualReview, result.localResolvedAt)))
                throw new JSONException("Неполная запись ручной сверки");
            return result;
        }
    }


    private boolean featureBusy() {return !batchFingerprint.isEmpty()||!planFingerprint.isEmpty()||(featureDialog!=null&&featureDialog.isShowing());}
    private void showFeature(AlertDialog dialog) {featureDialog=dialog;dialog.setOnDismissListener(d->{if(featureDialog==dialog)featureDialog=null;});dialog.show();}
    private String fieldworkRaw() {return prefs.getString(Fieldwork.key(projectFingerprint),projectRoot.optJSONObject("fieldwork")==null?"":projectRoot.optJSONObject("fieldwork").toString());}
    private void withFieldwork(java.util.function.Consumer<JSONObject> ready) {
        String raw=fieldworkRaw(),scope=projectFingerprint;JSONObject[] result={null};
        try{runFileWork("Чтение полевых данных",()->result[0]=Fieldwork.read(raw),error->{if(error!=null)reportManualFileResult(error,"");else if(scope.equals(projectFingerprint))ready.accept(result[0]);});}catch(Exception e){reportManualFileResult(e,"");}
    }
    private interface FieldEdit {void edit(JSONObject state)throws Exception;}
    private void changeFieldwork(FieldEdit edit,Runnable done) {
        String scope=projectFingerprint,storageKey=Fieldwork.key(scope),raw=fieldworkRaw();
        Map<String,Object> expected=new HashMap<>();expected.put(storageKey,prefs.getAll().get(storageKey));
        try{runFileWork("Сохранение полевых данных",()->{
            JSONObject state=Fieldwork.read(raw);edit.edit(state);Fieldwork.read(state.toString());
            Map<String,Object> changes=new HashMap<>();changes.put(storageKey,state.toString());
            DurablePreferenceChange.commit(preferenceStore(prefs),expected,changes);
        },error->{if(error!=null)reportManualFileResult(error,"");else if(scope.equals(projectFingerprint))done.run();});}catch(Exception e){reportManualFileResult(e,"");}
    }
    private void showBatch() {withFieldwork(this::showBatchState);}
    private void showBatchState(JSONObject state) {
        JSONArray scans=state.optJSONArray("scans");List<String> labels=new ArrayList<>();
        for(int i=0;i<scans.length();i++){JSONObject row=scans.optJSONObject(i);Device match=findBySerial(row.optString("id"),"");Device assigned=findById(row.optString("recordId"));
            labels.add(row.optString("id")+(match!=null?" · уже в проекте: "+getDisplayName(match):assigned!=null?" · выбрано: "+getDisplayName(assigned):" · выбрать устройство"));}
        LinearLayout form=new LinearLayout(this);form.setOrientation(LinearLayout.VERTICAL);form.setPadding(dp(16),dp(8),dp(16),0);
        form.addView(text("Сканирование сохраняет список. Инициализация запускается отдельно из карточки после проверки панели.",13,color("text_secondary"),false));
        Button scan=menuButton("Сканировать следующий QR");form.addView(scan);ListView list=new ListView(this);form.addView(list,new LinearLayout.LayoutParams(-1,dp(330)));list.setAdapter(new ArrayAdapter<>(this,android.R.layout.simple_list_item_1,labels));
        AlertDialog dialog=new AlertDialog.Builder(this).setTitle("Список QR · "+scans.length()).setView(form).setNegativeButton("Закрыть",null).create();
        scan.setOnClickListener(v->{batchFingerprint=projectFingerprint;dialog.dismiss();startActivityForResult(new Intent(this,ScannerActivity.class),SCAN_BATCH);});
        list.setOnItemClickListener((a,v,pos,id)->{dialog.dismiss();JSONObject row=scans.optJSONObject(pos);String serial=row.optString("id");
            AlertDialog options=new AlertDialog.Builder(this).setTitle(serial).setItems(new String[]{"Выбрать устройство и открыть карточку","Удалить из списка"},(d,index)->{
                if(index==1)changeFieldwork(data->{JSONArray items=data.getJSONArray("scans");for(int n=items.length()-1;n>=0;n--)if(serial.equals(items.getJSONObject(n).optString("id")))items.remove(n);},this::showBatch);
                else chooseDevice(device->{Device duplicate=findBySerial(serial,device.id);if(duplicate!=null){toast("ID уже записан у «"+getDisplayName(duplicate)+"». Выберите его карточку.");showBatch();return;}
                    changeFieldwork(data->{JSONArray items=data.getJSONArray("scans");for(int n=0;n<items.length();n++)if(serial.equals(items.getJSONObject(n).optString("id")))items.getJSONObject(n).put("recordId",device.id);},()->{
                        editDevice(device);if(activeCard!=null&&activeCard.device==device){activeCard.serial.setText(serial);if(!row.optString("key").isEmpty())activeCard.deviceKey.setText(row.optString("key"));}
                    });});
            }).setNegativeButton("Назад",(d,w)->showBatch()).create();showFeature(options);
        });showFeature(dialog);
    }
    private void acceptBatchQr(String raw) {
        QrValues qr=parseDeviceQr(raw);boolean[] added={false};
        changeFieldwork(state->added[0]=Fieldwork.addScan(state,qr.id,qr.deviceKey,System.currentTimeMillis()),()->{
            Device match=findBySerial(qr.id,"");toast(!added[0]?"ID уже есть в списке":match!=null?"Сохранено. Этот ID уже записан в проекте":"QR сохранён в список");showBatch();
        });
    }
    private void chooseDevice(java.util.function.Consumer<Device> picked) {
        LinearLayout form=new LinearLayout(this);form.setOrientation(LinearLayout.VERTICAL);form.setPadding(dp(16),0,dp(16),0);
        EditText query=new EditText(this);query.setHint("Поиск: описание, ID, зона");form.addView(query);
        ListView list=new ListView(this);form.addView(list,new LinearLayout.LayoutParams(-1,dp(360)));List<Device> choices=new ArrayList<>();List<String> labels=new ArrayList<>();ArrayAdapter<String> adapter=new ArrayAdapter<>(this,android.R.layout.simple_list_item_1,labels);list.setAdapter(adapter);
        Runnable filter=()->{String q=query.getText().toString().toLowerCase(Locale.ROOT);choices.clear();labels.clear();for(Device d:devices)if((getDisplayName(d)+" "+getSerial(d)+" "+d.zone+" "+d.name).toLowerCase(Locale.ROOT).contains(q)){choices.add(d);labels.add(getDisplayName(d)+" · "+d.zone+"\n"+projectPanelLabel(d));}adapter.notifyDataSetChanged();};
        query.addTextChangedListener(new TextWatcher(){public void beforeTextChanged(CharSequence s,int a,int c,int n){}public void onTextChanged(CharSequence s,int a,int b,int c){filter.run();}public void afterTextChanged(Editable e){}});filter.run();
        AlertDialog dialog=new AlertDialog.Builder(this).setTitle("Выберите устройство проекта").setView(form).setNegativeButton("Отмена",null).create();list.setOnItemClickListener((a,v,pos,id)->{Device choice=choices.get(pos);dialog.dismiss();picked.accept(choice);});showFeature(dialog);
    }
    private void chooseWorkReport(boolean pdf) {
        Intent intent=new Intent(Intent.ACTION_CREATE_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE)
            .setType(pdf?"application/pdf":"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        intent.putExtra(Intent.EXTRA_TITLE,fileStamp("streletz-work",pdf?".pdf":".xlsx"));startActivityForResult(intent,pdf?EXPORT_PDF:EXPORT_XLSX);
    }
    private void checkConnection() {
        if(!bridgeConfigured()){showBridgeSettings(this::checkConnection);return;}
        ensureLocalNetworkPermission(()->{
            BridgeConfig config=bridgeConfig();String scope=projectFingerprint,panel=selectedPanelFilter();
            TextView details=text("Проверяем Windows-мост, проект и Master…",14,color("text_primary"),false);details.setPadding(dp(20),dp(12),dp(20),dp(12));ScrollView scroll=new ScrollView(this);scroll.addView(details);
            BridgeTransport.Exchange exchange=new BridgeTransport.Exchange();
            AlertDialog dialog=new AlertDialog.Builder(this).setTitle("Проверка подключения").setView(scroll).setNegativeButton("Закрыть",null).create();
            featureDialog=dialog;dialog.setOnDismissListener(d->{exchange.abort();if(featureDialog==dialog)featureDialog=null;});dialog.show();
            bridgeExecutor.execute(()->{String message;try{JSONObject request=newBridgeRequest("check_connection",UUID.randomUUID().toString()).put("projectFingerprint",scope);message=ConnectionCheck.describe(callBridge(config,request,exchange),scope,"?".equals(panel)?"":panel);}catch(Exception e){message=ConnectionCheck.failure(e);}
                String result=message;onUiWhenStorageReady(()->{if(dialog.isShowing()&&scope.equals(projectFingerprint)&&config.sameEndpointAndCode(bridgeConfig()))details.setText(result);});
            });
        });
    }
    private void previewProject(ProjectReader reader,String source,Runnable done)throws Exception {
        if (waitingForProject) {
            openProjectAsync(reader, source, true, error -> {
                if (error != null) reportManualFileResult(error, "");
                else { resumeRegistrations(); done.run(); }
            });
            return;
        }
        requireIdleProject();if(previewOpen)return;previewOpen=true;
        ExportSnapshot snapshot=captureExport();JSONObject base=projectRoot;String scope=projectFingerprint;ImportPreview[] prepared={null};
        try{runFileWork("Сравнение проектов",()->prepared[0]=new ImportPreview(ProjectDocuments.portable(base,snapshot.build()),reader.read()),error->{
            if(error!=null){previewOpen=false;reportManualFileResult(error,"");return;}
            if(!scope.equals(projectFingerprint)){previewOpen=false;return;}
            // Pausing prevents the next periodic download from silently replacing a declined subset.
            bridgePrefs.edit().putBoolean("auto_project",false).apply();
            ImportPreview preview=prepared[0];LinearLayout form=new LinearLayout(this);form.setOrientation(LinearLayout.VERTICAL);form.setPadding(dp(16),0,dp(16),0);
            form.addView(text((preview.related?"Выберите записи для переноса. Удаления по умолчанию отключены. При расхождении журнала потребуется сверка.":"Нет общих записей: будет открыт другой проект. Текущий проект сохранится в архиве.")+"\nАвтозагрузка приостановлена; её можно включить в меню.",13,color("text_secondary"),false));
            CheckBox catalogue=new CheckBox(this);catalogue.setText("Обновить маршруты панелей и принадлежность неизменённых записей");catalogue.setChecked(true);form.addView(catalogue);
            CheckBox fields=new CheckBox(this);fields.setText("Перенести новые планы и QR (местные записи сохраняются)");fields.setChecked(true);form.addView(fields);
            Set<String> selected=new HashSet<>();List<String> labels=new ArrayList<>();for(ImportPreview.Change c:preview.changes){labels.add(c.label);if(c.after!=null)selected.add(c.id);}
            ListView list=new ListView(this);list.setChoiceMode(ListView.CHOICE_MODE_MULTIPLE);list.setAdapter(new ArrayAdapter<>(this,android.R.layout.simple_list_item_multiple_choice,labels));form.addView(list,new LinearLayout.LayoutParams(-1,dp(360)));
            for(int i=0;i<preview.changes.size();i++)list.setItemChecked(i,selected.contains(preview.changes.get(i).id));
            if(!preview.related)list.setEnabled(false);
            list.setOnItemClickListener((a,v,pos,id)->{String key=preview.changes.get(pos).id;if(list.isItemChecked(pos))selected.add(key);else selected.remove(key);});
            AlertDialog dialog=new AlertDialog.Builder(this).setTitle("Изменения "+source+" · "+preview.changes.size()).setView(form).setNegativeButton("Отмена",null).setPositiveButton("Применить",null).create();featureDialog=dialog;
            dialog.setOnDismissListener(d->{previewOpen=false;if(featureDialog==dialog)featureDialog=null;});dialog.setOnShowListener(d->dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(v->{
                if(!scope.equals(projectFingerprint)){toast("Проект изменился. Выполните сравнение снова.");dialog.dismiss();return;}
                Set<String> accepted=new HashSet<>(selected);boolean includeFields=fields.isChecked(),includeCatalogue=catalogue.isChecked();dialog.dismiss();
                try{openProjectAsync(()->preview.select(accepted,includeFields,includeCatalogue),"выбранные изменения "+source,true,problem->{if(problem!=null)reportManualFileResult(problem,"");else{toast("Сохранено. Устройств в проекте: "+devices.size()+". Конфликты журнала доступны в карточках.");done.run();}});}catch(Exception e){reportManualFileResult(e,"");}
            }));dialog.show();
        });}catch(Exception e){previewOpen=false;throw e;}
    }
    private void showPlans(){withFieldwork(state->{JSONArray plans=state.optJSONArray("plans");String[] names=new String[plans.length()];for(int i=0;i<names.length;i++)names[i]=plans.optJSONObject(i).optString("name","План");
        AlertDialog dialog=new AlertDialog.Builder(this).setTitle("Планы помещений · "+names.length).setItems(names,(d,n)->openPlan(plans.optJSONObject(n).optString("id"))).setPositiveButton("Добавить PNG/JPEG",(d,w)->{
            planFingerprint=projectFingerprint;Intent intent=new Intent(Intent.ACTION_OPEN_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType("image/*");intent.putExtra(Intent.EXTRA_MIME_TYPES,new String[]{"image/png","image/jpeg"});startActivityForResult(intent,IMPORT_PLAN);
        }).setNeutralButton("Удалить план",(d,w)->{
            if(names.length==0){toast("Планы не добавлены.");return;}
            AlertDialog pick=new AlertDialog.Builder(this).setTitle("Какой план удалить?").setItems(names,(choice,n)->{
                String id=plans.optJSONObject(n).optString("id");
                AlertDialog confirm=new AlertDialog.Builder(this).setTitle("Удалить план и отметки?").setMessage("Устройства останутся в проекте.")
                    .setNegativeButton("Отмена",null).setPositiveButton("Удалить",(c,v)->changeFieldwork(data->{JSONArray items=data.getJSONArray("plans");for(int i=items.length()-1;i>=0;i--)if(id.equals(items.getJSONObject(i).optString("id")))items.remove(i);},this::showPlans)).create();showFeature(confirm);
            }).setNegativeButton("Отмена",null).create();showFeature(pick);
        }).setNegativeButton("Закрыть",null).create();showFeature(dialog);
    });}
    private void importPlan(Uri uri)throws Exception {
        EditText name=new EditText(this);name.setHint("Корпус, этаж, помещение");name.setText("План "+new SimpleDateFormat("dd.MM HH:mm",Locale.ROOT).format(new Date()));
        AlertDialog dialog=new AlertDialog.Builder(this).setTitle("Название плана").setView(name).setNegativeButton("Отмена",null).setPositiveButton("Добавить",(d,w)->{
            String title=name.getText().toString().trim();if(title.isEmpty()){toast("Укажите название плана.");return;}
            changeFieldwork(state->{JSONArray plans=state.getJSONArray("plans");if(plans.length()>=Fieldwork.MAX_PLANS)throw new IOException("Допускается до 8 планов в проекте.");
                android.graphics.BitmapFactory.Options bounds=new android.graphics.BitmapFactory.Options();bounds.inJustDecodeBounds=true;
                try(InputStream input=getContentResolver().openInputStream(uri)){android.graphics.BitmapFactory.decodeStream(input,null,bounds);}
                if(bounds.outWidth<=0||bounds.outHeight<=0||!("image/jpeg".equals(bounds.outMimeType)||"image/png".equals(bounds.outMimeType)))throw new IOException("Нужен план PNG или JPEG.");
                android.graphics.BitmapFactory.Options options=new android.graphics.BitmapFactory.Options();options.inSampleSize=1;while(Math.max(bounds.outWidth,bounds.outHeight)/options.inSampleSize>1800)options.inSampleSize*=2;
                android.graphics.Bitmap bitmap;try(InputStream input=getContentResolver().openInputStream(uri)){bitmap=android.graphics.BitmapFactory.decodeStream(input,null,options);}if(bitmap==null)throw new IOException("Не удалось прочитать изображение.");
                byte[] bytes;try{ByteArrayOutputStream out=new ByteArrayOutputStream();bitmap.compress(android.graphics.Bitmap.CompressFormat.JPEG,82,out);bytes=out.toByteArray();if(bytes.length>Fieldwork.MAX_IMAGE_BYTES){out.reset();bitmap.compress(android.graphics.Bitmap.CompressFormat.JPEG,55,out);bytes=out.toByteArray();}}finally{bitmap.recycle();}
                if(bytes.length>Fieldwork.MAX_IMAGE_BYTES)throw new IOException("Слишком сложный план: уменьшите изображение (до 800 КБ после обработки).");
                plans.put(new JSONObject().put("id",UUID.randomUUID().toString()).put("name",title).put("image",java.util.Base64.getEncoder().encodeToString(bytes)).put("points",new JSONArray()));
            },this::showPlans);
        }).create();showFeature(dialog);
    }
    private void showDevicePlan(Device device){
        String recordId=device.id;
        withFieldwork(state->{try{
            JSONArray matches=Fieldwork.plansForRecord(state,recordId),all=state.getJSONArray("plans");
            JSONArray choices=matches.length()>0?matches:all;
            if(choices.length()==0){
                AlertDialog empty=new AlertDialog.Builder(this).setTitle("План пока не добавлен")
                    .setMessage("Добавьте PNG/JPEG в меню «Планы помещений» или загрузите проект с планами из Windows.")
                    .setPositiveButton("Планы помещений",(d,w)->showPlans()).setNegativeButton("Закрыть",null).create();showFeature(empty);return;
            }
            if(choices.length()==1){openPlan(choices.getJSONObject(0).getString("id"),recordId,matches.length()==0);return;}
            String[] names=new String[choices.length()];for(int i=0;i<names.length;i++)names[i]=choices.getJSONObject(i).optString("name","План");
            AlertDialog picker=new AlertDialog.Builder(this).setTitle(matches.length()>0?"Планы устройства":"Выберите план для устройства")
                .setItems(names,(d,n)->openPlan(choices.optJSONObject(n).optString("id"),recordId,matches.length()==0)).setNegativeButton("Отмена",null).create();showFeature(picker);
        }catch(Exception e){reportManualFileResult(e,"");}});
    }
    private void openPlan(String planId){openPlan(planId,"",false);}
    private void openPlan(String planId,String focusRecord,boolean beginPlacement){
        String raw=fieldworkRaw(),scope=projectFingerprint;ExportSnapshot snapshot=captureExport();JSONObject[] plan={null};android.graphics.Bitmap[] bitmap={null};Map<String,Integer> states=new HashMap<>();Map<String,String> labels=new HashMap<>();
        try{runFileWork("Открытие плана",()->{
            JSONArray plans=Fieldwork.read(raw).getJSONArray("plans");for(int i=0;i<plans.length();i++)if(planId.equals(plans.getJSONObject(i).optString("id")))plan[0]=plans.getJSONObject(i);if(plan[0]==null)throw new IOException("План не найден.");
            byte[] bytes=java.util.Base64.getDecoder().decode(plan[0].getString("image"));android.graphics.BitmapFactory.Options bounds=new android.graphics.BitmapFactory.Options();bounds.inJustDecodeBounds=true;android.graphics.BitmapFactory.decodeByteArray(bytes,0,bytes.length,bounds);
            if(bounds.outWidth<=0||bounds.outHeight<=0||bounds.outWidth>100000||bounds.outHeight>100000)throw new IOException("Некорректный размер плана.");
            android.graphics.BitmapFactory.Options opts=new android.graphics.BitmapFactory.Options();opts.inSampleSize=1;while(Math.max(bounds.outWidth,bounds.outHeight)/opts.inSampleSize>1800)opts.inSampleSize*=2;
            bitmap[0]=android.graphics.BitmapFactory.decodeByteArray(bytes,0,bytes.length,opts);if(bitmap[0]==null)throw new IOException("Изображение плана повреждено.");
            JSONObject backup=snapshot.build();List<WorkReport.Row> report=WorkReport.rows(backup);JSONArray records=backup.getJSONArray("records");for(int i=0;i<records.length();i++){JSONObject row=records.getJSONObject(i);String id=row.getString("id");labels.put(id,ImportPreview.display(row));states.put(id,report.get(i).complete?2:row.optJSONObject("registration")!=null||row.optInt("stage")>0||!row.optString("serial").isEmpty()?1:0);}
        },error->{if(error!=null){if(bitmap[0]!=null)bitmap[0].recycle();reportManualFileResult(error,"");return;}if(!scope.equals(projectFingerprint)){bitmap[0].recycle();return;}

            LinearLayout form=new LinearLayout(this);form.setOrientation(LinearLayout.VERTICAL);form.setPadding(dp(12),dp(8),dp(12),dp(8));
            LinearLayout heading=new LinearLayout(this);heading.setGravity(Gravity.CENTER_VERTICAL);
            TextView title=text(plan[0].optString("name"),18,color("text_primary"),true);heading.addView(title,new LinearLayout.LayoutParams(0,-2,1));
            Button close=new Button(this);close.setText("Закрыть");heading.addView(close);form.addView(heading);
            TextView hint=text("Два пальца — масштаб · один — перемещение",12,color("text_secondary"),false);form.addView(hint);
            LinearLayout zoomBar=new LinearLayout(this);zoomBar.setGravity(Gravity.CENTER_VERTICAL);
            Button minus=new Button(this);minus.setText("−");Button plus=new Button(this);plus.setText("+");Button fit=new Button(this);fit.setText("Весь план");
            TextView percent=text("100%",13,color("text_primary"),false);percent.setGravity(Gravity.CENTER);
            zoomBar.addView(minus,new LinearLayout.LayoutParams(dp(52),-2));zoomBar.addView(percent,new LinearLayout.LayoutParams(0,-2,1));zoomBar.addView(plus,new LinearLayout.LayoutParams(dp(52),-2));zoomBar.addView(fit);form.addView(zoomBar);
            AlertDialog[] owner={null};String[] placing={beginPlacement?focusRecord:""};FloorPlanView[] canvas={null};
            FloorPlanView view=new FloorPlanView(this,bitmap[0],plan[0].optJSONArray("points"),labels,states,(record,x,y)->{
                if(!scope.equals(projectFingerprint)||!owner[0].isShowing())return;
                if(!placing[0].isEmpty()){
                    if(!record.isEmpty()&&!record.equalsIgnoreCase(placing[0])){toast("Это место занято. Нажмите рядом.");return;}
                    String target=placing[0];if(findById(target)==null){toast("Устройство отсутствует в проекте.");return;}
                    owner[0].dismiss();changeFieldwork(state->Fieldwork.putPoint(state,planId,target,x,y),()->openPlan(planId,target,false));return;
                }
                if(record.isEmpty())return;Device selected=findById(record);if(selected==null){toast("Устройство отсутствует в проекте.");return;}
                boolean cardOpen=activeCard!=null&&activeCard.dialog!=null&&activeCard.dialog.isShowing();
                String[] actions=cardOpen?new String[]{"Переместить отметку","Убрать с плана"}:new String[]{"Открыть карточку","Переместить отметку","Убрать с плана"};
                new AlertDialog.Builder(this).setTitle(getDisplayName(selected)).setItems(actions,(d,n)->{
                    int action=cardOpen?n+1:n;
                    if(action==0){owner[0].dismiss();editDevice(selected);}
                    else if(action==1){placing[0]=record;canvas[0].place(true);hint.setText("Нажмите новое место: "+getDisplayName(selected));}
                    else{owner[0].dismiss();changeFieldwork(state->Fieldwork.removePoint(state,planId,record),()->openPlan(planId,focusRecord,false));}
                }).setNegativeButton("Назад",null).show();
            });canvas[0]=view;
            view.place(beginPlacement);view.focus(focusRecord);if(!focusRecord.isEmpty())hint.setText((beginPlacement?"Нажмите место: ":"Устройство: ")+labels.getOrDefault(focusRecord,focusRecord));
            view.onScaleChanged(()->{percent.setText(view.percent()+"%");minus.setEnabled(view.percent()>100);plus.setEnabled(view.percent()<800);});
            minus.setOnClickListener(v->view.zoom(1/1.5f));plus.setOnClickListener(v->view.zoom(1.5f));fit.setOnClickListener(v->view.fit());
            form.addView(view,new LinearLayout.LayoutParams(-1,0,1));
            form.addView(text("– Не привязано   ? Ожидает проверки   ✓ Подтверждено и сохранено",12,color("text_secondary"),false));
            Button place=new Button(this);place.setAllCaps(false);place.setText(focusRecord.isEmpty()?"Разместить устройство":"Разместить это устройство");form.addView(place);
            place.setOnClickListener(v->{if(!focusRecord.isEmpty()){placing[0]=focusRecord;view.place(true);hint.setText("Нажмите место: "+labels.getOrDefault(focusRecord,focusRecord));}else{
                owner[0].dismiss();chooseDevice(device->openPlan(planId,device.id,true));}});
            Button cancelPlace=new Button(this);cancelPlace.setText("Режим просмотра");cancelPlace.setAllCaps(false);form.addView(cancelPlace);
            cancelPlace.setOnClickListener(v->{placing[0]="";view.place(false);hint.setText("Два пальца — масштаб · один — перемещение");});
            owner[0]=new AlertDialog.Builder(this).create();owner[0].setView(form,0,0,0,0);featureDialog=owner[0];
            close.setOnClickListener(v->owner[0].dismiss());
            owner[0].setOnDismissListener(d->{if(featureDialog==owner[0])featureDialog=null;view.setVisibility(View.GONE);bitmap[0].recycle();});owner[0].show();
            if(owner[0].getWindow()!=null)owner[0].getWindow().setLayout(ViewGroup.LayoutParams.MATCH_PARENT,ViewGroup.LayoutParams.MATCH_PARENT);
        });}catch(Exception e){reportManualFileResult(e,"");}
    }

    private void choosePerformanceDestination() {
        Intent intent = new Intent(Intent.ACTION_CREATE_DOCUMENT);
        intent.addCategory(Intent.CATEGORY_OPENABLE);
        intent.setType("application/json");
        intent.putExtra(Intent.EXTRA_TITLE, fileStamp("streletz-performance", ".json"));
        startActivityForResult(intent, EXPORT_PERFORMANCE);
    }

    private void chooseProjectSource() {
        Intent intent = new Intent(Intent.ACTION_OPEN_DOCUMENT);
        intent.addCategory(Intent.CATEGORY_OPENABLE);
        intent.setType("*/*");
        intent.putExtra(Intent.EXTRA_MIME_TYPES, new String[]{"application/json", "application/octet-stream", "text/plain"});
        startActivityForResult(intent, IMPORT_PROJECT);
    }

    private void chooseProjectDestination() {
        Intent intent = new Intent(Intent.ACTION_CREATE_DOCUMENT);
        intent.addCategory(Intent.CATEGORY_OPENABLE);
        intent.setType("application/json");
        intent.putExtra(Intent.EXTRA_TITLE, fileStamp("streletz-project", ".s3project"));
        startActivityForResult(intent, EXPORT_PROJECT);
    }

    private void chooseCsvDestination() {
        Intent intent = new Intent(Intent.ACTION_CREATE_DOCUMENT);
        intent.addCategory(Intent.CATEGORY_OPENABLE);
        intent.setType("text/csv");
        intent.putExtra(Intent.EXTRA_TITLE, fileStamp("streletz-report", ".csv"));
        startActivityForResult(intent, EXPORT_CSV);
    }

    private void chooseBackupSource() {
        Intent intent = new Intent(Intent.ACTION_OPEN_DOCUMENT);
        intent.addCategory(Intent.CATEGORY_OPENABLE);
        intent.setType("application/json");
        startActivityForResult(intent, IMPORT_BACKUP);
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        pendingFileRequest = requestCode;
        pendingFileResultCode = resultCode;
        pendingFileResult = data;
        hasPendingFileResult = true;
        dispatchPendingFileResult();
    }

    private void handleActivityResult(int requestCode, int resultCode, Intent data) {
        if (requestCode == SCAN_BATCH) {
            String scope=batchFingerprint;batchFingerprint="";
            if(!scope.equals(projectFingerprint)) {toast("Проект изменился. Повторите сканирование в нужном проекте.");return;}
            if(resultCode==RESULT_OK&&data!=null) acceptBatchQr(data.getStringExtra("qr_text"));else showBatch();
            return;
        }
        if (requestCode == SCAN_QR) {
            if (resultCode == RESULT_OK && data != null) handleScannedQr(data.getStringExtra("qr_text"));
            return;
        }
        if (resultCode != RESULT_OK || data == null || data.getData() == null) {if(requestCode==IMPORT_PLAN)planFingerprint="";return;}
        Uri uri = data.getData();
        try {
            if(requestCode==IMPORT_PLAN) {
                String scope=planFingerprint;planFingerprint="";
                if(!scope.equals(projectFingerprint))throw new IOException("Проект изменился. Выберите план снова.");
                importPlan(uri);
            } else if(requestCode==EXPORT_XLSX||requestCode==EXPORT_PDF) {
                ExportSnapshot snapshot=captureExport();
                runFileWork("Подготовка отчёта", () -> {
                    JSONObject backup=snapshot.build();
                    try(OutputStream stream=getContentResolver().openOutputStream(uri,"wt")) {
                        if(stream==null)throw new IOException("Не удалось открыть файл отчёта.");
                        if(requestCode==EXPORT_XLSX)WorkReport.xlsx(stream,backup);else WorkReportPdf.write(stream,backup);
                    }
                }, error -> reportManualFileResult(error,"Отчёт сохранён. Незавершённые устройства выделены отдельно."));
            } else if (requestCode == EXPORT_PERFORMANCE) {
                runFileWork("Сохранение отчёта о скорости", () -> writeUri(uri, PerformanceMetrics.INSTANCE.report().toString(2)),
                        error -> reportManualFileResult(error, "Отчёт о скорости сохранён. В нём только длительности и счётчики за текущий запуск."));
            } else if (requestCode == IMPORT_PROJECT) {
                previewProject(() -> parseProject(readAll(getContentResolver().openInputStream(uri))), "из файла", () -> toast("Выбранные изменения проекта сохранены."));
            } else if (requestCode == IMPORT_BACKUP) {
                importBackupAsync(uri);
            } else if (requestCode == EXPORT_PROJECT || requestCode == EXPORT_BACKUP || requestCode == EXPORT_CSV) {
                ExportSnapshot snapshot = captureExport();
                JSONObject base = projectRoot;
                String label = requestCode == EXPORT_PROJECT ? "Проект для Windows сохранён"
                        : requestCode == EXPORT_CSV ? "Отчёт CSV сохранён" : "Копия журнала сохранена";
                runFileWork("Сохранение файла", () -> {
                    long exportStarted = System.nanoTime();
                    boolean exported = false;
                    try {
                    JSONObject backup = snapshot.build();
                    String text = requestCode == EXPORT_PROJECT ? ProjectDocuments.portable(base, backup).toString(2)
                            : requestCode == EXPORT_CSV ? ProjectDocuments.csv(backup) : backup.toString(2);
                    if(requestCode!=EXPORT_CSV&&text.getBytes(StandardCharsets.UTF_8).length>ProjectTransfer.MAX_DOCUMENT_BYTES)
                        throw new IOException("Файл проекта превышает 16 МБ. Уменьшите число или размер планов.");
                    writeUri(uri, text);
                    exported = true;
                    } finally { PerformanceMetrics.INSTANCE.record(PerformanceMetrics.Task.EXPORT,
                            System.nanoTime() - exportStarted, snapshot.devices.size(), exported); }
                }, error -> reportManualFileResult(error, label));
            }
        } catch (Exception error) { reportManualFileResult(error, ""); }
    }

    private void importBackupAsync(Uri uri) throws Exception {
        requireIdleProject();
        JSONObject previous = projectRoot;
        String fingerprint = projectFingerprint;
        Map<String, ?> oldJournal = new HashMap<>(prefs.getAll()), oldBridge = new HashMap<>(bridgePrefs.getAll());
        ProjectViewState view = new ProjectViewState();
        int[] restored = {0};
        runFileWork("Восстановление журнала", () -> {
            JSONObject backup = new JSONObject(readAll(getContentResolver().openInputStream(uri)));
            String restoredFields=backup.optJSONObject("fieldwork")==null?null:Fieldwork.forProject(Fieldwork.read(backup.getJSONObject("fieldwork").toString()),previous).toString();
            ProjectTransaction.install(new File(getFilesDir(), PROJECT_FILE), previous.toString(), oldJournal, oldBridge, () -> {
                publishOnUi(() -> {
                    if (destroyed) throw new IOException("Экран закрыт; восстановлен предыдущий журнал.");
                    restored[0] = restoreBackup(backup);
                });
                if(restoredFields!=null&&!prefs.edit().putString(Fieldwork.key(fingerprint),restoredFields).commit())throw new IOException("Не удалось восстановить планы и QR.");
                flushProjectPreferences();
            }, (journal, bridge) -> {
                restoreProjectPreferences(journal, bridge);
                publishOnUi(() -> { if (!destroyed) loadProject(previous, fingerprint); });
                flushProjectPreferences();
            });
            projectStorageUncertain = false;
        }, error -> {
            if (!projectStateUncertain) view.restore(false);
            reportManualFileResult(error, "Восстановлено записей: " + restored[0]);
        });
    }

    private void handleScannedQr(String raw) {
        QrValues values = parseDeviceQr(raw);
        EditText serialField = scanSerialField;
        EditText keyField = scanKeyField;
        scanTargetDevice = null;
        scanSerialField = null;
        scanKeyField = null;
        if (values.id.isEmpty()) {
            new AlertDialog.Builder(this)
                    .setTitle("QR-код прочитан")
                    .setMessage("В коде не удалось найти ID устройства. Проверьте наклейку и повторите сканирование.")
                    .setPositiveButton("Понятно", null)
                    .show();
            return;
        }
        if (serialField == null || keyField == null) {
            toast("QR-код прочитан. Откройте карточку устройства и повторите сканирование.");
            return;
        }
        serialField.setText(values.id);
        if (!values.deviceKey.isEmpty()) keyField.setText(values.deviceKey);
        toast(values.deviceKey.isEmpty()
                ? "ID распознан; ключ в QR-коде не найден"
                : "ID и ключ устройства распознаны");
    }

    private QrValues parseDeviceQr(String raw) {
        String value = raw == null ? "" : raw.trim().toUpperCase(Locale.ROOT);
        String id = matchJoined(value,
                "(?i)\\bID\\s*[:=]?\\s*([0-9A-F]{6})[\\s\\r\\n-]*([0-9A-F]{6})");
        if (id.isEmpty()) id = matchOne(value,
                "(?i)\\bID\\s*[:=]?\\s*([0-9A-F]{8,24})");
        String deviceKey = matchOne(value,
                "(?i)\\bKEY\\s*[:=]?\\s*([0-9A-F]{8,32})");

        if (id.isEmpty()) id = matchOne(value,
                "(?i)(?<![0-9A-F])([0-9A-F]{12})(?![0-9A-F])");
        if (deviceKey.isEmpty()) {
            Matcher tokens = Pattern.compile("(?i)(?<![0-9A-F])([0-9A-F]{8})(?![0-9A-F])").matcher(value);
            while (tokens.find()) {
                String candidate = tokens.group(1).toUpperCase(Locale.ROOT);
                if (!candidate.equals(id)) { deviceKey = candidate; break; }
            }
        }
        String compact = value.replaceAll("[^0-9A-F]", "");
        if (id.isEmpty() && deviceKey.isEmpty() && compact.length() == 20) {
            deviceKey = compact.substring(0, 8);
            id = compact.substring(8);
        }
        return new QrValues(id, deviceKey);
    }

    private String matchOne(String value, String expression) {
        Matcher matcher = Pattern.compile(expression).matcher(value);
        return matcher.find() ? matcher.group(1).toUpperCase(Locale.ROOT) : "";
    }

    private String matchJoined(String value, String expression) {
        Matcher matcher = Pattern.compile(expression).matcher(value);
        return matcher.find()
                ? (matcher.group(1) + matcher.group(2)).toUpperCase(Locale.ROOT) : "";
    }

    private ExportSnapshot captureExport() {
        List<ExportSnapshot.Row> rows = new ArrayList<>(devices.size());
        for (Device d : devices) rows.add(new ExportSnapshot.Row(d.id, d.name, d.description, d.zone, d.group,
                d.type, d.sourceRow, d.sourceInitialized, d.commissioning));
        Map<String,Object> frozen=new HashMap<>(prefs.getAll());
        if(!frozen.containsKey(Fieldwork.key(projectFingerprint))&&projectRoot!=null&&projectRoot.optJSONObject("fieldwork")!=null)
            frozen.put(Fieldwork.key(projectFingerprint),projectRoot.optJSONObject("fieldwork").toString());
        return new ExportSnapshot(rows, siteName, projectFingerprint, projectPanels, projectPanelCatalogueComplete,
                panelIndex, frozen, bridgePrefs.getAll(), retirementStorageUncertain);
    }

    private JSONObject createBackup() throws JSONException {
        JSONObject root = new JSONObject();
        root.put("format", "streletz-commissioning-progress");
        root.put("version", 1);
        root.put("siteName", siteName);
        root.put("projectFingerprint", projectFingerprint);
        root.put("panels", projectPanels);
        root.put("panelCatalogComplete", projectPanelCatalogueComplete);
        JSONArray sourceDescriptions = new JSONArray();
        for (Device device : devices) sourceDescriptions.put(new JSONObject()
                .put("recordId", device.id).put("description", device.description));
        root.put("recordMetadata", sourceDescriptions);
        root.put("exportedAt", System.currentTimeMillis());
        JSONArray records = new JSONArray();
        for (Device d : devices) {
            JSONObject item = new JSONObject();
            item.put("id", d.id);
            item.put("name", d.name);
            item.put("stage", getStage(d));
            item.put("stageName", STAGE_LABELS[getStage(d)]);
            item.put("sourceRow", d.sourceRow).put("type", d.type).put("group", d.group).put("zone", d.zone);
            item.put("panelLabel", projectPanelLabel(d));
            ProjectPanelIndex.Panel membership = panelIndex.uniqueForRecord(d.id);
            item.put("projectPanelId", membership == null ? "" : membership.id);
            item.put("serial", getSerial(d));
            item.put("deviceKey", getDeviceKey(d));
            item.put("note", getNote(d));
            item.put("description", getDescription(d));
            item.put("descriptionOverridden", prefs.contains(descriptionPreferenceKey(d))
                    || (d.commissioning != null && d.commissioning.has("description")));
            item.put("panelId", bridgePrefs.getString(panelPreferenceKey(d), ""));
            addRegistrationSummary(item, d);
            item.put("updatedAt", getUpdated(d));
            records.put(item);
        }
        root.put("records", records);
        return root;
    }

    private JSONObject createPortableProject() throws JSONException {
        return ProjectDocuments.portable(projectRoot, createBackup());
    }

    private void addRegistrationSummary(JSONObject destination, Device device) throws JSONException {
        RegistrationOperation operation = operationFor(device);
        if (operation == null) {
            JSONObject historical = importedRegistrationSummary(device);
            if (historical != null) destination.put("registration", RegistrationRecovery.safeSummary(historical));
            return;
        }
        JSONObject summary = RegistrationRecovery.safeSummary(operation.toStored());
        JSONArray history = new JSONArray();
        Set<String> archived = new HashSet<>();
        JSONObject imported = importedRegistrationSummary(device);
        JSONArray prior = imported == null ? null : imported.optJSONArray("history");
        JSONArray local = new JSONArray(bridgePrefs.getString(operationHistoryKey(operation), "[]"));
        for (JSONArray entries : new JSONArray[]{prior, local}) if (entries != null) {
            for (int i = 0; i < entries.length(); i++) {
                JSONObject entry = RegistrationRecovery.safeSummary(entries.getJSONObject(i));
                if (archived.add(entry.optString("operationId"))) history.put(entry);
            }
        }
        if (history.length() > 0) summary.put("history", history);
        destination.put("registration", summary);
    }

    private void saveImportedProject(String raw) throws IOException {
        ProjectTransfer.write(new File(getFilesDir(), PROJECT_FILE), raw);
    }

    private int restoreBackup(String raw) throws JSONException { return restoreBackup(new JSONObject(raw)); }

    private int restoreBackup(JSONObject root) throws JSONException {
        if (!"streletz-commissioning-progress".equals(root.optString("format"))) {
            throw new JSONException("Это не копия журнала приложения «Стрелец · Инициализация».");
        }
        if (root.has("projectFingerprint") && !projectFingerprint.equals(root.optString("projectFingerprint")))
            throw new JSONException("Копия журнала относится к другому проекту. Сначала откройте соответствующий проект.");
        JSONArray rows = root.getJSONArray("records");
        JSONArray restoredMetadata = root.optJSONArray("recordMetadata");
        if (restoredMetadata == null) {
            restoredMetadata = new JSONArray();
            for (int i = 0; i < rows.length(); i++) {
                JSONObject row = rows.getJSONObject(i);
                if (row.has("description") && !row.optBoolean("descriptionOverridden", true))
                    restoredMetadata.put(new JSONObject().put("recordId", row.optString("id"))
                            .put("description", row.optString("description", "")));
            }
        }
        SharedPreferences.Editor editor = prefs.edit();
        SharedPreferences.Editor panelSelections = bridgePrefs.edit();
        int restored = 0;
        for (int i = 0; i < rows.length(); i++) {
            JSONObject row = rows.getJSONObject(i);
            Device d = findById(row.optString("id"));
            if (d == null) continue;
            int stage = clampStage(row.optInt("stage", 0));
            editor.putInt(key("stage", d), stage);
            editor.putString(key("serial", d), row.optString("serial"));
            editor.putString(key("device_key", d), row.optString("deviceKey"));
            editor.putString(key("note", d), row.optString("note"));
            if (row.has("description")) {
                if (row.optBoolean("descriptionOverridden", true))
                    editor.putString(descriptionPreferenceKey(d), row.optString("description", ""));
                else editor.remove(descriptionPreferenceKey(d));
            }
            editor.putLong(key("updated", d), row.optLong("updatedAt", 0));
            if (row.optJSONObject("registration") != null)
                editor.putString(key("registration_summary", d), row.getJSONObject("registration").toString());
            preserveManualRegistration(editor, d);
            if (row.has("panelId")) panelSelections.putString(panelPreferenceKey(d), row.optString("panelId", ""));
            restored++;
        }
        JSONArray restoredPanels = root.has("projectFingerprint") && root.optJSONArray("panels") != null
                ? root.getJSONArray("panels") : projectPanels;
        installProjectCatalogue(restoredPanels, restoredMetadata, false);
        if (root.has("projectFingerprint") && root.optJSONArray("panels") != null)
            projectPanelCatalogueComplete = root.optBoolean("panelCatalogComplete", false);
        editor.apply();
        panelSelections.apply();
        cacheProjectCatalogue();
        rebuildPanelFilter();
        setCatalogueStatus("Панелей: " + panelIndex.panels().size() + " · из копии",
                "Каталог восстановлен из копии журнала. Для регистрации обновите связь с проектом Windows.");
        return restored;
    }

    private String createCsv() {
        StringBuilder csv = new StringBuilder("sep=;\r\n");
        csv.append("Номер;Название;Тип;Группа;Зона;Описание;Панель;Этап;Серийный номер;Ключ устройства;Примечание;Изменено\r\n");
        for (Device d : devices) {
            long updated = getUpdated(d);
            String updatedText = updated == 0 ? "" : new SimpleDateFormat(
                    "dd.MM.yyyy HH:mm", Locale.getDefault()).format(new Date(updated));
            csv.append(d.sourceRow).append(';')
                    .append(csvCell(d.name)).append(';')
                    .append(csvCell(d.type)).append(';')
                    .append(csvCell(d.group)).append(';')
                    .append(csvCell(d.zone)).append(';')
                    .append(csvCell(getDescription(d))).append(';')
                    .append(csvCell(projectPanelLabel(d))).append(';')
                    .append(csvCell(STAGE_LABELS[getStage(d)])).append(';')
                    .append(csvCell(getSerial(d))).append(';')
                    .append(csvCell(getDeviceKey(d))).append(';')
                    .append(csvCell(getNote(d))).append(';')
                    .append(csvCell(updatedText)).append("\r\n");
        }
        return "\uFEFF" + csv;
    }

    private void ensureDeviceIndex() {
        if (deviceIndex != null && indexedDevices == devices && indexedDeviceCount == devices.size()) return;
        Map<String, Device> replacement = new HashMap<>();
        for (Device device : devices) {
            if (replacement.put(device.id.toLowerCase(Locale.ROOT), device) != null)
                throw new IllegalStateException("В проекте повторяется GUID записи.");
        }
        deviceIndex = replacement;
        indexedDevices = devices;
        indexedDeviceCount = devices.size();
    }

    private Device findById(String id) {
        ensureDeviceIndex();
        return id == null ? null : deviceIndex.get(id.toLowerCase(Locale.ROOT));
    }

    private void writeUri(Uri uri, String content) throws IOException {
        ContentResolver resolver = getContentResolver();
        try (OutputStream stream = resolver.openOutputStream(uri, "wt")) {
            if (stream == null) throw new IOException("Не удалось открыть выбранный файл.");
            stream.write(content.getBytes(StandardCharsets.UTF_8));
        }
    }

    private String readAll(InputStream input) throws IOException {
        if (input == null) throw new IOException("Файл недоступен.");
        try (InputStream stream = input; ByteArrayOutputStream output = new ByteArrayOutputStream()) {
            byte[] buffer = new byte[8192];
            int count;
            while ((count = stream.read(buffer)) != -1) {
                if ((long) output.size() + count > ProjectTransfer.MAX_DOCUMENT_BYTES)
                    throw new IOException("Файл превышает допустимый размер 16 МБ.");
                output.write(buffer, 0, count);
            }
            return output.toString(StandardCharsets.UTF_8.name());
        }
    }

    private int getStage(Device d) {
        return prefs.contains(key("stage", d))
                ? prefs.getInt(key("stage", d), 0)
                : d.commissioning != null ? clampStage(d.commissioning.optInt("stage", d.sourceInitialized ? 2 : 0))
                : (d.sourceInitialized ? 2 : 0);
    }

    private int clampStage(int stage) { return Math.max(0, Math.min(3, stage)); }

    private String baseValue(Device d, String field) { return d.commissioning == null ? "" : d.commissioning.optString(field, ""); }
    private String getSerial(Device d) { return prefs.getString(key("serial", d), baseValue(d, "serial")); }
    private String getDeviceKey(Device d) { return prefs.getString(key("device_key", d), baseValue(d, "deviceKey")); }
    private String getNote(Device d) { return prefs.getString(key("note", d), baseValue(d, "note")); }
    private long getUpdated(Device d) {
        return prefs.getLong(key("updated", d), d.commissioning == null ? 0 : d.commissioning.optLong("updatedAt", 0));
    }
    private String descriptionPreferenceKey(Device d) {
        return "description:" + projectFingerprint + ":" + d.id.toLowerCase(Locale.ROOT);
    }
    private String getDescription(Device d) {
        String key = descriptionPreferenceKey(d);
        String base = d.commissioning != null && d.commissioning.has("description")
                ? d.commissioning.optString("description", "") : d.description;
        return ProjectPanelIndex.description(base, prefs.contains(key), prefs.getString(key, ""));
    }
    private String getDisplayName(Device d) {
        return DeviceLabels.preferredName(d.name, getDescription(d));
    }
    private void saveDescriptionIfChanged(SharedPreferences.Editor changes, DeviceCard card) {
        String value = card.description.getText().toString().trim();
        if (prefs.contains(descriptionPreferenceKey(card.device)) || !value.equals(card.device.description))
            changes.putString(descriptionPreferenceKey(card.device), value);
    }
    private String key(String part, Device d) { return part + ":" + projectFingerprint + ":" + d.id.toLowerCase(Locale.ROOT); }

    private void showFatalError(String title, Exception error) {
        new AlertDialog.Builder(this)
                .setTitle(title)
                .setMessage(error.getMessage())
                .setCancelable(false)
                .setPositiveButton("Закрыть", (d, which) -> finish())
                .show();
    }

    private ArrayAdapter<String> simpleAdapter(String[] values) {
        ArrayAdapter<String> adapter = new ArrayAdapter<>(this,
                android.R.layout.simple_spinner_item, values);
        adapter.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item);
        return adapter;
    }

    private ArrayAdapter<String> filterAdapter(String[] values) {
        ArrayAdapter<String> adapter = new ArrayAdapter<String>(this,
                android.R.layout.simple_spinner_item, values) {
            @Override public View getView(int position, View convertView, ViewGroup parent) {
                TextView view = (TextView) super.getView(position, convertView, parent);
                view.setSingleLine(true);
                view.setEllipsize(TextUtils.TruncateAt.END);
                view.setTextSize(14);
                return view;
            }
            @Override public View getDropDownView(int position, View convertView, ViewGroup parent) {
                TextView view = (TextView) super.getDropDownView(position, convertView, parent);
                view.setSingleLine(false);
                view.setMaxLines(Integer.MAX_VALUE);
                view.setEllipsize(null);
                view.setTextSize(14);
                view.setPadding(dp(12), dp(10), dp(12), dp(10));
                view.setMinimumHeight(dp(48));
                return view;
            }
        };
        adapter.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item);
        return adapter;
    }

    private TextView singleLineText(String value, int sp, int color, boolean bold) {
        TextView view = text(value, sp, color, bold);
        view.setSingleLine(true);
        view.setEllipsize(TextUtils.TruncateAt.END);
        view.setIncludeFontPadding(false);
        return view;
    }

    private TextView text(String value, int sp, int color, boolean bold) {
        TextView view = new TextView(this);
        view.setText(value);
        view.setTextSize(sp);
        view.setTextColor(color);
        if (bold) view.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        return view;
    }

    private TextView fieldLabel(String value) {
        TextView label = text(value, 12, color("text_secondary"), true);
        label.setPadding(0, dp(10), 0, 0);
        return label;
    }

    private Button compactButton(String value) {
        Button button = new Button(this);
        button.setText(value);
        button.setTextSize(11);
        button.setAllCaps(false);
        button.setMinHeight(0);
        button.setMinimumHeight(0);
        button.setPadding(dp(4), dp(2), dp(4), dp(2));
        return button;
    }

    private LinearLayout.LayoutParams matchWrap() {
        return new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
    }

    private LinearLayout.LayoutParams weighted() {
        return new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1);
    }

    private LinearLayout.LayoutParams weightedWithStartMargin() {
        LinearLayout.LayoutParams lp = weighted();
        lp.leftMargin = dp(6);
        return lp;
    }

    private Drawable resourceDrawable(String name) {
        int id = getResources().getIdentifier(name, "drawable", getPackageName());
        return getDrawable(id);
    }

    private int color(String name) {
        int id = getResources().getIdentifier(name, "color", getPackageName());
        return getColor(id);
    }

    private int dp(int value) {
        return Math.round(value * getResources().getDisplayMetrics().density);
    }

    private String valueOrDash(String value) { return value == null || value.isEmpty() ? "—" : value; }

    private String csvCell(String value) {
        if (value == null) value = "";
        return "\"" + value.replace("\"", "\"\"") + "\"";
    }

    private String fileStamp(String prefix, String extension) {
        return prefix + "-" + new SimpleDateFormat("yyyyMMdd-HHmm", Locale.ROOT).format(new Date()) + extension;
    }

    private void toast(String message) { Toast.makeText(this, message, Toast.LENGTH_LONG).show(); }

    private void hideKeyboard(View view) {
        InputMethodManager manager = (InputMethodManager) getSystemService(Context.INPUT_METHOD_SERVICE);
        if (manager != null) manager.hideSoftInputFromWindow(view.getWindowToken(), 0);
    }

    private static class Device {
        String id;
        String name;
        String description;
        String searchIndex;
        long searchVersion;
        String version;
        String zone;
        String group;
        String type;
        int sourceRow;
        int listOrder;
        boolean sourceInitialized;
        JSONObject commissioning;
    }

    private class DeviceAdapter extends BaseAdapter {
        @Override public int getCount() { return visible.size(); }
        @Override public Device getItem(int position) { return visible.get(position); }
        @Override public long getItemId(int position) { return visible.get(position).sourceRow; }

        @Override
        public View getView(int position, View convertView, ViewGroup parent) {
            Holder h;
            if (convertView == null) {
                LinearLayout card = new LinearLayout(MainActivity.this);
                card.setOrientation(LinearLayout.VERTICAL);
                card.setBackground(resourceDrawable("card_background"));
                card.setPadding(dp(14), dp(11), dp(14), dp(11));
                h = new Holder();
                h.title = text("", 16, color("text_primary"), true);
                h.type = text("", 13, color("text_secondary"), false);
                h.location = singleLineText("", 12, color("text_secondary"), false);
                h.status = text("", 13, color("accent"), true);
                h.serial = text("", 12, color("text_secondary"), false);
                h.type.setPadding(0, dp(3), 0, 0);
                h.location.setPadding(0, dp(5), 0, 0);
                h.status.setPadding(0, dp(7), 0, 0);
                card.addView(h.title, matchWrap());
                card.addView(h.type, matchWrap());
                card.addView(h.location, matchWrap());
                card.addView(h.status, matchWrap());
                card.addView(h.serial, matchWrap());
                card.setTag(h);
                convertView = card;
            } else {
                h = (Holder) convertView.getTag();
            }
            bind(convertView, getItem(position));
            return convertView;
        }

        void bind(View view, Device d) {
            Holder h = (Holder) view.getTag();
            int stage = getStage(d);
            String displayName = getDisplayName(d);
            h.title.setText(displayName.isEmpty() ? d.type : displayName);
            h.type.setText(d.type);
            h.type.setVisibility(d.type.isEmpty() || displayName.isEmpty() || displayName.equals(d.type)
                    ? View.GONE : View.VISIBLE);
            h.location.setText("Зона " + valueOrDash(d.zone) + "  •  " + projectPanelLabel(d));
            h.status.setText(STAGE_LABELS[stage] + (journalConflict(d).isEmpty() ? "" : " · нужна сверка"));
            h.status.setTextColor(stage == 3 ? color("success") : stage == 2
                    ? Color.rgb(27, 105, 147) : stage == 1 ? color("warning") : color("accent"));
            String serial = getSerial(d);
            String note = getNote(d);
            String lower = (serial.isEmpty() ? "Серийный номер не указан" : "С/н: " + serial)
                    + (note.isEmpty() ? "" : "  •  " + note);
            h.serial.setText(lower);
        }
    }

    private static class Holder {
        TextView title;
        TextView type;
        TextView location;
        TextView status;
        TextView serial;
    }

    private static class QrValues {
        final String id;
        final String deviceKey;
        QrValues(String id, String deviceKey) {
            this.id = id == null ? "" : id;
            this.deviceKey = deviceKey == null ? "" : deviceKey;
        }
    }
}
