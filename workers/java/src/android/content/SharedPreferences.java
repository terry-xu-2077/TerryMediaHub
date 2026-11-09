/*
 * Decompiled with CFR 0.152.
 */
package android.content;

import java.util.Map;
import java.util.Set;

public interface SharedPreferences {
    public Map<String, ?> getAll();

    public String getString(String var1, String var2);

    public Set<String> getStringSet(String var1, Set<String> var2);

    public int getInt(String var1, int var2);

    public long getLong(String var1, long var2);

    public float getFloat(String var1, float var2);

    public boolean getBoolean(String var1, boolean var2);

    public boolean contains(String var1);

    public Editor edit();

    public void registerOnSharedPreferenceChangeListener(OnSharedPreferenceChangeListener var1);

    public void unregisterOnSharedPreferenceChangeListener(OnSharedPreferenceChangeListener var1);

    public static interface OnSharedPreferenceChangeListener {
        public void onSharedPreferenceChanged(SharedPreferences var1, String var2);
    }

    public static interface Editor {
        public Editor putString(String var1, String var2);

        public Editor putStringSet(String var1, Set<String> var2);

        public Editor putInt(String var1, int var2);

        public Editor putLong(String var1, long var2);

        public Editor putFloat(String var1, float var2);

        public Editor putBoolean(String var1, boolean var2);

        public Editor remove(String var1);

        public Editor clear();

        public boolean commit();

        public void apply();
    }
}

