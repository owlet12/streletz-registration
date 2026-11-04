// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;

import java.io.BufferedInputStream;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.net.InetSocketAddress;
import java.net.InetAddress;
import java.net.Socket;
import java.net.SocketTimeoutException;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.CancellationException;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.TimeoutException;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.ThreadPoolExecutor;
import java.util.concurrent.RejectedExecutionException;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.ScheduledFuture;
import java.util.concurrent.TimeUnit;

/** One bounded TCP or HTTPS exchange. This layer never retries a request or interprets a registration result. */
final class BridgeTransport {
    static final int CONNECT_MILLIS = 30000;
    static final int RESPONSE_MILLIS = 120000;
    static final int EXCHANGE_MILLIS = CONNECT_MILLIS + RESPONSE_MILLIS;
    private static final ScheduledExecutorService DEADLINES = Executors.newSingleThreadScheduledExecutor(task -> {
        Thread thread = new Thread(task, "streletz-bridge-deadline");
        thread.setDaemon(true);
        return thread;
    });
    private static final ThreadPoolExecutor RESOLVER = new ThreadPoolExecutor(2, 2, 0L,
            TimeUnit.MILLISECONDS, new ArrayBlockingQueue<>(8), task -> {
        Thread thread = new Thread(task, "streletz-bridge-dns");
        thread.setDaemon(true);
        return thread;
    });

    interface RequestFrame { String create() throws Exception; }
    interface HostResolver { InetAddress resolve(String host) throws Exception; }

    static final class AbortedException extends IOException {
        AbortedException() { super("Ожидание ответа прервано для отправки отмены."); }
    }

    static final class Exchange {
        private final int connectMillis, responseMillis, exchangeMillis;
        private final HostResolver resolver;
        private Socket socket;
        private Future<InetAddress> resolving;
        private boolean used, aborted, timedOut;

        Exchange() { this(CONNECT_MILLIS, RESPONSE_MILLIS, EXCHANGE_MILLIS); }

        // Lower injected deadlines make transport/race tests fast without weakening production limits.
        Exchange(int connectMillis, int responseMillis, int exchangeMillis) {
            this(connectMillis, responseMillis, exchangeMillis, InetAddress::getByName);
        }

        Exchange(int connectMillis, int responseMillis, int exchangeMillis, HostResolver resolver) {
            if (connectMillis <= 0 || responseMillis <= 0 || exchangeMillis <= 0)
                throw new IllegalArgumentException("Timeouts must be positive");
            this.connectMillis = connectMillis;
            this.responseMillis = responseMillis;
            this.exchangeMillis = exchangeMillis;
            this.resolver = resolver;
        }

        void abort() {
            Socket current;
            Future<InetAddress> lookup;
            synchronized (this) { aborted = true; current = socket; lookup = resolving; }
            if (lookup != null) lookup.cancel(true);
            close(current);
        }

        private void expire() {
            Socket current;
            Future<InetAddress> lookup;
            synchronized (this) { timedOut = true; current = socket; lookup = resolving; }
            if (lookup != null) lookup.cancel(true);
            close(current);
        }

        private synchronized void requireOpen() throws IOException {
            if (aborted) throw new AbortedException();
            if (timedOut) throw new SocketTimeoutException("Истёк срок ожидания ответа Windows-моста.");
        }

        private InetAddress resolve(String host, long started) throws Exception {
            // The usual numeric IPv4 endpoint remains usable even if unrelated platform DNS calls stall.
            if (host.matches("[0-9]{1,3}(\\.[0-9]{1,3}){3}")) {
                String[] pieces = host.split("\\."); byte[] address = new byte[4]; boolean valid = true;
                for (int i = 0; i < 4; i++) {
                    int value = Integer.parseInt(pieces[i]); valid &= value <= 255; address[i] = (byte) value;
                }
                if (valid) return InetAddress.getByAddress(address);
            }
            final Future<InetAddress> lookup;
            try { lookup = RESOLVER.submit(() -> resolver.resolve(host)); }
            catch (RejectedExecutionException error) {
                throw new IOException("Ожидание DNS занято. Укажите числовой IP-адрес Windows-моста или повторите проверку позже.", error);
            }
            synchronized (this) { resolving = lookup; }
            try {
                requireOpen();
                return lookup.get(remainingConnectMillis(started), TimeUnit.MILLISECONDS);
            } catch (TimeoutException error) {
                expire();
                throw new SocketTimeoutException("Не удалось разрешить адрес Windows-моста вовремя.");
            } catch (CancellationException error) {
                requireOpen();
                throw new IOException("Разрешение адреса Windows-моста прервано.", error);
            } catch (ExecutionException error) {
                if (error.getCause() instanceof Exception) throw (Exception) error.getCause();
                throw new IOException("Не удалось разрешить адрес Windows-моста.", error);
            } catch (InterruptedException error) {
                Thread.currentThread().interrupt();
                abort();
                throw new AbortedException();
            } finally {
                lookup.cancel(true);
                if (lookup instanceof Runnable) RESOLVER.remove((Runnable) lookup);
                synchronized (this) { if (resolving == lookup) resolving = null; }
            }
        }

        private int remainingConnectMillis(long started) throws SocketTimeoutException {
            long budget = TimeUnit.MILLISECONDS.toNanos(Math.min(connectMillis, exchangeMillis));
            long remaining = budget - (System.nanoTime() - started);
            if (remaining <= 0) {
                expire();
                throw new SocketTimeoutException("Не удалось подключиться к Windows-мосту вовремя.");
            }
            return (int) Math.max(1L, TimeUnit.NANOSECONDS.toMillis(remaining));
        }

        String perform(String host, int port, RequestFrame request, int maxRequestBytes, int maxResponseBytes)
                throws Exception {
            final BridgeEndpoint endpoint = BridgeEndpoint.parse(host, port);
            final Socket current;
            synchronized (this) {
                if (used) throw new IOException("Повторная отправка одного сетевого запроса запрещена.");
                used = true;
                requireOpen();
                current = new Socket();
                socket = current;
            }
            final long started = System.nanoTime();
            // ScheduledExecutorService uses relative, monotonic scheduling. Both timers close the socket,
            // bounding writes and a trickling response as well as a fully silent read.
            ScheduledFuture<?> whole = DEADLINES.schedule(this::expire, exchangeMillis, TimeUnit.MILLISECONDS);
            ScheduledFuture<?> response = null;
            ScheduledFuture<?> connecting = DEADLINES.schedule(this::expire, connectMillis, TimeUnit.MILLISECONDS);
            Socket connection = current;
            try {
                // DNS does not obey Socket.close(). Bound its caller separately; the daemon pool is
                // fixed in size even if a platform resolver ignores interruption internally.
                InetAddress address = resolve(endpoint.host, started);
                requireOpen();
                current.connect(new InetSocketAddress(address, endpoint.port), remainingConnectMillis(started));
                requireOpen();
                if (endpoint.https) {
                    javax.net.ssl.SSLSocket tls = (javax.net.ssl.SSLSocket)
                            ((javax.net.ssl.SSLSocketFactory) javax.net.ssl.SSLSocketFactory.getDefault())
                            .createSocket(current, endpoint.host, endpoint.port, true);
                    connection = tls;
                    synchronized (this) { socket = tls; }
                    requireOpen();
                    javax.net.ssl.SSLParameters parameters = tls.getSSLParameters();
                    parameters.setEndpointIdentificationAlgorithm("HTTPS");
                    tls.setSSLParameters(parameters);
                    tls.setSoTimeout(remainingConnectMillis(started));
                    tls.startHandshake();
                    requireOpen();
                }
                connecting.cancel(false);
                connection.setSoTimeout(responseMillis);
                response = DEADLINES.schedule(this::expire, responseMillis, TimeUnit.MILLISECONDS);
                // Create and timestamp the authenticated frame after connection, never while queued.
                byte[] frame = request.create().getBytes(StandardCharsets.UTF_8);
                if (frame.length == 0 || frame.length > maxRequestBytes)
                    throw new IOException("Некорректный размер запроса к Windows-мосту.");
                requireOpen();
                if (endpoint.https) {
                    BridgeHttp.write(connection.getOutputStream(), endpoint, frame);
                    String result = BridgeHttp.read(connection.getInputStream(), maxResponseBytes);
                    requireOpen();
                    return result;
                }
                current.getOutputStream().write(frame);
                current.getOutputStream().write('\n');
                current.getOutputStream().flush();
                BufferedInputStream input = new BufferedInputStream(current.getInputStream());
                ByteArrayOutputStream bytes = new ByteArrayOutputStream(Math.min(maxResponseBytes, 8192));
                boolean complete = false;
                for (int i = 0; i < maxResponseBytes; i++) {
                    int next = input.read();
                    if (next < 0) break;
                    if (next == '\n') { complete = true; break; }
                    if (next != '\r') bytes.write(next);
                }
                requireOpen();
                if (!complete || bytes.size() == 0)
                    throw new IOException("Windows-мост не передал полный ответ допустимого размера.");
                return new String(bytes.toByteArray(), StandardCharsets.UTF_8);
            } catch (IOException error) {
                requireOpen();
                throw error;
            } finally {
                whole.cancel(false);
                connecting.cancel(false);
                if (response != null) response.cancel(false);
                close(connection);
                close(current);
                synchronized (this) { socket = null; }
            }
        }

        private static void close(Socket socket) {
            if (socket != null) try { socket.close(); } catch (IOException ignored) { }
        }
    }
}
