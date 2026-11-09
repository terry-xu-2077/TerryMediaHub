/*
 * Decompiled with CFR 0.152.
 */
package org.json;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

public class JSONStringer {
    final StringBuilder out = new StringBuilder();
    private final List<Scope> stack = new ArrayList<Scope>();
    private final String indent;

    public JSONStringer() {
        this.indent = null;
    }

    JSONStringer(int n) {
        char[] cArray = new char[n];
        Arrays.fill(cArray, ' ');
        this.indent = new String(cArray);
    }

    public JSONStringer array() throws JSONException {
        return this.open(Scope.EMPTY_ARRAY, "[");
    }

    public JSONStringer endArray() throws JSONException {
        return this.close(Scope.EMPTY_ARRAY, Scope.NONEMPTY_ARRAY, "]");
    }

    public JSONStringer object() throws JSONException {
        return this.open(Scope.EMPTY_OBJECT, "{");
    }

    public JSONStringer endObject() throws JSONException {
        return this.close(Scope.EMPTY_OBJECT, Scope.NONEMPTY_OBJECT, "}");
    }

    JSONStringer open(Scope scope, String string) throws JSONException {
        if (this.stack.isEmpty() && this.out.length() > 0) {
            throw new JSONException("Nesting problem: multiple top-level roots");
        }
        this.beforeValue();
        this.stack.add(scope);
        this.out.append(string);
        return this;
    }

    JSONStringer close(Scope scope, Scope scope2, String string) throws JSONException {
        Scope scope3 = this.peek();
        if (scope3 != scope2 && scope3 != scope) {
            throw new JSONException("Nesting problem");
        }
        this.stack.remove(this.stack.size() - 1);
        if (scope3 == scope2) {
            this.newline();
        }
        this.out.append(string);
        return this;
    }

    private Scope peek() throws JSONException {
        if (this.stack.isEmpty()) {
            throw new JSONException("Nesting problem");
        }
        return this.stack.get(this.stack.size() - 1);
    }

    private void replaceTop(Scope scope) {
        this.stack.set(this.stack.size() - 1, scope);
    }

    public JSONStringer value(Object object) throws JSONException {
        if (this.stack.isEmpty()) {
            throw new JSONException("Nesting problem");
        }
        if (object instanceof JSONArray) {
            ((JSONArray)object).writeTo(this);
            return this;
        }
        if (object instanceof JSONObject) {
            ((JSONObject)object).writeTo(this);
            return this;
        }
        this.beforeValue();
        if (object == null || object instanceof Boolean || object == JSONObject.NULL) {
            this.out.append(object);
        } else if (object instanceof Number) {
            this.out.append(JSONObject.numberToString((Number)object));
        } else {
            this.string(object.toString());
        }
        return this;
    }

    public JSONStringer value(boolean bl) throws JSONException {
        if (this.stack.isEmpty()) {
            throw new JSONException("Nesting problem");
        }
        this.beforeValue();
        this.out.append(bl);
        return this;
    }

    public JSONStringer value(double d) throws JSONException {
        if (this.stack.isEmpty()) {
            throw new JSONException("Nesting problem");
        }
        this.beforeValue();
        this.out.append(JSONObject.numberToString(d));
        return this;
    }

    public JSONStringer value(long l) throws JSONException {
        if (this.stack.isEmpty()) {
            throw new JSONException("Nesting problem");
        }
        this.beforeValue();
        this.out.append(l);
        return this;
    }

    private void string(String string) {
        this.out.append("\"");
        int n = string.length();
        block8: for (int i = 0; i < n; ++i) {
            char c = string.charAt(i);
            switch (c) {
                case '\"': 
                case '/': 
                case '\\': {
                    this.out.append('\\').append(c);
                    continue block8;
                }
                case '\t': {
                    this.out.append("\\t");
                    continue block8;
                }
                case '\b': {
                    this.out.append("\\b");
                    continue block8;
                }
                case '\n': {
                    this.out.append("\\n");
                    continue block8;
                }
                case '\r': {
                    this.out.append("\\r");
                    continue block8;
                }
                case '\f': {
                    this.out.append("\\f");
                    continue block8;
                }
                default: {
                    if (c <= '\u001f') {
                        this.out.append(String.format("\\u%04x", Character.valueOf(c)));
                        continue block8;
                    }
                    this.out.append(c);
                }
            }
        }
        this.out.append("\"");
    }

    private void newline() {
        if (this.indent == null) {
            return;
        }
        this.out.append("\n");
        for (int i = 0; i < this.stack.size(); ++i) {
            this.out.append(this.indent);
        }
    }

    public JSONStringer key(String string) throws JSONException {
        if (string == null) {
            throw new JSONException("Names must be non-null");
        }
        this.beforeKey();
        this.string(string);
        return this;
    }

    private void beforeKey() throws JSONException {
        Scope scope = this.peek();
        if (scope == Scope.NONEMPTY_OBJECT) {
            this.out.append(',');
        } else if (scope != Scope.EMPTY_OBJECT) {
            throw new JSONException("Nesting problem");
        }
        this.newline();
        this.replaceTop(Scope.DANGLING_KEY);
    }

    private void beforeValue() throws JSONException {
        if (this.stack.isEmpty()) {
            return;
        }
        Scope scope = this.peek();
        if (scope == Scope.EMPTY_ARRAY) {
            this.replaceTop(Scope.NONEMPTY_ARRAY);
            this.newline();
        } else if (scope == Scope.NONEMPTY_ARRAY) {
            this.out.append(',');
            this.newline();
        } else if (scope == Scope.DANGLING_KEY) {
            this.out.append(this.indent == null ? ":" : ": ");
            this.replaceTop(Scope.NONEMPTY_OBJECT);
        } else if (scope != Scope.NULL) {
            throw new JSONException("Nesting problem");
        }
    }

    public String toString() {
        return this.out.length() == 0 ? null : this.out.toString();
    }

    static enum Scope {
        EMPTY_ARRAY,
        NONEMPTY_ARRAY,
        EMPTY_OBJECT,
        DANGLING_KEY,
        NONEMPTY_OBJECT,
        NULL;

    }
}

