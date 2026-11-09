package android.util;

import java.nio.charset.StandardCharsets;

/** Minimal Android Base64 compatibility for TVBox spiders running on a JVM. */
public class Base64 {
    public static final int DEFAULT = 0;
    public static final int NO_PADDING = 1;
    public static final int NO_WRAP = 2;
    public static final int CRLF = 4;
    public static final int URL_SAFE = 8;
    public static final int NO_CLOSE = 16;

    public static byte[] decode(String input, int flags) {
        if (input == null) return null;
        String cleaned = input.replaceAll("\\s", "");
        try {
            return ((flags & URL_SAFE) != 0 ? java.util.Base64.getUrlDecoder() : java.util.Base64.getDecoder())
                .decode(cleaned);
        } catch (IllegalArgumentException ex) {
            try {
                return java.util.Base64.getUrlDecoder().decode(cleaned);
            } catch (IllegalArgumentException ignored) {
                return null;
            }
        }
    }

    public static byte[] decode(byte[] input, int flags) {
        return input == null ? null : decode(new String(input, StandardCharsets.US_ASCII), flags);
    }

    public static byte[] decode(byte[] input, int offset, int length, int flags) {
        if (input == null) return null;
        return decode(new String(input, offset, length, StandardCharsets.US_ASCII), flags);
    }

    public static byte[] encode(byte[] input, int flags) {
        String result = encodeToString(input, flags);
        return result == null ? null : result.getBytes(StandardCharsets.US_ASCII);
    }

    public static String encodeToString(byte[] input, int flags) {
        if (input == null) return null;
        java.util.Base64.Encoder encoder = (flags & URL_SAFE) != 0
            ? java.util.Base64.getUrlEncoder() : java.util.Base64.getEncoder();
        if ((flags & NO_PADDING) != 0) encoder = encoder.withoutPadding();
        String result = encoder.encodeToString(input);
        if ((flags & NO_WRAP) == 0) {
            String newline = (flags & CRLF) != 0 ? "\r\n" : "\n";
            result = result.replaceAll("(.{76})", "$1" + newline);
        }
        return result;
    }
}
