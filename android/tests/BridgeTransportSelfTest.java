// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;

import org.json.JSONObject;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.net.InetAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.net.SocketTimeoutException;
import java.nio.charset.StandardCharsets;
import java.lang.reflect.Field;
import java.lang.reflect.Constructor;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;
import java.util.HashMap;
import java.util.HashSet;
import java.util.Set;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.Future;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicReference;
import java.util.concurrent.ConcurrentHashMap;

/** Local loopback transport and production request-identity checks; never contacts the user's network. */
public final class BridgeTransportSelfTest {
    private static int assertions;
    private static final String OP = "00000000-0000-4000-8000-000000000061";
    private static final Class<?> OP_TYPE;
    private static final Class<?> REQUEST_TYPE;
    static {
        try {
            OP_TYPE = Class.forName("ru.akchurin.streletzinit.MainActivity$RegistrationOperation");
            REQUEST_TYPE = Class.forName("ru.akchurin.streletzinit.MainActivity$OperationRequest");
        } catch (Exception error) { throw new ExceptionInInitializerError(error); }
    }

    public static void main(String[] args) throws Exception {
        delayedReply();
        abortRead();
        boundedProgress();
        boundedDns();
        abortedBeforeSend();
        framingBound();
        freshAuthenticatedSend();
        cancellationIdentityRace();
        System.out.println("BridgeTransportSelfTest: " + assertions
                + " assertions passed (127.0.0.1 delayed replies, abort/deadlines, stale callback guard)");
    }

    private static void delayedReply() throws Exception {
        AtomicInteger built = new AtomicInteger();
        try (Peer peer = new Peer((socket, frame) -> {
            Thread.sleep(7200); // Exceeds the old seven-second response deadline.
            socket.getOutputStream().write("late-authenticated-frame\n".getBytes(StandardCharsets.UTF_8));
            socket.getOutputStream().flush();
        })) {
            long started = System.nanoTime();
            BridgeTransport.Exchange exchange = new BridgeTransport.Exchange(1000, 10000, 11000);
            check(built.get() == 0, "request construction is deferred until the exchange runs");
            String response = exchange.perform("127.0.0.1", peer.port(), () -> {
                built.incrementAndGet(); return "one-request";
            }, 1024, 1024);
            check("late-authenticated-frame".equals(response), "reply delayed beyond the former seven seconds is accepted");
            check(elapsed(started) >= 7000 && elapsed(started) < 11000, "delayed reply remains within its whole exchange budget");
            check(built.get() == 1 && "one-request".equals(peer.request), "only one complete request frame was sent");
            rejects(IOException.class, () -> exchange.perform("127.0.0.1", peer.port(), () -> "again", 1024, 1024),
                    "an exchange object cannot resend a begin or any other request");
        }
    }

    private static void abortRead() throws Exception {
        ExecutorService worker = Executors.newSingleThreadExecutor();
        try (Peer peer = new Peer((socket, frame) -> { while (socket.getInputStream().read() >= 0) { } })) {
            BridgeTransport.Exchange exchange = new BridgeTransport.Exchange(1000, 10000, 11000);
            Future<String> waiting = worker.submit(() -> exchange.perform("127.0.0.1", peer.port(), () -> "status", 1024, 1024));
            check(peer.received.await(2, TimeUnit.SECONDS), "status request reaches peer before cancellation");
            long started = System.nanoTime();
            exchange.abort();
            futureRejects(waiting, BridgeTransport.AbortedException.class, "abort closes a pending response read");
            check(elapsed(started) < 2000, "cancel need not wait for the long response deadline");
            check("status".equals(peer.request), "read interruption does not send a new hardware command");
        } finally { worker.shutdownNow(); }
    }

    private static void boundedProgress() throws Exception {
        try (Peer peer = new Peer((socket, frame) -> {
            for (int i = 0; i < 30; i++) {
                try { socket.getOutputStream().write('x'); socket.getOutputStream().flush(); }
                catch (IOException closed) { break; }
                Thread.sleep(50);
            }
        })) {
            long started = System.nanoTime();
            rejects(SocketTimeoutException.class,
                    () -> new BridgeTransport.Exchange(1000, 180, 2000).perform("127.0.0.1", peer.port(), () -> "status", 1024, 1024),
                    "a trickling response cannot keep extending the response budget");
            check(elapsed(started) < 2000, "progress does not remove the absolute deadline");
        }
        try (Peer peer = new Peer((socket, frame) -> { while (socket.getInputStream().read() >= 0) { } })) {
            long started = System.nanoTime();
            rejects(SocketTimeoutException.class,
                    () -> new BridgeTransport.Exchange(1000, 10000, 180).perform("127.0.0.1", peer.port(), () -> "status", 1024, 1024),
                    "the whole exchange deadline bounds a longer read timeout");
            check(elapsed(started) < 2000, "whole exchange deadline closes the socket promptly");
        }
    }

    private static void abortedBeforeSend() throws Exception {
        AtomicInteger built = new AtomicInteger();
        BridgeTransport.Exchange exchange = new BridgeTransport.Exchange(); exchange.abort();
        rejects(BridgeTransport.AbortedException.class,
                () -> exchange.perform("127.0.0.1", 1, () -> { built.incrementAndGet(); return "begin"; }, 1024, 1024),
                "a superseded queued read never connects or sends when its executor eventually runs");
        check(built.get() == 0, "an aborted queued request does not even build its authenticated frame");
    }

    private static void boundedDns() throws Exception {
        CountDownLatch release = new CountDownLatch(1);
        AtomicInteger built = new AtomicInteger();
        BridgeTransport.HostResolver slow = host -> {
            while (release.getCount() != 0) {
                try { release.await(); } catch (InterruptedException ignored) { }
            }
            return InetAddress.getByAddress(new byte[]{127, 0, 0, 1});
        };
        try {
            long started = System.nanoTime();
            rejects(SocketTimeoutException.class,
                    () -> new BridgeTransport.Exchange(160, 10000, 1000, slow).perform("slow.invalid", 1,
                            () -> { built.incrementAndGet(); return "begin"; }, 1024, 1024),
                    "a platform DNS resolver ignoring interruption cannot extend the connection budget");
            check(elapsed(started) < 1500 && built.get() == 0, "DNS timeout returns before any authenticated request is built");
        } finally { release.countDown(); }

        ExecutorService worker = Executors.newSingleThreadExecutor();
        CountDownLatch entered = new CountDownLatch(1), done = new CountDownLatch(1);
        BridgeTransport.Exchange exchange = new BridgeTransport.Exchange(10000, 10000, 20000, host -> {
            entered.countDown();
            while (done.getCount() != 0) {
                try { done.await(); } catch (InterruptedException ignored) { }
            }
            return InetAddress.getByAddress(new byte[]{127, 0, 0, 1});
        });
        try {
            Future<String> waiting = worker.submit(() -> exchange.perform("blocked.invalid", 1, () -> "status", 1024, 1024));
            check(entered.await(2, TimeUnit.SECONDS), "injected slow DNS started");
            exchange.abort();
            futureRejects(waiting, BridgeTransport.AbortedException.class, "cancellation interrupts the caller's DNS wait even if resolver ignores interruption");
        } finally { done.countDown(); worker.shutdownNow(); }
    }

    private static void freshAuthenticatedSend() throws Exception {
        String code = "0011223344556677";
        AtomicReference<JSONObject> sent = new AtomicReference<>();
        try (Peer peer = new Peer((socket, frame) -> {
            JSONObject outer = new JSONObject(frame);
            byte[] plaintext = ProtocolV3.open(new ProtocolV3.Envelope(outer.getString("iv"), outer.getString("data"),
                    outer.getString("mac")), code, "request");
            JSONObject request = new JSONObject(new String(plaintext, StandardCharsets.UTF_8));
            sent.set(request);
            JSONObject reply = new JSONObject().put("protocol", 3).put("type", "response")
                    .put("requestId", request.getString("requestId")).put("ok", true);
            ProtocolV3.Envelope sealed = ProtocolV3.seal(reply.toString().getBytes(StandardCharsets.UTF_8), code, "response", new byte[16]);
            JSONObject response = new JSONObject().put("protocol", 3).put("type", "bridge-response")
                    .put("iv", sealed.iv).put("data", sealed.data).put("mac", sealed.mac);
            socket.getOutputStream().write((response.toString() + "\n").getBytes(StandardCharsets.UTF_8));
            socket.getOutputStream().flush();
        })) {
            Class<?> unsafe = Class.forName("sun.misc.Unsafe"); Field singleton = unsafe.getDeclaredField("theUnsafe"); singleton.setAccessible(true);
            MainActivity activity = (MainActivity) unsafe.getMethod("allocateInstance", Class.class).invoke(singleton.get(null), MainActivity.class);
            Set<BridgeTransport.Exchange> exchanges = ConcurrentHashMap.newKeySet();
            set(activity, "bridgeExchanges", exchanges);
            Class<?> configType = Class.forName("ru.akchurin.streletzinit.MainActivity$BridgeConfig");
            Constructor<?> constructor = configType.getDeclaredConstructor(String.class, int.class, String.class); constructor.setAccessible(true);
            Object config = constructor.newInstance("127.0.0.1", peer.port(), code);
            long oldTime = System.currentTimeMillis() - 600000;
            JSONObject request = new JSONObject().put("protocol", 3).put("type", "begin_registration")
                    .put("requestId", OP).put("operationId", OP).put("sentAt", oldTime);
            long beforeSend = System.currentTimeMillis();
            JSONObject reply = (JSONObject) invoke(activity, "callBridge", new Class<?>[]{configType, JSONObject.class}, config, request);
            check(reply.getBoolean("ok"), "production authenticated exchange returns the matching response");
            check(sent.get().getLong("sentAt") >= beforeSend && sent.get().getLong("sentAt") <= System.currentTimeMillis(),
                    "a request delayed in a queue is timestamped freshly at actual send time");
            check(OP.equals(sent.get().getString("requestId")) && OP.equals(sent.get().getString("operationId")),
                    "fresh send timestamp never creates a new registration identity");
            check(request.getLong("sentAt") == oldTime, "network worker does not mutate the UI-owned request object");
            check(exchanges.isEmpty(), "completed socket is removed from lifecycle cancellation tracking");
        }
    }

    private static void framingBound() throws Exception {
        try (Peer peer = new Peer((socket, frame) -> {
            socket.getOutputStream().write(("x".repeat(128) + "\n").getBytes(StandardCharsets.UTF_8));
            socket.getOutputStream().flush();
        })) {
            rejects(IOException.class,
                    () -> new BridgeTransport.Exchange(1000, 1000, 2000).perform("127.0.0.1", peer.port(), () -> "status", 1024, 64),
                    "a longer VPN timeout does not relax the response size limit");
        }
    }

    private static void cancellationIdentityRace() throws Exception {
        Class<?> unsafe = Class.forName("sun.misc.Unsafe");
        Field singleton = unsafe.getDeclaredField("theUnsafe"); singleton.setAccessible(true);
        MainActivity activity = (MainActivity) unsafe.getMethod("allocateInstance", Class.class).invoke(singleton.get(null), MainActivity.class);
        Set<String> inFlight = new HashSet<>();
        set(activity, "operationsInFlight", inFlight); set(activity, "operationRequests", new HashMap<>());
        Method load = OP_TYPE.getDeclaredMethod("fromStored", JSONObject.class); load.setAccessible(true);
        Object operation = load.invoke(null, new JSONObject().put("operationId", OP).put("projectFingerprint", "d".repeat(64))
                .put("recordId", "00000000-0000-4000-8000-000000000001").put("panelId", "panel-test")
                .put("expectedHardwareId", "001122334455").put("host", "192.0.2.1").put("port", 23019)
                .put("pairingCode", "0011223344556677").put("state", "unknown").put("uncertain", true));
        Object old = start(activity, operation, "registration_status");
        check(inFlight.contains(OP), "status request holds the operation lock");
        check((Boolean) invoke(activity, "preemptReadForCancel", new Class<?>[]{OP_TYPE}, operation), "explicit cancellation may interrupt a status read");
        Object cancel = start(activity, operation, "cancel_registration");
        check(!(Boolean) finish(activity, operation, old) && inFlight.contains(OP),
                "late status callback cannot remove the newer cancel request lock");
        check((Boolean) finish(activity, operation, cancel) && !inFlight.contains(OP), "only current cancel callback releases its lock");
        check(!(Boolean) finish(activity, operation, cancel), "duplicate callback cannot release another request");

        Object begin = start(activity, operation, "begin_registration");
        check(!(Boolean) invoke(activity, "preemptReadForCancel", new Class<?>[]{OP_TYPE}, operation)
                && inFlight.contains(OP), "cancel waits for pending begin acknowledgement instead of racing before Windows acceptance");
        check((Boolean) finish(activity, operation, begin), "pending begin still owns its eventual callback");
        Object recovery = start(activity, operation, "recover_registration");
        check((Boolean) invoke(activity, "preemptReadForCancel", new Class<?>[]{OP_TYPE}, operation), "read-only recovery may be preempted too");
        Object newer = start(activity, operation, "cancel_registration");
        check(!(Boolean) finish(activity, operation, recovery) && inFlight.contains(OP), "late recovery callback cannot publish over cancellation");
        check(!(Boolean) invoke(activity, "preemptReadForCancel", new Class<?>[]{OP_TYPE}, operation), "an already dispatched cancellation is not repeatedly replaced");
        finish(activity, operation, newer);
    }

    private static Object start(MainActivity activity, Object operation, String type) throws Exception {
        return invoke(activity, "startOperationRequest", new Class<?>[]{OP_TYPE, String.class}, operation, type);
    }
    private static Object finish(MainActivity activity, Object operation, Object request) throws Exception {
        return invoke(activity, "finishOperationRequest", new Class<?>[]{OP_TYPE, REQUEST_TYPE}, operation, request);
    }
    private static long elapsed(long started) { return TimeUnit.NANOSECONDS.toMillis(System.nanoTime() - started); }
    private static void check(boolean value, String message) { if (!value) throw new AssertionError(message); assertions++; }
    private static void rejects(Class<? extends Exception> type, Action action, String message) throws Exception {
        try { action.run(); }
        catch (Exception error) { if (type.isInstance(error)) { assertions++; return; } throw error; }
        throw new AssertionError(message);
    }
    private static void futureRejects(Future<?> result, Class<? extends Exception> type, String message) throws Exception {
        try { result.get(2, TimeUnit.SECONDS); }
        catch (ExecutionException error) { if (type.isInstance(error.getCause())) { assertions++; return; } throw error; }
        throw new AssertionError(message);
    }
    private static Object invoke(Object target, String name, Class<?>[] types, Object... args) throws Exception {
        Method method = target.getClass().getDeclaredMethod(name, types); method.setAccessible(true);
        try { return method.invoke(target, args); }
        catch (InvocationTargetException error) { if (error.getCause() instanceof Exception) throw (Exception) error.getCause(); throw error; }
    }
    private static void set(Object target, String name, Object value) throws Exception {
        Field field = target.getClass().getDeclaredField(name); field.setAccessible(true); field.set(target, value);
    }
    private interface Action { void run() throws Exception; }
    private interface Response { void send(Socket socket, String request) throws Exception; }
    private static final class Peer implements AutoCloseable {
        final ServerSocket listener = new ServerSocket(0, 2, InetAddress.getByName("127.0.0.1"));
        final CountDownLatch received = new CountDownLatch(1);
        final ExecutorService executor = Executors.newSingleThreadExecutor();
        final Future<?> task;
        volatile String request;
        Peer(Response response) throws Exception {
            task = executor.submit(() -> {
                try (Socket socket = listener.accept()) {
                    socket.setSoTimeout(12000);
                    ByteArrayOutputStream bytes = new ByteArrayOutputStream();
                    int next;
                    while ((next = socket.getInputStream().read()) >= 0 && next != '\n' && bytes.size() < 4096) bytes.write(next);
                    request = new String(bytes.toByteArray(), StandardCharsets.UTF_8);
                    received.countDown();
                    response.send(socket, request);
                    return null;
                }
            });
        }
        int port() { return listener.getLocalPort(); }
        public void close() throws Exception {
            listener.close();
            try { task.get(2, TimeUnit.SECONDS); }
            finally { executor.shutdownNow(); }
        }
    }
}
