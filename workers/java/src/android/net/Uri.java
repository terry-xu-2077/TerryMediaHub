/*
 * Decompiled with CFR 0.152.
 */
package android.net;

import android.net.Uri$Builder;
import java.io.File;
import java.net.URI;
import java.net.URLDecoder;
import java.net.URLEncoder;
import java.util.LinkedHashMap;
import java.util.Map;

public class Uri {
    private final String raw;

    private Uri(String string) {
        this.raw = string;
    }

    public static Uri parse(String string) {
        return string == null ? null : new Uri(string);
    }

    public static Uri fromFile(File file) {
        return file == null ? null : new Uri("file://" + file.getAbsolutePath().replace('\\', '/'));
    }

    public static Uri fromParts(String string, String string2, String string3) {
        return new Uri(string + ":" + string2 + (string3 == null ? "" : "#" + string3));
    }

    public static String encode(String string) {
        try {
            return URLEncoder.encode(string, "UTF-8");
        }
        catch (Exception exception) {
            return string;
        }
    }

    public static String encode(String string, String string2) {
        try {
            return URLEncoder.encode(string, "UTF-8");
        }
        catch (Exception exception) {
            return string;
        }
    }

    public String getScheme() {
        int n = this.raw.indexOf(58);
        return n < 0 ? null : this.raw.substring(0, n);
    }

    public String getHost() {
        try {
            return new URI(this.raw).getHost();
        }
        catch (Exception exception) {
            return null;
        }
    }

    public String getPath() {
        try {
            return new URI(this.raw).getPath();
        }
        catch (Exception exception) {
            return null;
        }
    }

    public String getLastPathSegment() {
        String string = this.getPath();
        if (string == null) {
            return null;
        }
        String[] stringArray = string.split("/");
        return stringArray.length == 0 ? null : stringArray[stringArray.length - 1];
    }

    public String getSchemeSpecificPart() {
        int n = this.raw.indexOf(58);
        return n < 0 ? this.raw : this.raw.substring(n + 1);
    }

    public Uri$Builder buildUpon() {
        return new Uri$Builder(this);
    }

    public String getQueryParameter(String string) {
        Map<String, String> map = this.query();
        return map.get(string);
    }

    public Map<String, String> query() {
        LinkedHashMap<String, String> linkedHashMap = new LinkedHashMap<String, String>();
        int n = this.raw.indexOf(63);
        if (n < 0) {
            return linkedHashMap;
        }
        String string = this.raw.substring(n + 1);
        int n2 = string.indexOf(35);
        if (n2 >= 0) {
            string = string.substring(0, n2);
        }
        for (String string2 : string.split("&")) {
            if (string2.isEmpty()) continue;
            int n3 = string2.indexOf(61);
            try {
                if (n3 < 0) {
                    linkedHashMap.put(URLDecoder.decode(string2, "UTF-8"), "");
                    continue;
                }
                linkedHashMap.put(URLDecoder.decode(string2.substring(0, n3), "UTF-8"), URLDecoder.decode(string2.substring(n3 + 1), "UTF-8"));
            }
            catch (Exception exception) {
                // empty catch block
            }
        }
        return linkedHashMap;
    }

    public String toString() {
        return this.raw;
    }
}

