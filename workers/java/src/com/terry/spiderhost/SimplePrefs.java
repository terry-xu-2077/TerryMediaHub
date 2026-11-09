/*
 * Decompiled with CFR 0.152.
 */
package com.terry.spiderhost;

import android.content.SharedPreferences;
import java.util.HashMap;
import java.util.HashSet;
import java.util.Map;
import java.util.Set;

public final class SimplePrefs
implements SharedPreferences {
    private static final Map<String, SimplePrefs> POOL = new HashMap<String, SimplePrefs>();
    private final Map<String, Object> data = new HashMap<String, Object>();

    public static synchronized SharedPreferences of(String string) {
        SimplePrefs simplePrefs = POOL.get(string);
        if (simplePrefs == null) {
            simplePrefs = new SimplePrefs();
            POOL.put(string, simplePrefs);
        }
        return simplePrefs;
    }

    @Override
    public synchronized Map<String, ?> getAll() {
        return new HashMap<String, Object>(this.data);
    }

    @Override
    public synchronized String getString(String string, String string2) {
        Object object = this.data.get(string);
        return object instanceof String ? (String)object : string2;
    }

    @Override
    public synchronized Set<String> getStringSet(String string, Set<String> set) {
        Object object = this.data.get(string);
        return object instanceof Set ? (Set)object : set;
    }

    @Override
    public synchronized int getInt(String string, int n) {
        Object object = this.data.get(string);
        return object instanceof Number ? ((Number)object).intValue() : n;
    }

    @Override
    public synchronized long getLong(String string, long l) {
        Object object = this.data.get(string);
        return object instanceof Number ? ((Number)object).longValue() : l;
    }

    @Override
    public synchronized float getFloat(String string, float f) {
        Object object = this.data.get(string);
        return object instanceof Number ? ((Number)object).floatValue() : f;
    }

    @Override
    public synchronized boolean getBoolean(String string, boolean bl) {
        Object object = this.data.get(string);
        return object instanceof Boolean ? (Boolean)object : bl;
    }

    @Override
    public synchronized boolean contains(String string) {
        return this.data.containsKey(string);
    }

    @Override
    public SharedPreferences.Editor edit() {
        return new SimpleEditor(this);
    }

    @Override
    public void registerOnSharedPreferenceChangeListener(SharedPreferences.OnSharedPreferenceChangeListener onSharedPreferenceChangeListener) {
    }

    @Override
    public void unregisterOnSharedPreferenceChangeListener(SharedPreferences.OnSharedPreferenceChangeListener onSharedPreferenceChangeListener) {
    }

    private static final class SimpleEditor
    implements SharedPreferences.Editor {
        private final SimplePrefs owner;
        private final Map<String, Object> pending = new HashMap<String, Object>();
        private final Set<String> removed = new HashSet<String>();
        private boolean clearAll = false;

        SimpleEditor(SimplePrefs simplePrefs) {
            this.owner = simplePrefs;
        }

        @Override
        public SharedPreferences.Editor putString(String string, String string2) {
            this.pending.put(string, string2);
            return this;
        }

        @Override
        public SharedPreferences.Editor putStringSet(String string, Set<String> set) {
            this.pending.put(string, set == null ? null : new HashSet<String>(set));
            return this;
        }

        @Override
        public SharedPreferences.Editor putInt(String string, int n) {
            this.pending.put(string, n);
            return this;
        }

        @Override
        public SharedPreferences.Editor putLong(String string, long l) {
            this.pending.put(string, l);
            return this;
        }

        @Override
        public SharedPreferences.Editor putFloat(String string, float f) {
            this.pending.put(string, Float.valueOf(f));
            return this;
        }

        @Override
        public SharedPreferences.Editor putBoolean(String string, boolean bl) {
            this.pending.put(string, bl);
            return this;
        }

        @Override
        public SharedPreferences.Editor remove(String string) {
            this.removed.add(string);
            return this;
        }

        @Override
        public SharedPreferences.Editor clear() {
            this.clearAll = true;
            return this;
        }

        /*
         * WARNING - Removed try catching itself - possible behaviour change.
         */
        @Override
        public boolean commit() {
            SimplePrefs simplePrefs = this.owner;
            synchronized (simplePrefs) {
                if (this.clearAll) {
                    this.owner.data.clear();
                }
                for (String string : this.removed) {
                    this.owner.data.remove(string);
                }
                this.owner.data.putAll(this.pending);
            }
            return true;
        }

        @Override
        public void apply() {
            this.commit();
        }
    }
}

