/*
 * Decompiled with CFR 0.152.
 */
package com.github.catvod.crawler;

public class SpiderDebug {
    public static boolean DEBUG = true;

    public static void log(String string) {
        if (DEBUG) {
            System.err.println("[spider] " + string);
        }
    }

    public static void log(Throwable throwable) {
        if (throwable != null && DEBUG) {
            throwable.printStackTrace();
        }
    }

    public static void log(String string, String string2) {
        if (DEBUG) {
            System.err.println("[spider/" + string + "] " + string2);
        }
    }
}

