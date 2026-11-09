/*
 * Decompiled with CFR 0.152.
 */
package android.content;

import android.content.res.Resources$Theme;

import android.content.ContentResolver;
import android.content.Intent;
import android.content.ServiceConnection;
import android.content.SharedPreferences;
import android.content.pm.ApplicationInfo;
import android.content.pm.PackageManager;
import android.content.res.Resources;
import com.terry.spiderhost.SimplePrefs;
import java.io.File;

public class Context {
    public ContentResolver getContentResolver() {
        return null;
    }

    public Context createPackageContext(String string, int n) {
        return this;
    }

    public Context getApplicationContext() {
        return this;
    }

    public ApplicationInfo getApplicationInfo() {
        return null;
    }

    public PackageManager getPackageManager() {
        return null;
    }

    public Resources getResources() {
        return null;
    }

    public Resources$Theme getTheme() {
        return null;
    }

    public boolean bindService(Intent intent, ServiceConnection serviceConnection, int n) {
        return false;
    }

    public int checkSelfPermission(String string) {
        return 0;
    }

    public File getCacheDir() {
        File file = new File(System.getProperty("java.io.tmpdir"), "tmh-cache");
        file.mkdirs();
        return file;
    }

    public File getFilesDir() {
        File file = new File(System.getProperty("java.io.tmpdir"), "tmh-files");
        file.mkdirs();
        return file;
    }

    public ClassLoader getClassLoader() {
        return Context.class.getClassLoader();
    }

    public SharedPreferences getSharedPreferences(String string, int n) {
        return SimplePrefs.of(string);
    }

    public Object getSystemService(Class clazz) {
        return null;
    }

    public Object getSystemService(String string) {
        return null;
    }

    public String getPackageName() {
        return "com.terry.spiderhost";
    }

    public void startActivity(Intent intent) {
    }

    public void unbindService(ServiceConnection serviceConnection) {
    }
}

