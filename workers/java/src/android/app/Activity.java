/*
 * Decompiled with CFR 0.152.
 */
package android.app;

import android.app.Application;
import android.app.FragmentManager;
import android.content.Intent;

public class Activity {
    public FragmentManager getFragmentManager() {
        return null;
    }

    public boolean shouldShowRequestPermissionRationale(String string) {
        return false;
    }

    public int checkSelfPermission(String string) {
        return 0;
    }

    public void finish() {
    }

    public void registerActivityLifecycleCallbacks(Application$ActivityLifecycleCallbacks activityLifecycleCallbacks) {
    }

    public void requestPermissions(String[] stringArray, int n) {
    }

    public void startActivity(Intent intent) {
    }

    public void startActivityForResult(Intent intent, int n) {
    }
}

