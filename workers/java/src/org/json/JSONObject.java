/*
 * Decompiled with CFR 0.152.
 */
package org.json;

import java.util.ArrayList;
import java.util.Collection;
import java.util.HashMap;
import java.util.Iterator;
import java.util.Map;
import java.util.Set;
import org.json.JSON;
import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONStringer;
import org.json.JSONTokener;

public class JSONObject {
    private static final Double NEGATIVE_ZERO = -0.0;
    public static final Object NULL = new Object(){

        public boolean equals(Object object) {
            return object == this || object == null;
        }

        public String toString() {
            return "null";
        }
    };
    private final Map<String, Object> nameValuePairs;

    public JSONObject() {
        this.nameValuePairs = new HashMap<String, Object>();
    }

    public JSONObject(Map map) {
        this();
        Map map2 = map;
        for (Object o : map2.entrySet()) {
            Map.Entry entry = (Map.Entry) o;
            Map.Entry entry2 = entry;
            String string = (String)entry2.getKey();
            if (string == null) {
                throw new NullPointerException("key == null");
            }
            this.nameValuePairs.put(string, JSONObject.wrap(entry2.getValue()));
        }
    }

    public JSONObject(JSONTokener jSONTokener) throws JSONException {
        Object object = jSONTokener.nextValue();
        if (!(object instanceof JSONObject)) {
            throw JSON.typeMismatch(object, "JSONObject");
        }
        this.nameValuePairs = ((JSONObject)object).nameValuePairs;
    }

    public JSONObject(String string) throws JSONException {
        this(new JSONTokener(string));
    }

    public JSONObject(JSONObject jSONObject, String[] stringArray) throws JSONException {
        this();
        for (String string : stringArray) {
            Object object = jSONObject.opt(string);
            if (object == null) continue;
            this.nameValuePairs.put(string, object);
        }
    }

    public int length() {
        return this.nameValuePairs.size();
    }

    public JSONObject put(String string, boolean bl) throws JSONException {
        this.nameValuePairs.put(this.checkName(string), bl);
        return this;
    }

    public JSONObject put(String string, double d) throws JSONException {
        this.nameValuePairs.put(this.checkName(string), JSON.checkDouble(d));
        return this;
    }

    public JSONObject put(String string, int n) throws JSONException {
        this.nameValuePairs.put(this.checkName(string), n);
        return this;
    }

    public JSONObject put(String string, long l) throws JSONException {
        this.nameValuePairs.put(this.checkName(string), l);
        return this;
    }

    public JSONObject put(String string, Object object) throws JSONException {
        if (object == null) {
            this.nameValuePairs.remove(string);
            return this;
        }
        if (object instanceof Number) {
            JSON.checkDouble(((Number)object).doubleValue());
        }
        this.nameValuePairs.put(this.checkName(string), object);
        return this;
    }

    public JSONObject putOpt(String string, Object object) throws JSONException {
        if (string == null || object == null) {
            return this;
        }
        return this.put(string, object);
    }

    public JSONObject accumulate(String string, Object object) throws JSONException {
        Object object2 = this.nameValuePairs.get(this.checkName(string));
        if (object2 == null) {
            return this.put(string, object);
        }
        if (object instanceof Number) {
            JSON.checkDouble(((Number)object).doubleValue());
        }
        if (object2 instanceof JSONArray) {
            JSONArray jSONArray = (JSONArray)object2;
            jSONArray.put(object);
        } else {
            JSONArray jSONArray = new JSONArray();
            jSONArray.put(object2);
            jSONArray.put(object);
            this.nameValuePairs.put(string, jSONArray);
        }
        return this;
    }

    String checkName(String string) throws JSONException {
        if (string == null) {
            throw new JSONException("Names must be non-null");
        }
        return string;
    }

    public Object remove(String string) {
        return this.nameValuePairs.remove(string);
    }

    public boolean isNull(String string) {
        Object object = this.nameValuePairs.get(string);
        return object == null || object == NULL;
    }

    public boolean has(String string) {
        return this.nameValuePairs.containsKey(string);
    }

    public Object get(String string) throws JSONException {
        Object object = this.nameValuePairs.get(string);
        if (object == null) {
            throw new JSONException("No value for " + string);
        }
        return object;
    }

    public Object opt(String string) {
        return this.nameValuePairs.get(string);
    }

    public boolean getBoolean(String string) throws JSONException {
        Object object = this.get(string);
        Boolean bl = JSON.toBoolean(object);
        if (bl == null) {
            throw JSON.typeMismatch(string, object, "boolean");
        }
        return bl;
    }

    public boolean optBoolean(String string) {
        return this.optBoolean(string, false);
    }

    public boolean optBoolean(String string, boolean bl) {
        Object object = this.opt(string);
        Boolean bl2 = JSON.toBoolean(object);
        return bl2 != null ? bl2 : bl;
    }

    public double getDouble(String string) throws JSONException {
        Object object = this.get(string);
        Double d = JSON.toDouble(object);
        if (d == null) {
            throw JSON.typeMismatch(string, object, "double");
        }
        return d;
    }

    public double optDouble(String string) {
        return this.optDouble(string, Double.NaN);
    }

    public double optDouble(String string, double d) {
        Object object = this.opt(string);
        Double d2 = JSON.toDouble(object);
        return d2 != null ? d2 : d;
    }

    public int getInt(String string) throws JSONException {
        Object object = this.get(string);
        Integer n = JSON.toInteger(object);
        if (n == null) {
            throw JSON.typeMismatch(string, object, "int");
        }
        return n;
    }

    public int optInt(String string) {
        return this.optInt(string, 0);
    }

    public int optInt(String string, int n) {
        Object object = this.opt(string);
        Integer n2 = JSON.toInteger(object);
        return n2 != null ? n2 : n;
    }

    public long getLong(String string) throws JSONException {
        Object object = this.get(string);
        Long l = JSON.toLong(object);
        if (l == null) {
            throw JSON.typeMismatch(string, object, "long");
        }
        return l;
    }

    public long optLong(String string) {
        return this.optLong(string, 0L);
    }

    public long optLong(String string, long l) {
        Object object = this.opt(string);
        Long l2 = JSON.toLong(object);
        return l2 != null ? l2 : l;
    }

    public String getString(String string) throws JSONException {
        Object object = this.get(string);
        String string2 = JSON.toString(object);
        if (string2 == null) {
            throw JSON.typeMismatch(string, object, "String");
        }
        return string2;
    }

    public Set<String> keySet() {
        return this.nameValuePairs.keySet();
    }

    public String optString(String string) {
        return this.optString(string, "");
    }

    public String optString(String string, String string2) {
        Object object = this.opt(string);
        String string3 = JSON.toString(object);
        return string3 != null ? string3 : string2;
    }

    public JSONArray getJSONArray(String string) throws JSONException {
        Object object = this.get(string);
        if (object instanceof JSONArray) {
            return (JSONArray)object;
        }
        throw JSON.typeMismatch(string, object, "JSONArray");
    }

    public JSONArray optJSONArray(String string) {
        Object object = this.opt(string);
        return object instanceof JSONArray ? (JSONArray)object : null;
    }

    public JSONObject getJSONObject(String string) throws JSONException {
        Object object = this.get(string);
        if (object instanceof JSONObject) {
            return (JSONObject)object;
        }
        throw JSON.typeMismatch(string, object, "JSONObject");
    }

    public JSONObject optJSONObject(String string) {
        Object object = this.opt(string);
        return object instanceof JSONObject ? (JSONObject)object : null;
    }

    public JSONArray toJSONArray(JSONArray jSONArray) throws JSONException {
        JSONArray jSONArray2 = new JSONArray();
        if (jSONArray == null) {
            return null;
        }
        int n = jSONArray.length();
        if (n == 0) {
            return null;
        }
        for (int i = 0; i < n; ++i) {
            String string = JSON.toString(jSONArray.opt(i));
            jSONArray2.put(this.opt(string));
        }
        return jSONArray2;
    }

    public Iterator keys() {
        return this.nameValuePairs.keySet().iterator();
    }

    public JSONArray names() {
        return this.nameValuePairs.isEmpty() ? null : new JSONArray(new ArrayList<String>(this.nameValuePairs.keySet()));
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
        jSONStringer.object();
        for (Map.Entry<String, Object> entry : this.nameValuePairs.entrySet()) {
            jSONStringer.key(entry.getKey()).value(entry.getValue());
        }
        jSONStringer.endObject();
    }

    public static String numberToString(Number number) throws JSONException {
        if (number == null) {
            throw new JSONException("Number must be non-null");
        }
        double d = number.doubleValue();
        JSON.checkDouble(d);
        if (number.equals(NEGATIVE_ZERO)) {
            return "-0";
        }
        long l = number.longValue();
        if (d == (double)l) {
            return Long.toString(l);
        }
        return number.toString();
    }

    public static String quote(String string) {
        if (string == null) {
            return "\"\"";
        }
        try {
            JSONStringer jSONStringer = new JSONStringer();
            jSONStringer.open(JSONStringer.Scope.NULL, "");
            jSONStringer.value(string);
            jSONStringer.close(JSONStringer.Scope.NULL, JSONStringer.Scope.NULL, "");
            return jSONStringer.toString();
        }
        catch (JSONException jSONException) {
            throw new AssertionError();
        }
    }

    public static Object wrap(Object object) {
        if (object == null) {
            return NULL;
        }
        if (object instanceof JSONArray || object instanceof JSONObject) {
            return object;
        }
        if (object.equals(NULL)) {
            return object;
        }
        try {
            if (object instanceof Collection) {
                return new JSONArray((Collection)object);
            }
            if (object.getClass().isArray()) {
                return new JSONArray(object);
            }
            if (object instanceof Map) {
                return new JSONObject((Map)object);
            }
            if (object instanceof Boolean || object instanceof Byte || object instanceof Character || object instanceof Double || object instanceof Float || object instanceof Integer || object instanceof Long || object instanceof Short || object instanceof String) {
                return object;
            }
            if (object.getClass().getPackage().getName().startsWith("java.")) {
                return object.toString();
            }
        }
        catch (Exception exception) {
            // empty catch block
        }
        return null;
    }
}

