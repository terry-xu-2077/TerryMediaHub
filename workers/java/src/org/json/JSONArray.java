/*
 * Decompiled with CFR 0.152.
 */
package org.json;

import java.lang.reflect.Array;
import java.util.ArrayList;
import java.util.Collection;
import java.util.Iterator;
import java.util.List;
import org.json.JSON;
import org.json.JSONException;
import org.json.JSONObject;
import org.json.JSONStringer;
import org.json.JSONTokener;

public class JSONArray {
    private final List<Object> values;

    public JSONArray() {
        this.values = new ArrayList<Object>();
    }

    public JSONArray(Collection collection) {
        this();
        if (collection != null) {
            Iterator iterator = collection.iterator();
            while (iterator.hasNext()) {
                this.put(JSONObject.wrap(iterator.next()));
            }
        }
    }

    public JSONArray(JSONTokener jSONTokener) throws JSONException {
        Object object = jSONTokener.nextValue();
        if (!(object instanceof JSONArray)) {
            throw JSON.typeMismatch(object, "JSONArray");
        }
        this.values = ((JSONArray)object).values;
    }

    public JSONArray(String string) throws JSONException {
        this(new JSONTokener(string));
    }

    public JSONArray(Object object) throws JSONException {
        if (!object.getClass().isArray()) {
            throw new JSONException("Not a primitive array: " + object.getClass());
        }
        int n = Array.getLength(object);
        this.values = new ArrayList<Object>(n);
        for (int i = 0; i < n; ++i) {
            this.put(JSONObject.wrap(Array.get(object, i)));
        }
    }

    public int length() {
        return this.values.size();
    }

    public JSONArray put(boolean bl) {
        this.values.add(bl);
        return this;
    }

    public JSONArray put(double d) throws JSONException {
        this.values.add(JSON.checkDouble(d));
        return this;
    }

    public JSONArray put(int n) {
        this.values.add(n);
        return this;
    }

    public JSONArray put(long l) {
        this.values.add(l);
        return this;
    }

    public JSONArray put(Object object) {
        this.values.add(object);
        return this;
    }

    public JSONArray put(int n, boolean bl) throws JSONException {
        return this.put(n, (Object)bl);
    }

    public JSONArray put(int n, double d) throws JSONException {
        return this.put(n, (Object)d);
    }

    public JSONArray put(int n, int n2) throws JSONException {
        return this.put(n, (Object)n2);
    }

    public JSONArray put(int n, long l) throws JSONException {
        return this.put(n, (Object)l);
    }

    public JSONArray put(int n, Object object) throws JSONException {
        if (object instanceof Number) {
            JSON.checkDouble(((Number)object).doubleValue());
        }
        while (this.values.size() <= n) {
            this.values.add(null);
        }
        this.values.set(n, object);
        return this;
    }

    public boolean isNull(int n) {
        Object object = this.opt(n);
        return object == null || object == JSONObject.NULL;
    }

    public Object get(int n) throws JSONException {
        try {
            Object object = this.values.get(n);
            if (object == null) {
                throw new JSONException("Value at " + n + " is null.");
            }
            return object;
        }
        catch (IndexOutOfBoundsException indexOutOfBoundsException) {
            throw new JSONException("Index " + n + " out of range [0.." + this.values.size() + ")");
        }
    }

    public Object opt(int n) {
        if (n < 0 || n >= this.values.size()) {
            return null;
        }
        return this.values.get(n);
    }

    public Object remove(int n) {
        if (n < 0 || n >= this.values.size()) {
            return null;
        }
        return this.values.remove(n);
    }

    public boolean getBoolean(int n) throws JSONException {
        Object object = this.get(n);
        Boolean bl = JSON.toBoolean(object);
        if (bl == null) {
            throw JSON.typeMismatch(n, object, "boolean");
        }
        return bl;
    }

    public boolean optBoolean(int n) {
        return this.optBoolean(n, false);
    }

    public boolean optBoolean(int n, boolean bl) {
        Object object = this.opt(n);
        Boolean bl2 = JSON.toBoolean(object);
        return bl2 != null ? bl2 : bl;
    }

    public double getDouble(int n) throws JSONException {
        Object object = this.get(n);
        Double d = JSON.toDouble(object);
        if (d == null) {
            throw JSON.typeMismatch(n, object, "double");
        }
        return d;
    }

    public double optDouble(int n) {
        return this.optDouble(n, Double.NaN);
    }

    public double optDouble(int n, double d) {
        Object object = this.opt(n);
        Double d2 = JSON.toDouble(object);
        return d2 != null ? d2 : d;
    }

    public int getInt(int n) throws JSONException {
        Object object = this.get(n);
        Integer n2 = JSON.toInteger(object);
        if (n2 == null) {
            throw JSON.typeMismatch(n, object, "int");
        }
        return n2;
    }

    public int optInt(int n) {
        return this.optInt(n, 0);
    }

    public int optInt(int n, int n2) {
        Object object = this.opt(n);
        Integer n3 = JSON.toInteger(object);
        return n3 != null ? n3 : n2;
    }

    public long getLong(int n) throws JSONException {
        Object object = this.get(n);
        Long l = JSON.toLong(object);
        if (l == null) {
            throw JSON.typeMismatch(n, object, "long");
        }
        return l;
    }

    public long optLong(int n) {
        return this.optLong(n, 0L);
    }

    public long optLong(int n, long l) {
        Object object = this.opt(n);
        Long l2 = JSON.toLong(object);
        return l2 != null ? l2 : l;
    }

    public String getString(int n) throws JSONException {
        Object object = this.get(n);
        String string = JSON.toString(object);
        if (string == null) {
            throw JSON.typeMismatch(n, object, "String");
        }
        return string;
    }

    public String optString(int n) {
        return this.optString(n, "");
    }

    public String optString(int n, String string) {
        Object object = this.opt(n);
        String string2 = JSON.toString(object);
        return string2 != null ? string2 : string;
    }

    public JSONArray getJSONArray(int n) throws JSONException {
        Object object = this.get(n);
        if (object instanceof JSONArray) {
            return (JSONArray)object;
        }
        throw JSON.typeMismatch(n, object, "JSONArray");
    }

    public JSONArray optJSONArray(int n) {
        Object object = this.opt(n);
        return object instanceof JSONArray ? (JSONArray)object : null;
    }

    public JSONObject getJSONObject(int n) throws JSONException {
        Object object = this.get(n);
        if (object instanceof JSONObject) {
            return (JSONObject)object;
        }
        throw JSON.typeMismatch(n, object, "JSONObject");
    }

    public JSONObject optJSONObject(int n) {
        Object object = this.opt(n);
        return object instanceof JSONObject ? (JSONObject)object : null;
    }

    public JSONObject toJSONObject(JSONArray jSONArray) throws JSONException {
        JSONObject jSONObject = new JSONObject();
        int n = Math.min(jSONArray.length(), this.values.size());
        if (n == 0) {
            return null;
        }
        for (int i = 0; i < n; ++i) {
            String string = JSON.toString(jSONArray.opt(i));
            jSONObject.put(string, this.opt(i));
        }
        return jSONObject;
    }

    public String join(String string) throws JSONException {
        JSONStringer jSONStringer = new JSONStringer();
        jSONStringer.open(JSONStringer.Scope.NULL, "");
        int n = this.values.size();
        for (int i = 0; i < n; ++i) {
            if (i > 0) {
                jSONStringer.out.append(string);
            }
            jSONStringer.value(this.values.get(i));
        }
        jSONStringer.close(JSONStringer.Scope.NULL, JSONStringer.Scope.NULL, "");
        return jSONStringer.out.toString();
    }

    public String toString() {
        try {
            JSONStringer jSONStringer = new JSONStringer();
            this.writeTo(jSONStringer);
            return jSONStringer.toString();
        }
        catch (JSONException jSONException) {
            return null;
        }
    }

    public String toString(int n) throws JSONException {
        JSONStringer jSONStringer = new JSONStringer(n);
        this.writeTo(jSONStringer);
        return jSONStringer.toString();
    }

    void writeTo(JSONStringer jSONStringer) throws JSONException {
        jSONStringer.array();
        for (Object object : this.values) {
            jSONStringer.value(object);
        }
        jSONStringer.endArray();
    }

    public boolean equals(Object object) {
        return object instanceof JSONArray && ((JSONArray)object).values.equals(this.values);
    }

    public int hashCode() {
        return this.values.hashCode();
    }
}

