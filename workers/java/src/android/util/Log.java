/*
 * Decompiled with CFR 0.152.
 */
package android.util;

public class Log {
    private static int out(String string, String string2, String string3, Throwable throwable) {
        System.err.println("[" + string + "/" + string2 + "] " + string3);
        if (throwable != null) {
            throwable.printStackTrace();
        }
        return 0;
    }

    public static boolean isLoggable(String string, int n) {
        return true;
    }

    public static int v(String string, String string2) {
        return Log.out("V", string, string2, null);
    }

    public static int d(String string, String string2) {
        return Log.out("D", string, string2, null);
    }

    public static int i(String string, String string2) {
        return Log.out("I", string, string2, null);
    }

    public static int w(String string, String string2) {
        return Log.out("W", string, string2, null);
    }

    public static int e(String string, String string2) {
        return Log.out("E", string, string2, null);
    }

    public static int e(String string, String string2, Throwable throwable) {
        return Log.out("E", string, string2, throwable);
    }

    public static int w(String string, String string2, Throwable throwable) {
        return Log.out("W", string, string2, throwable);
    }

    public static int wtf(String string, String string2, Throwable throwable) {
        return Log.out("WTF", string, string2, throwable);
    }

    public static int d(String string, String string2, Throwable throwable) {
        return Log.out("D", string, string2, throwable);
    }

    public static int i(String string, String string2, Throwable throwable) {
        return Log.out("I", string, string2, throwable);
    }

    public static int v(String string, String string2, Throwable throwable) {
        return Log.out("V", string, string2, throwable);
    }
}

