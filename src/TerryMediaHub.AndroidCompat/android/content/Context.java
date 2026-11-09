package android.content;

import android.content.pm.ApplicationInfo;
import android.content.pm.PackageManager;
import android.content.res.Resources;
import com.terry.spiderhost.SimplePrefs;
import java.io.File;

/** Small desktop implementation of the Android Context methods used by catvod spiders. */
public class Context {
    private final ContentResolver contentResolver = new ContentResolver();
    private final PackageManager packageManager = new PackageManager();
    private final Resources resources = new Resources();
    private final android.content.res.Resources$Theme theme = new android.content.res.Resources$Theme();
    private final ApplicationInfo applicationInfo = new ApplicationInfo();

    public Context() {
        applicationInfo.name = getPackageName();
        applicationInfo.enabled = true;
    }

    public ContentResolver getContentResolver() { return contentResolver; }
    public Context createPackageContext(String name, int flags) { return this; }
    public Context getApplicationContext() { return this; }
    public ApplicationInfo getApplicationInfo() { return applicationInfo; }
    public PackageManager getPackageManager() { return packageManager; }
    public Resources getResources() { return resources; }
    public android.content.res.Resources$Theme getTheme() { return theme; }
    public boolean bindService(Intent intent, ServiceConnection connection, int flags) { return false; }
    public int checkSelfPermission(String permission) { return 0; }
    public File getCacheDir() { return directory("android-cache"); }
    public File getFilesDir() { return directory("android-files"); }
    public File getExternalCacheDir() { return directory("android-cache"); }
    public File getExternalFilesDir(String type) { return directory(type == null ? "android-files" : "android-files/" + type); }
    public File getDataDir() { return directory("android-data"); }
    public File getDir(String name, int mode) { return directory("android-data/" + name); }
    public ClassLoader getClassLoader() { return Context.class.getClassLoader(); }
    public SharedPreferences getSharedPreferences(String name, int mode) { return SimplePrefs.of(name); }
    public Object getSystemService(Class<?> serviceClass) { return null; }
    public Object getSystemService(String name) { return null; }
    public String getPackageName() { return "com.terry.spiderhost"; }
    public void startActivity(Intent intent) { }
    public void unbindService(ServiceConnection connection) { }

    private static File directory(String child) {
        String root = System.getenv("ProgramData");
        if (root == null || root.isEmpty()) root = System.getProperty("java.io.tmpdir");
        File directory = new File(new File(root, "TerryMediaHub"), child);
        directory.mkdirs();
        return directory;
    }
}
