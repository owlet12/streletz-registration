// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;

import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.security.KeyStore;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.*;
import javax.net.ssl.*;

/** Loopback HTTPS only. Test trust is isolated to this JVM; production uses the Android trust store. */
public final class HttpsBridgeSelfTest {
    private static int checks;
    private static SSLContext trusted;
    public static void main(String[] args)throws Exception {
        endpoints();framing();
        SSLContext original=SSLContext.getDefault();
        try {
            trusted=testContext();SSLContext.setDefault(trusted);
            exchange();rejections();deadlines();
            SSLContext.setDefault(original);
            try(Peer peer=new Peer((s,r)->{throw new AssertionError("untrusted certificate sent request");})) {
                expect(SSLException.class,()->new BridgeTransport.Exchange(3000,2000,5000)
                        .perform(peer.url(),443,()->"{}",1024,1024));
                check(peer.requests.get()==0,"untrusted test certificate rejected by normal trust store");
            }
        } finally {SSLContext.setDefault(original);}
        System.out.println("HttpsBridgeSelfTest: "+checks+" checks passed (TLS identity, HTTP framing, one POST, deadlines, abort, limits)");
    }
    private static void endpoints() {
        BridgeEndpoint tcp=BridgeEndpoint.parse(" 192.168.0.244 ",23019);
        check(!tcp.https&&tcp.port==23019&&tcp.host.equals("192.168.0.244"),"legacy TCP retained");
        check(!BridgeEndpoint.parse("bridge.keenetic.pro",23019).https,"bare domain remains explicit TCP");
        BridgeEndpoint https=BridgeEndpoint.parse("HTTPS://Bridge.Keenetic.pro",0);
        check(https.https&&https.port==443&&https.address.equals("https://bridge.keenetic.pro/streletz-bridge/"),"HTTPS default route and port");
        check(BridgeEndpoint.parse("https://host:8443/custom/",23019).path.equals("/custom/"),"custom gateway route preserved");
        check(BridgeEndpoint.parse("https://host:8443/custom/",23019).port==8443,"HTTPS port comes from URL");
        check(BridgeEndpoint.parse("https://host/мост",443).path.equals("/%D0%BC%D0%BE%D1%81%D1%82"),"Unicode route is encoded for HTTP");
        check(BridgeEndpoint.display(https.address,443).equals(https.address),"HTTPS display never appends TCP port");
        for(String bad:new String[]{"","http://host","https://user:password@host","https://host/?token=x","https://host/#fragment","https://host:0","https://host:65536","host/route","host\r\nInjected: yes","https://","host:123"}) {
            try{BridgeEndpoint.parse(bad,23019);throw new AssertionError("accepted "+bad);}catch(IllegalArgumentException expected){checks++;}
        }
    }
    private static String decode(String wire,int limit)throws Exception {
        return BridgeHttp.read(new ByteArrayInputStream(wire.getBytes(StandardCharsets.US_ASCII)),limit);
    }
    private static String headers(String extra){return "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n"+extra+"\r\n";}
    private static void framing()throws Exception {
        check(decode(headers("Content-Length: 2\r\n")+"{}",2).equals("{}"),"fixed length at limit");
        check(decode(headers("Transfer-Encoding: chunked\r\n")+"1\r\n{\r\n1;test=yes\r\n}\r\n0\r\nX-Finish: yes\r\n\r\n",2).equals("{}"),"chunked including trailer");
        check(decode(headers("")+"{}",2).equals("{}"),"close-delimited response");
        for(String wire:new String[]{headers("Content-Length: 3\r\n")+"{}",headers("Content-Length: 99999999\r\n"),headers("Content-Length: -1\r\n"),headers("Content-Length: 0\r\n"),headers("Content-Length: 2\r\nContent-Length: 2\r\n")+"{}",headers("Content-Length: 2\r\nTransfer-Encoding: chunked\r\n")+"{}",headers("Transfer-Encoding: gzip\r\n"),headers("Transfer-Encoding: chunked\r\n")+"100000\r\n",headers("Transfer-Encoding: chunked\r\n")+"1\r\n{\nx",headers("Content-Encoding: gzip\r\n")+"{}","HTTP/1.1 200 OK\r\nContent-Type: text/html\r\n\r\n<form/>","HTTP/1.1 302 Found\r\nLocation: https://other/\r\n\r\n","HTTP/1.1 502 Bad Gateway\r\n\r\n",headers("")+"{}x"}) {
            expect(IOException.class,()->decode(wire,2));
        }
        expect(IOException.class,()->decode("HTTP/1.1 200 OK\r\nX-Large: "+"x".repeat(33000),40000));
    }
    private static void exchange()throws Exception {
        String response="{\"data\":\""+"x".repeat(1200000)+"\"}";
        AtomicInteger frameBuilt=new AtomicInteger();
        try(Peer peer=new Peer((socket,request)->{
            check(request.startsWith("POST /gateway/ HTTP/1.1\r\n"),"correct gateway method/path");
            check(request.endsWith("\r\n\r\n{\"sealed\":true}"),"single unchanged envelope without newline");
            byte[] body=response.getBytes(StandardCharsets.UTF_8);
            socket.getOutputStream().write((headers("Content-Length: "+body.length+"\r\n")+response).getBytes(StandardCharsets.UTF_8));
        })) {
            BridgeTransport.Exchange call=new BridgeTransport.Exchange(3000,4000,7000);
            String result=call.perform(peer.url(),23019,()->{frameBuilt.incrementAndGet();return "{\"sealed\":true}";},1024,2000000);
            check(result.equals(response),"large project HTTPS response retained");
            check(peer.requests.get()==1&&frameBuilt.get()==1,"one authenticated frame and one HTTP request");
            expect(IOException.class,()->call.perform(peer.url(),23019,()->"{}",1024,1024));
            peer.assertHealthy();
        }
    }
    private static void rejections()throws Exception {
        try(Peer peer=new Peer((s,r)->s.getOutputStream().write("HTTP/1.1 302 Found\r\nLocation: /retry\r\n\r\n".getBytes(StandardCharsets.US_ASCII)))) {
            expect(IOException.class,()->new BridgeTransport.Exchange().perform(peer.url(),23019,()->"{}",1024,1024));
            check(peer.requests.get()==1,"redirect does not resend command");
        }
        AtomicInteger built=new AtomicInteger();
        try(Peer peer=new Peer((s,r)->{throw new AssertionError("hostname failure sent request");})) {
            expect(SSLException.class,()->new BridgeTransport.Exchange(2000,2000,4000)
                    .perform(peer.url().replace("localhost","127.0.0.1"),443,()->{built.incrementAndGet();return "{}";},1024,1024));
            check(built.get()==0&&peer.requests.get()==0,"wrong certificate hostname rejected before creating frame");
        }
        try(ServerSocket server=new ServerSocket(0,1,InetAddress.getLoopbackAddress())) {
            Thread silent=new Thread(()->{try(Socket s=server.accept()){Thread.sleep(1200);}catch(Exception ignored){}});silent.setDaemon(true);silent.start();
            long time=System.nanoTime();
            expect(SocketTimeoutException.class,()->new BridgeTransport.Exchange(180,1000,1500)
                    .perform("https://localhost:"+server.getLocalPort()+"/",443,()->{built.incrementAndGet();return "{}";},1024,1024));
            check(TimeUnit.NANOSECONDS.toMillis(System.nanoTime()-time)<1100,"TLS handshake shares connection deadline");
            check(built.get()==0,"TLS timeout sends no frame");
        }
    }
    private static void deadlines()throws Exception {
        try(Peer peer=new Peer((s,r)->{Thread.sleep(1500);})) {
            long time=System.nanoTime();
            expect(SocketTimeoutException.class,()->new BridgeTransport.Exchange(2000,180,2500).perform(peer.url(),443,()->"{}",1024,1024));
            check(TimeUnit.NANOSECONDS.toMillis(System.nanoTime()-time)<1300,"HTTP read deadline bounded");
        }
        CountDownLatch received=new CountDownLatch(1);
        try(Peer peer=new Peer((s,r)->{received.countDown();Thread.sleep(1500);})) {
            BridgeTransport.Exchange call=new BridgeTransport.Exchange(2000,4000,6000);
            ExecutorService executor=Executors.newSingleThreadExecutor();
            try {
                Future<String> pending=executor.submit(()->call.perform(peer.url(),443,()->"{}",1024,1024));
                check(received.await(3,TimeUnit.SECONDS),"request received before cancellation");call.abort();
                try{pending.get(1,TimeUnit.SECONDS);throw new AssertionError("abort ignored");}
                catch(ExecutionException error){check(error.getCause() instanceof BridgeTransport.AbortedException,"abort closes TLS read");}
                check(peer.requests.get()==1,"abort never resends request");
            }finally{executor.shutdownNow();}
        }
    }
    private static SSLContext testContext()throws Exception {
        Path dir=Files.createTempDirectory("bridge-test-tls-");Path key=dir.resolve("test.p12");
        String executable=new File(System.getProperty("java.home"),"bin/keytool.exe").getPath();
        Process process=new ProcessBuilder(executable,"-genkeypair","-alias","test","-keyalg","RSA","-keysize","2048","-validity","2","-dname","CN=localhost","-ext","SAN=dns:localhost","-storetype","PKCS12","-keystore",key.toString(),"-storepass","test-only-password","-keypass","test-only-password").redirectErrorStream(true).start();
        process.getInputStream().readAllBytes();if(process.waitFor()!=0)throw new IOException("test certificate generation failed");
        KeyStore store=KeyStore.getInstance("PKCS12");try(InputStream input=Files.newInputStream(key)){store.load(input,"test-only-password".toCharArray());}
        KeyManagerFactory keys=KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm());keys.init(store,"test-only-password".toCharArray());
        TrustManagerFactory trust=TrustManagerFactory.getInstance(TrustManagerFactory.getDefaultAlgorithm());trust.init(store);
        SSLContext context=SSLContext.getInstance("TLS");context.init(keys.getKeyManagers(),trust.getTrustManagers(),null);
        Files.delete(key);Files.delete(dir);return context;
    }
    interface Reply{void run(SSLSocket socket,String request)throws Exception;}
    static final class Peer implements AutoCloseable {
        final SSLServerSocket server;final AtomicInteger requests=new AtomicInteger();final AtomicReference<Throwable> failure=new AtomicReference<>();final Thread worker;
        Peer(Reply reply)throws Exception {
            server=(SSLServerSocket)trusted.getServerSocketFactory().createServerSocket(0,1,InetAddress.getLoopbackAddress());
            worker=new Thread(()->{
                try(SSLSocket socket=(SSLSocket)server.accept()) {
                    socket.setSoTimeout(5000);InputStream in=socket.getInputStream();ByteArrayOutputStream header=new ByteArrayOutputStream();
                    while(!header.toString("US-ASCII").endsWith("\r\n\r\n")){int n=in.read();if(n<0)throw new EOFException();header.write(n);if(header.size()>8192)throw new IOException("too large");}
                    String text=header.toString("US-ASCII");int length=-1;
                    for(String row:text.split("\r\n"))if(row.startsWith("Content-Length: "))length=Integer.parseInt(row.substring(16));
                    if(length<0)throw new IOException("missing length");byte[] body=in.readNBytes(length);if(body.length!=length)throw new EOFException();
                    requests.incrementAndGet();reply.run(socket,text+new String(body,StandardCharsets.UTF_8));socket.getOutputStream().flush();
                }catch(SSLException|EOFException expected){}catch(Throwable error){failure.set(error);}
            });worker.setDaemon(true);worker.start();
        }
        String url(){return "https://localhost:"+server.getLocalPort()+"/gateway/";}
        void assertHealthy()throws Exception{worker.join(2000);if(failure.get()!=null)throw new AssertionError(failure.get());}
        public void close()throws Exception{server.close();}
    }
    interface Action{void run()throws Exception;}
    static void expect(Class<? extends Throwable> type,Action action)throws Exception {
        try{action.run();throw new AssertionError("Expected "+type);}catch(Exception error){if(!type.isInstance(error))throw error;checks++;}
    }
    static synchronized void check(boolean condition,String message){if(!condition)throw new AssertionError(message);checks++;}
}
