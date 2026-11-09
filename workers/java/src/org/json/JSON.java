/*
 * Decompiled with CFR 0.152.
 */
package org.json;

import org.json.JSONException;

class JSON {
    JSON() {
    }

    static double checkDouble(double d) throws JSONException {
        if (Double.isInfinite(d) || Double.isNaN(d)) {
            throw new JSONException("Forbidden numeric value: " + d);
        }
        return d;
    }

    static Boolean toBoolean(Object object) {
        if (object instanceof Boolean) {
            return (Boolean)object;
        }
        if (object instanceof String) {
            String string = (String)object;
            if ("true".equalsIgnoreCase(string)) {
                return true;
            }
            if ("false".equalsIgnoreCase(string)) {
                return false;
            }
        }
        return null;
    }

    static Double toDouble(Object object) {
        if (object instanceof Double) {
            return (Double)object;
        }
        if (object instanceof Number) {
            return ((Number)object).doubleValue();
        }
        if (object instanceof String) {
            try {
                return Double.valueOf((String)object);
            }
            catch (NumberFormatException numberFormatException) {
                // empty catch block
            }
        }
        return null;
    }

    static Integer toInteger(Object object) {
        if (object instanceof Integer) {
            return (Integer)object;
        }
        if (object instanceof Number) {
            return ((Number)object).intValue();
        }
        if (object instanceof String) {
            try {
                return (int)Double.parseDouble((String)object);
            }
            catch (NumberFormatException numberFormatException) {
                // empty catch block
            }
        }
        return null;
    }

    static Long toLong(Object object) {
        if (object instanceof Long) {
            return (Long)object;
        }
        if (object instanceof Number) {
            return ((Number)object).longValue();
        }
        if (object instanceof String) {
            try {
                return (long)Double.parseDouble((String)object);
            }
            catch (NumberFormatException numberFormatException) {
                // empty catch block
            }
        }
        return null;
    }

    static String toString(Object object) {
        if (object instanceof String) {
            return (String)object;
        }
        if (object != null) {
            return String.valueOf(object);
        }
        return null;
    }

    public static JSONException typeMismatch(Object object, Object object2, String string) throws JSONException {
        if (object2 == null) {
            throw new JSONException("Value at " + object + " is null.");
        }
        throw new JSONException("Value " + object2 + " at " + object + " of type " + object2.getClass().getName() + " cannot be converted to " + string);
    }

    public static JSONException typeMismatch(Object object, String string) throws JSONException {
        if (object == null) {
            throw new JSONException("Value is null.");
        }
        throw new JSONException("Value " + object + " of type " + object.getClass().getName() + " cannot be converted to " + string);
    }
}

