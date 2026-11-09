/*
 * TerryMediaHub — JVM 解析宿主（spider host）
 *
 * 通过 stdin/stdout 逐行 JSON RPC，把 TVBox / 影视仓 的 csp_ 站点爬虫
 * （com.github.catvod.spider.*，打包在 jar 里，只能在 JVM 里运行）
 * 暴露给 Go 主服务调用。
 *
 * 协议详见 contracts/worker/protocol.md。
 *
 * 本文件从上一版 host-runtime.jar 中的 Host.class 反编译还原，
 * 目的是补齐丢失的宿主源码，保证可维护、可复现构建。
 */
package com.terry.spiderhost;

import android.content.Context;
import com.github.catvod.crawler.Spider;
import com.github.catvod.crawler.SpiderApi;
import com.googlecode.d2j.dex.BaseDexExceptionHandler;
import com.googlecode.d2j.dex.Dex2jar;
import com.googlecode.d2j.dex.DexExceptionHandler;
import java.io.BufferedReader;
import java.io.File;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.io.OutputStreamWriter;
import java.io.PrintWriter;
import java.io.Serializable;
import java.io.StringWriter;
import java.io.Writer;
import java.lang.reflect.AccessibleObject;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.lang.reflect.Modifier;
import java.net.URL;
import java.net.URLClassLoader;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.StandardCopyOption;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Enumeration;
import java.util.HashMap;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.zip.ZipEntry;
import java.util.zip.ZipFile;
import org.json.JSONArray;
import org.json.JSONObject;

public final class Host {
    private static final String PREFIX = "com.github.catvod.spider.";
    private static final Map<String, Spider> CACHE = new HashMap<String, Spider>();
    private static final Map<String, JarLoader> LOADERS = new LinkedHashMap<String, JarLoader>();
    private static final Map<String, Set<String>> CLASSES = new HashMap<String, Set<String>>();
    private static final Object LOCK = new Object();
    private static File dir;
    private static String globalJar;
    private static final AtomicBoolean PREWARMED = new AtomicBoolean(false);

    public static void main(String[] stringArray) throws Exception {
        String string;
        System.setProperty("file.encoding", "UTF-8");
        if (stringArray.length < 1) {
            System.err.println("usage: Host <spiderDir> [globalJarName]");
            System.exit(2);
        }
        if (!(dir = new File(stringArray[0])).isDirectory()) {
            dir = dir.getParentFile();
        }
        globalJar = stringArray.length > 1 ? stringArray[1] : Host.pickGlobal();
        BufferedReader bufferedReader = new BufferedReader(new InputStreamReader(System.in, StandardCharsets.UTF_8));
        PrintWriter printWriter = new PrintWriter((Writer) new OutputStreamWriter((OutputStream) System.out, StandardCharsets.UTF_8), true);
        printWriter.println(new JSONObject()
                .put("ok", true)
                .put("ready", true)
                .put("dir", (Object) dir.getAbsolutePath())
                .put("global", (Object) (globalJar == null ? "" : globalJar))
                .toString());
        Thread thread = new Thread(Host::prewarm, "jar-prewarm");
        thread.setDaemon(true);
        thread.start();
        while ((string = bufferedReader.readLine()) != null) {
            Object object;
            if ((string = string.trim()).isEmpty()) {
                continue;
            }
            JSONObject jSONObject = new JSONObject();
            long l = System.currentTimeMillis();
            try {
                JSONObject jSONObject2 = new JSONObject(string);
                object = Host.handle(jSONObject2);
                jSONObject.put("ok", true);
                jSONObject.put("data", (Object) (object == null ? "" : object.toString()));
            } catch (Throwable throwable) {
                jSONObject.put("ok", false);
                jSONObject.put("error", (Object) (throwable.getClass().getSimpleName() + ": " + String.valueOf(throwable.getMessage())));
                object = new StringWriter();
                throwable.printStackTrace(new PrintWriter((Writer) object));
                jSONObject.put("trace", (Object) Host.tail(((StringWriter) object).toString()));
            }
            jSONObject.put("ms", System.currentTimeMillis() - l);
            printWriter.println(jSONObject.toString());
        }
        // stdin 关闭（父进程退出/被杀）：强制退出，spider 里的非守护线程否则会让 JVM 变成孤儿进程。
        System.exit(0);
    }

    private static String pickGlobal() {
        File[] fileArray = dir.listFiles((file, string) -> string.startsWith("spider-") && string.endsWith(".jar"));
        if (fileArray == null || fileArray.length == 0) {
            return null;
        }
        Arrays.sort(fileArray, (file, file2) -> Long.compare(file2.lastModified(), file.lastModified()));
        return fileArray[0].getName();
    }

    private static List<String> allJars() {
        ArrayList<String> arrayList = new ArrayList<String>();
        File[] fileArray = dir.listFiles((file, string) -> string.endsWith(".jar")
                && !string.equals("host-runtime.jar")
                && !string.endsWith(".classes.jar"));
        if (fileArray == null) {
            return arrayList;
        }
        Arrays.sort(fileArray, (file, file2) -> file.getName().compareTo(file2.getName()));
        for (File file3 : fileArray) {
            arrayList.add(file3.getName());
        }
        return arrayList;
    }

    private static void prewarm() {
        try {
            List<String> list = Host.allJars();
            for (String string : list) {
                try {
                    Host.classesOf(string);
                } catch (Throwable throwable) {
                    System.err.println("[host] 索引失败 " + string + ": " + throwable);
                }
            }
            PREWARMED.set(true);
            System.err.println("[host] 预热完成，共 " + list.size() + " 个 jar");
        } catch (Throwable throwable) {
            System.err.println("[host] 预热异常: " + throwable);
        }
    }

    private static Set<String> classesOf(String string) throws Exception {
        Object object = LOCK;
        synchronized (object) {
            Set<String> set = CLASSES.get(string);
            if (set != null) {
                return set;
            }
            File file = Host.ensureClassesJar(new File(dir, string));
            LinkedHashSet<String> linkedHashSet = new LinkedHashSet<String>();
            try (ZipFile zipFile = new ZipFile(file)) {
                Enumeration<? extends ZipEntry> enumeration = zipFile.entries();
                while (enumeration.hasMoreElements()) {
                    String string2 = enumeration.nextElement().getName();
                    if (!string2.endsWith(".class") || !string2.startsWith("com/github/catvod/spider/")) {
                        continue;
                    }
                    linkedHashSet.add(string2.substring(0, string2.length() - 6).replace('/', '.'));
                }
            }
            CLASSES.put(string, linkedHashSet);
            return linkedHashSet;
        }
    }

    private static JarLoader globalLoader() throws Exception {
        String string = globalJar;
        if (string == null) {
            List<String> list = Host.allJars();
            string = list.isEmpty() ? null : list.get(0);
        }
        if (string == null) {
            return null;
        }
        return Host.loader(string, true);
    }

    private static JarLoader loader(String string, boolean bl) throws Exception {
        Object object = LOCK;
        synchronized (object) {
            JarLoader jarLoader = LOADERS.get(string);
            if (jarLoader != null) {
                return jarLoader;
            }
            File file = Host.ensureClassesJar(new File(dir, string));
            ClassLoader classLoader = bl
                    ? Host.class.getClassLoader()
                    : (globalJar != null && !globalJar.equals(string) ? Host.globalLoader() : Host.class.getClassLoader());
            JarLoader jarLoader2 = new JarLoader(new URL[]{file.toURI().toURL()}, classLoader);
            LOADERS.put(string, jarLoader2);
            return jarLoader2;
        }
    }

    private static void injectApi(Spider spider) {
        try {
            SpiderApi spiderApi = new SpiderApi();
            for (Class<?> clazz = spider.getClass(); clazz != null && clazz != Object.class; clazz = clazz.getSuperclass()) {
                for (Field field : clazz.getDeclaredFields()) {
                    if (field.getType() != SpiderApi.class || !Modifier.isStatic(field.getModifiers())) {
                        continue;
                    }
                    try {
                        field.setAccessible(true);
                        field.set(null, spiderApi);
                    } catch (Throwable throwable) {
                        // empty catch block
                    }
                }
                for (AccessibleObject accessibleObject : clazz.getDeclaredMethods()) {
                    Class<?>[] classArray = ((Method) accessibleObject).getParameterTypes();
                    if (classArray.length != 1 || classArray[0] != SpiderApi.class) {
                        continue;
                    }
                    try {
                        ((Method) accessibleObject).setAccessible(true);
                        ((Method) accessibleObject).invoke(spider, spiderApi);
                    } catch (Throwable throwable) {
                        // empty catch block
                    }
                }
            }
        } catch (Throwable throwable) {
            System.err.println("[host] 注入 SpiderApi 失败: " + throwable);
        }
    }

    private static Context context() {
        try {
            return (Context) Class.forName("android.app.Application").getMethod("instance", new Class[0]).invoke(null, new Object[0]);
        } catch (Throwable throwable) {
            return null;
        }
    }

    private static List<String> candidatesFor(String string, String string2) throws Exception {
        ArrayList<String> arrayList = new ArrayList<String>();
        if (string2 != null && !string2.isEmpty() && new File(dir, string2).exists()) {
            arrayList.add(string2);
        }
        if (globalJar != null) {
            arrayList.add(globalJar);
        }
        for (String object : Host.allJars()) {
            if (arrayList.contains(object)) {
                continue;
            }
            arrayList.add(object);
        }
        ArrayList<String> arrayList2 = new ArrayList<String>();
        for (String string3 : arrayList) {
            try {
                if (!Host.classesOf(string3).contains(string)) {
                    continue;
                }
                arrayList2.add(string3);
            } catch (Throwable throwable) {
                System.err.println("[host] 打开 jar 失败 " + string3 + ": " + throwable);
            }
        }
        return arrayList2;
    }

    private static Object handle(JSONObject jSONObject) throws Exception {
        Serializable serializable;
        String string;
        Spider spider;
        String string2 = jSONObject.getString("method");
        if ("prepare".equals(string2)) {
            return Host.prepareReport();
        }
        String string3 = jSONObject.getString("site");
        String string4 = jSONObject.optString("ext", "");
        String string5 = jSONObject.optString("jar", "");
        JSONObject jSONObject2 = jSONObject.optJSONObject("args");
        if (jSONObject2 == null) {
            jSONObject2 = new JSONObject();
        }
        if ((spider = CACHE.get(string = string3 + "|" + string4.hashCode() + "|" + string5)) == null) {
            String string6 = PREFIX + string3;
            List<String> list = Host.candidatesFor(string6, string5);
            if (list.isEmpty()) {
                throw new ClassNotFoundException("没有哪个 jar 里包含 " + string6 + "（已扫描 " + Host.allJars().size() + " 个 jar）");
            }
            serializable = null;
            for (String object : list) {
                try {
                    Object object2 = Class.forName(string6, true, Host.loader(object, object.equals(globalJar)));
                    Spider spider2 = (Spider) ((Class) object2).getDeclaredConstructor(new Class[0]).newInstance(new Object[0]);
                    Host.injectApi(spider2);
                    spider2.init(Host.context(), string4);
                    spider = spider2;
                    break;
                } catch (Throwable throwable) {
                    serializable = throwable;
                    System.err.println("[host] " + string6 + " 在 " + object + " 里初始化失败: " + throwable);
                }
            }
            if (spider == null) {
                throw new IllegalStateException("所有候选 jar 都无法初始化 " + string6, (Throwable) serializable);
            }
            CACHE.put(string, spider);
        }
        switch (string2) {
            case "home": {
                return spider.homeContent(true);
            }
            case "homeVideo": {
                return spider.homeVideoContent();
            }
            case "category": {
                serializable = new HashMap();
                JSONObject object = jSONObject2.optJSONObject("extend");
                if (object != null) {
                    for (Object object2 : object.keySet()) {
                        ((Map) serializable).put((String) object2, (String) object.optString((String) object2));
                    }
                }
                return spider.categoryContent(jSONObject2.optString("tid"), jSONObject2.optString("pg", "1"), true, (HashMap<String, String>) serializable);
            }
            case "detail": {
                serializable = new ArrayList();
                JSONArray object = jSONObject2.optJSONArray("ids");
                if (object != null) {
                    int i = 0;
                    while (i < object.length()) {
                        ((List) serializable).add(object.getString(i));
                        ++i;
                    }
                }
                return spider.detailContent((List<String>) ((Object) serializable));
            }
            case "search": {
                return spider.searchContent(jSONObject2.optString("key"), jSONObject2.optBoolean("quick", false));
            }
            case "play": {
                serializable = new ArrayList();
                JSONArray object = jSONObject2.optJSONArray("vipFlags");
                if (object != null) {
                    int i = 0;
                    while (i < object.length()) {
                        ((List) serializable).add(object.getString(i));
                        ++i;
                    }
                }
                return spider.playerContent(jSONObject2.optString("flag"), jSONObject2.optString("id"), (List<String>) ((Object) serializable));
            }
            case "ping": {
                return "pong";
            }
        }
        throw new IllegalArgumentException("unknown method: " + string2);
    }

    private static JSONObject prepareReport() throws Exception {
        for (int i = 0; i < 120 && !PREWARMED.get(); ++i) {
            Thread.sleep(100L);
        }
        JSONObject jSONObject = new JSONObject();
        JSONArray jSONArray = new JSONArray();
        for (String string : Host.allJars()) {
            JSONObject jSONObject2 = new JSONObject();
            jSONObject2.put("jar", (Object) string);
            try {
                jSONObject2.put("classes", Host.classesOf(string).size());
            } catch (Throwable throwable) {
                jSONObject2.put("error", (Object) String.valueOf(throwable));
            }
            jSONArray.put((Object) jSONObject2);
        }
        jSONObject.put("global", (Object) (globalJar == null ? "" : globalJar));
        jSONObject.put("jars", (Object) jSONArray);
        return jSONObject;
    }

    private static File ensureClassesJar(File file) throws Exception {
        if (!file.exists()) {
            throw new IllegalArgumentException("spider jar 不存在: " + file);
        }
        if (file.getName().endsWith(".classes.jar")) {
            return file;
        }
        boolean hasDex = false;
        boolean hasClass = false;
        try (ZipFile zip = new ZipFile(file)) {
            Enumeration<? extends ZipEntry> en = zip.entries();
            while (en.hasMoreElements()) {
                String name = en.nextElement().getName();
                if (name.equals("classes.dex")) {
                    hasDex = true;
                }
                if (name.endsWith(".class")) {
                    hasClass = true;
                }
            }
        } catch (Exception exception) {
            hasDex = true; // 不是 zip：当作裸 dex
        }
        if (!hasDex || hasClass) {
            return file;
        }
        String base = file.getName().replaceAll("\\.[^.]*$", "");
        File out = new File(file.getParentFile(), base + ".classes.jar");
        if (out.exists() && out.lastModified() >= file.lastModified() && out.length() > 1024L) {
            return out;
        }
        File dex = new File(file.getParentFile(), base + ".tmp.dex");
        if (file.getName().endsWith(".dex")) {
            Files.copy(file.toPath(), dex.toPath(), StandardCopyOption.REPLACE_EXISTING);
        } else {
            try (ZipFile zip = new ZipFile(file);
                 InputStream in = zip.getInputStream(zip.getEntry("classes.dex"))) {
                Files.copy(in, dex.toPath(), StandardCopyOption.REPLACE_EXISTING);
            }
        }
        System.err.println("[host] 转换 dex -> jar: " + out.getName());
        File tmp = new File(file.getParentFile(), out.getName() + ".tmp");
        Dex2jar.from(dex)
                .withExceptionHandler((DexExceptionHandler) new BaseDexExceptionHandler())
                .skipDebug()
                .to(tmp.toPath());
        if (!tmp.exists() || tmp.length() < 1024L) {
            throw new IllegalStateException("dex2jar 没有产出有效 jar: " + file.getName());
        }
        Files.move(tmp.toPath(), out.toPath(), StandardCopyOption.REPLACE_EXISTING);
        dex.delete();
        return out;
    }

    private static String tail(String string) {
        return string.length() <= 2000 ? string : string.substring(0, 2000);
    }

    static final class JarLoader extends URLClassLoader {
        JarLoader(URL[] uRLArray, ClassLoader classLoader) {
            super(uRLArray, classLoader);
        }

        @Override
        protected Class<?> loadClass(String string, boolean bl) throws ClassNotFoundException {
            Object object = this.getClassLoadingLock(string);
            synchronized (object) {
                Class<?> clazz = this.findLoadedClass(string);
                if (clazz == null && string.startsWith(Host.PREFIX)) {
                    try {
                        clazz = this.findClass(string);
                    } catch (ClassNotFoundException classNotFoundException) {
                    } catch (LinkageError linkageError) {
                        System.err.println("[host] " + string + " 在本 jar 里损坏(" + linkageError + ")，改用父加载器");
                    }
                }
                if (clazz == null) {
                    clazz = super.loadClass(string, false);
                }
                if (bl) {
                    this.resolveClass(clazz);
                }
                return clazz;
            }
        }
    }
}
