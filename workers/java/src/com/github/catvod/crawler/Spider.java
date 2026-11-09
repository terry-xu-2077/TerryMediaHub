/*
 * Decompiled with CFR 0.152.
 */
package com.github.catvod.crawler;

import android.content.Context;
import java.util.HashMap;
import java.util.List;
import okhttp3.Dns;
import okhttp3.OkHttpClient;

public class Spider {
    public static final OkHttpClient httpClient = new OkHttpClient.Builder().followRedirects(true).followSslRedirects(true).build();

    public void init(Context context, String string) {
    }

    public void init(Context context) {
    }

    public static Dns safeDns() {
        return Dns.SYSTEM;
    }

    public String homeContent(boolean bl) {
        return "";
    }

    public String homeVideoContent() {
        return "";
    }

    public String categoryContent(String string, String string2, boolean bl, HashMap<String, String> hashMap) {
        return "";
    }

    public String detailContent(List<String> list) {
        return "";
    }

    public String searchContent(String string, boolean bl) {
        return "";
    }

    public String playerContent(String string, String string2, List<String> list) {
        return "";
    }

    public OkHttpClient client() {
        return httpClient;
    }

    public boolean manualVideoCheck() {
        return false;
    }

    public boolean isVideoFormat(String string) {
        return false;
    }

    public Object[] proxyLocal(HashMap<String, String> hashMap) {
        return null;
    }

    public String getName() {
        return "";
    }
}

