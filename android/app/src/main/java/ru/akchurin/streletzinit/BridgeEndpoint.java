// Author: owlet (https://github.com/owlet12)
// Copyright (c) 2026 owlet
package ru.akchurin.streletzinit;

import java.net.URI;
import java.util.Locale;

/** A legacy TCP host or an explicit HTTPS gateway URL. No heuristic protocol downgrade. */
final class BridgeEndpoint {
    final String address, host, path, authority;
    final int port;
    final boolean https;

    private BridgeEndpoint(String address, String host, int port, boolean https, String path, String authority) {
        this.address=address; this.host=host; this.port=port; this.https=https; this.path=path; this.authority=authority;
    }

    static BridgeEndpoint parse(String value, int tcpPort) {
        String input=value.trim();
        try {
            if (input.isEmpty() || input.chars().anyMatch(c -> Character.isWhitespace(c) || Character.isISOControl(c)))
                throw new IllegalArgumentException();
            if (input.contains("://")) {
                URI uri=new URI(input);
                if (!"https".equalsIgnoreCase(uri.getScheme()) || uri.getHost()==null || uri.getUserInfo()!=null
                        || uri.getRawFragment()!=null || uri.getRawQuery()!=null) throw new IllegalArgumentException();
                int port=uri.getPort()==-1?443:uri.getPort();
                if(port<1 || port>65535) throw new IllegalArgumentException();
                String host=uri.getHost().toLowerCase(Locale.ROOT);
                String path=new URI(uri.toASCIIString()).getRawPath();
                if(path==null || path.isEmpty() || path.equals("/")) path="/streletz-bridge/";
                String authority=host+(port==443?"":":"+port);
                return new BridgeEndpoint("https://"+authority+path,host,port,true,path,authority);
            }
            if (input.contains("/") || input.contains("@") || input.contains("?") || input.contains("#")
                    || tcpPort<1 || tcpPort>65535) throw new IllegalArgumentException();
            URI uri=new URI("tcp://"+input+":"+tcpPort);
            if (uri.getHost()==null || uri.getPort()!=tcpPort) throw new IllegalArgumentException();
            return new BridgeEndpoint(input,uri.getHost(),tcpPort,false,"",input);
        } catch (Exception error) {
            throw new IllegalArgumentException("Укажите IP/имя компьютера и TCP-порт или HTTPS-адрес шлюза без логина, параметров и фрагмента.");
        }
    }

    static String display(String host, int port) {
        return host.regionMatches(true,0,"https://",0,8)?host:host+":"+port;
    }
}
