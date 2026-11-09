/*
 * Decompiled with CFR 0.152.
 */
package android.app;

import android.app.Application$ActivityLifecycleCallbacks;
import android.content.ComponentCallbacks;
import android.content.Context;

public class Application
extends Context {
    private static final Application INSTANCE = new Application();

    public static Application instance() {
        return INSTANCE;
    }

    public void registerActivityLifecycleCallbacks(Application$ActivityLifecycleCallbacks application$ActivityLifecycleCallbacks) {
    }

    public void registerComponentCallbacks(ComponentCallbacks componentCallbacks) {
    }
}

