/*
 * Decompiled with CFR 0.152.
 */
package android.net;

import android.net.Uri;
import java.util.ArrayList;
import java.util.List;

public class Uri$Builder {
    private String scheme;
    private String authority;
    private final StringBuilder path = new StringBuilder();
    private final List<String> query = new ArrayList<String>();

    public Uri$Builder() {
    }

    Uri$Builder(Uri uri) {
        this.scheme = uri.getScheme();
        this.authority = uri.getHost();
        String string = uri.getPath();
        if (string != null) {
            this.path.append(string);
        }
    }

    public Uri$Builder scheme(String string) {
        this.scheme = string;
        return this;
    }

    public Uri$Builder authority(String string) {
        this.authority = string;
        return this;
    }

    public Uri$Builder path(String string) {
        this.path.setLength(0);
        this.path.append(string);
        return this;
    }

    public Uri$Builder encodedPath(String string) {
        return this.path(string);
    }

    public Uri$Builder appendPath(String string) {
        if (this.path.length() > 0 && this.path.charAt(this.path.length() - 1) != '/') {
            this.path.append('/');
        }
        this.path.append(string);
        return this;
    }

    public Uri$Builder appendEncodedPath(String string) {
        return this.appendPath(string);
    }

    public Uri$Builder appendQueryParameter(String string, String string2) {
        this.query.add(string + "=" + string2);
        return this;
    }

    public Uri build() {
        StringBuilder stringBuilder = new StringBuilder();
        if (this.scheme != null) {
            stringBuilder.append(this.scheme).append("://");
        }
        if (this.authority != null) {
            stringBuilder.append(this.authority);
        }
        if (this.path.length() > 0) {
            stringBuilder.append((CharSequence)this.path);
        }
        for (int i = 0; i < this.query.size(); ++i) {
            stringBuilder.append(i == 0 ? "?" : "&").append(this.query.get(i));
        }
        return Uri.parse(stringBuilder.toString());
    }

    public String toString() {
        return this.build().toString();
    }
}

