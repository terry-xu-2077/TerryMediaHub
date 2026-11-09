import android.content.Context;

public final class CompatSmoke {
    public static void main(String[] args) {
        Context context = new Context();
        if (context.getApplicationInfo() == null || !context.getApplicationInfo().enabled)
            throw new AssertionError("ApplicationInfo unavailable");
        if (context.getContentResolver() == null || context.getPackageManager() == null
                || context.getResources() == null || context.getTheme() == null)
            throw new AssertionError("Android context service unavailable");
        if (!context.getCacheDir().isDirectory() || !context.getFilesDir().isDirectory()
                || !context.getExternalFilesDir("media").isDirectory())
            throw new AssertionError("Android directories unavailable");
        context.getSharedPreferences("smoke", 0).edit().putString("key", "value").apply();
        if (!"value".equals(context.getSharedPreferences("smoke", 0).getString("key", "")))
            throw new AssertionError("SharedPreferences unavailable");
        System.out.println("android-context-ok");
    }
}
