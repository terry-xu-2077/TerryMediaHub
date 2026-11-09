/*
 * Decompiled with CFR 0.152.
 */
package org.json;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

public class JSONTokener {
    private final String in;
    private int pos;

    public JSONTokener(String string) {
        if (string != null && string.startsWith("\ufeff")) {
            string = string.substring(1);
        }
        this.in = string;
    }

    public Object nextValue() throws JSONException {
        int n = this.nextCleanInternal();
        switch (n) {
            case -1: {
                throw this.syntaxError("End of input");
            }
            case 123: {
                return this.readObject();
            }
            case 91: {
                return this.readArray();
            }
            case 34: 
            case 39: {
                return this.nextString((char)n);
            }
        }
        --this.pos;
        return this.readLiteral();
    }

    private int nextCleanInternal() throws JSONException {
        block9: while (this.pos < this.in.length()) {
            char c = this.in.charAt(this.pos++);
            switch (c) {
                case '\t': 
                case '\n': 
                case '\r': 
                case ' ': {
                    continue block9;
                }
                case '/': {
                    if (this.pos == this.in.length()) {
                        return c;
                    }
                    char c2 = this.in.charAt(this.pos);
                    switch (c2) {
                        case '*': {
                            ++this.pos;
                            int n = this.in.indexOf("*/", this.pos);
                            if (n == -1) {
                                throw this.syntaxError("Unterminated comment");
                            }
                            this.pos = n + 2;
                            continue block9;
                        }
                        case '/': {
                            ++this.pos;
                            this.skipToEndOfLine();
                            continue block9;
                        }
                    }
                    return c;
                }
                case '#': {
                    this.skipToEndOfLine();
                    continue block9;
                }
            }
            return c;
        }
        return -1;
    }

    private void skipToEndOfLine() {
        while (this.pos < this.in.length()) {
            char c = this.in.charAt(this.pos);
            if (c == '\r' || c == '\n') {
                ++this.pos;
                break;
            }
            ++this.pos;
        }
    }

    public String nextString(char c) throws JSONException {
        StringBuilder stringBuilder = null;
        int n = this.pos;
        while (this.pos < this.in.length()) {
            char c2;
            if ((c2 = this.in.charAt(this.pos++)) == c) {
                if (stringBuilder == null) {
                    return new String(this.in.substring(n, this.pos - 1));
                }
                stringBuilder.append(this.in, n, this.pos - 1);
                return stringBuilder.toString();
            }
            if (c2 != '\\') continue;
            if (this.pos == this.in.length()) {
                throw this.syntaxError("Unterminated escape sequence");
            }
            if (stringBuilder == null) {
                stringBuilder = new StringBuilder();
            }
            stringBuilder.append(this.in, n, this.pos - 1);
            stringBuilder.append(this.readEscapeCharacter());
            n = this.pos;
        }
        throw this.syntaxError("Unterminated string");
    }

    private char readEscapeCharacter() throws JSONException {
        char c = this.in.charAt(this.pos++);
        switch (c) {
            case 'u': {
                if (this.pos + 4 > this.in.length()) {
                    throw this.syntaxError("Unterminated escape sequence");
                }
                String string = this.in.substring(this.pos, this.pos + 4);
                this.pos += 4;
                return (char)Integer.parseInt(string, 16);
            }
            case 't': {
                return '\t';
            }
            case 'b': {
                return '\b';
            }
            case 'n': {
                return '\n';
            }
            case 'r': {
                return '\r';
            }
            case 'f': {
                return '\f';
            }
        }
        return c;
    }

    private Object readLiteral() throws JSONException {
        String string = this.nextToInternal("{}[]/\\:,=;# \t\f");
        if (string.length() == 0) {
            throw this.syntaxError("Expected literal value");
        }
        if ("null".equalsIgnoreCase(string)) {
            return JSONObject.NULL;
        }
        if ("true".equalsIgnoreCase(string)) {
            return Boolean.TRUE;
        }
        if ("false".equalsIgnoreCase(string)) {
            return Boolean.FALSE;
        }
        if (string.indexOf(46) == -1) {
            int n = 10;
            String string2 = string;
            if (string2.startsWith("0x") || string2.startsWith("0X")) {
                string2 = string2.substring(2);
                n = 16;
            } else if (string2.startsWith("0") && string2.length() > 1) {
                string2 = string2.substring(1);
                n = 8;
            }
            try {
                long l = Long.parseLong(string2, n);
                if (l <= Integer.MAX_VALUE && l >= Integer.MIN_VALUE) {
                    return (int)l;
                }
                return l;
            }
            catch (NumberFormatException numberFormatException) {
                // empty catch block
            }
        }
        try {
            return Double.valueOf(string);
        }
        catch (NumberFormatException numberFormatException) {
            return new String(string);
        }
    }

    private String nextToInternal(String string) {
        int n = this.pos;
        while (this.pos < this.in.length()) {
            char c = this.in.charAt(this.pos);
            if (c == '\r' || c == '\n' || string.indexOf(c) != -1) {
                return this.in.substring(n, this.pos);
            }
            ++this.pos;
        }
        return this.in.substring(n);
    }

    private JSONObject readObject() throws JSONException {
        JSONObject jSONObject = new JSONObject();
        int n = this.nextCleanInternal();
        if (n == 125) {
            return jSONObject;
        }
        if (n != -1) {
            --this.pos;
        }
        block4: while (true) {
            Object object;
            if (!((object = this.nextValue()) instanceof String)) {
                if (object == null) {
                    throw this.syntaxError("Names cannot be null");
                }
                throw this.syntaxError("Names must be strings, but " + object + " is of type " + object.getClass().getName());
            }
            int n2 = this.nextCleanInternal();
            if (n2 != 58 && n2 != 61) {
                throw this.syntaxError("Expected ':' after " + object);
            }
            if (this.pos < this.in.length() && this.in.charAt(this.pos) == '>') {
                ++this.pos;
            }
            jSONObject.put((String)object, this.nextValue());
            switch (this.nextCleanInternal()) {
                case 125: {
                    return jSONObject;
                }
                case 44: 
                case 59: {
                    continue block4;
                }
            }
            break;
        }
        throw this.syntaxError("Unterminated object");
    }

    private JSONArray readArray() throws JSONException {
        JSONArray jSONArray = new JSONArray();
        boolean bl = false;
        block9: while (true) {
            switch (this.nextCleanInternal()) {
                case -1: {
                    throw this.syntaxError("Unterminated array");
                }
                case 93: {
                    if (bl) {
                        jSONArray.put(null);
                    }
                    return jSONArray;
                }
                case 44: 
                case 59: {
                    jSONArray.put(null);
                    bl = true;
                    continue block9;
                }
            }
            --this.pos;
            jSONArray.put(this.nextValue());
            switch (this.nextCleanInternal()) {
                case 93: {
                    return jSONArray;
                }
                case 44: 
                case 59: {
                    bl = true;
                    continue block9;
                }
            }
            break;
        }
        throw this.syntaxError("Unterminated array");
    }

    public JSONException syntaxError(String string) {
        return new JSONException(string + this);
    }

    public String toString() {
        return " at character " + this.pos + " of " + this.in;
    }

    public boolean more() {
        return this.pos < this.in.length();
    }

    public char next() {
        return this.pos < this.in.length() ? this.in.charAt(this.pos++) : (char)'\u0000';
    }

    public char next(char c) throws JSONException {
        char c2 = this.next();
        if (c2 != c) {
            throw this.syntaxError("Expected " + c + " but was " + c2);
        }
        return c2;
    }

    public char nextClean() throws JSONException {
        int n = this.nextCleanInternal();
        return n == -1 ? (char)'\u0000' : (char)n;
    }

    public String next(int n) throws JSONException {
        if (this.pos + n > this.in.length()) {
            throw this.syntaxError(n + " is out of bounds");
        }
        String string = this.in.substring(this.pos, this.pos + n);
        this.pos += n;
        return string;
    }

    public String nextTo(String string) {
        if (string == null) {
            throw new NullPointerException("excluded == null");
        }
        return this.nextToInternal(string).trim();
    }

    public String nextTo(char c) {
        return this.nextToInternal(String.valueOf(c)).trim();
    }

    public void skipPast(String string) {
        int n = this.in.indexOf(string, this.pos);
        this.pos = n == -1 ? this.in.length() : n + string.length();
    }

    public char skipTo(char c) {
        int n = this.in.indexOf(c, this.pos);
        if (n != -1) {
            this.pos = n;
            return c;
        }
        return '\u0000';
    }

    public void back() {
        if (--this.pos == -1) {
            this.pos = 0;
        }
    }

    public static int dehexchar(char c) {
        if (c >= '0' && c <= '9') {
            return c - 48;
        }
        if (c >= 'A' && c <= 'F') {
            return c - 65 + 10;
        }
        if (c >= 'a' && c <= 'f') {
            return c - 97 + 10;
        }
        return -1;
    }
}

