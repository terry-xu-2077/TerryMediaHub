/*
 * Decompiled with CFR 0.152.
 */
package com.github.catvod.crawler;

import com.github.catvod.crawler.SpiderDebug;
import java.net.Inet6Address;
import java.net.InetAddress;
import java.net.NetworkInterface;
import java.util.Enumeration;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

public class SpiderApi {
    public void log(String string) {
        SpiderDebug.log(string);
    }

    public String getAddress(boolean bl) {
        try {
            Enumeration<NetworkInterface> enumeration = NetworkInterface.getNetworkInterfaces();
            while (enumeration.hasMoreElements()) {
                NetworkInterface networkInterface = enumeration.nextElement();
                if (!networkInterface.isUp() || networkInterface.isLoopback() || networkInterface.isVirtual()) continue;
                Enumeration<InetAddress> enumeration2 = networkInterface.getInetAddresses();
                while (enumeration2.hasMoreElements()) {
                    boolean bl2;
                    InetAddress inetAddress = enumeration2.nextElement();
                    if (inetAddress.isLoopbackAddress() || inetAddress.isLinkLocalAddress() || (bl2 = inetAddress instanceof Inet6Address) != bl) continue;
                    return inetAddress.getHostAddress();
                }
            }
        }
        catch (Throwable throwable) {
            // empty catch block
        }
        return "127.0.0.1";
    }

    public String getPort() {
        return "9978";
    }

    public int getScreenOrientation() {
        return 0;
    }

    public String lrcToSrt(String string) {
        if (string == null || string.isEmpty()) {
            return "";
        }
        StringBuilder stringBuilder = new StringBuilder();
        int n = 1;
        for (String string2 : string.split("\\r?\\n")) {
            Matcher matcher;
            String string3 = string2.trim();
            if (string3.isEmpty() || !(matcher = Pattern.compile("^\\[(\\d{1,2}):(\\d{1,2})(?:[.:](\\d{1,3}))?\\](.*)$").matcher(string3)).find()) continue;
            int n2 = Integer.parseInt(matcher.group(1));
            int n3 = Integer.parseInt(matcher.group(2));
            String string4 = matcher.group(3) == null ? "0" : matcher.group(3);
            int n4 = Integer.parseInt((string4 + "00").substring(0, 3));
            String string5 = matcher.group(4).trim();
            long l = ((long)n2 * 60L + (long)n3) * 1000L + (long)n4;
            long l2 = l + 3000L;
            stringBuilder.append(n++).append('\n').append(SpiderApi.fmt(l)).append(" --> ").append(SpiderApi.fmt(l2)).append('\n').append(string5).append("\n\n");
        }
        return stringBuilder.toString();
    }

    private static String fmt(long l) {
        long l2 = l / 3600000L;
        long l3 = l % 3600000L / 60000L;
        long l4 = l % 60000L / 1000L;
        long l5 = l % 1000L;
        return String.format("%02d:%02d:%02d,%03d", l2, l3, l4, l5);
    }
}

