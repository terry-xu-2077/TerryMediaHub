package sources

import (
	"crypto/aes"
	"crypto/cipher"
	"encoding/base64"
	"encoding/hex"
	"testing"
)

const sample = `{
  // 注释里有 http://example.com
  "spider": "./x.jar;md5;abc",
  "sites": [{"key":"a","name":"A","type":3,"api":"csp_A","ext":"./e.json",},],
}`

func encrypt(plain, key, iv string) string {
	pad := 16 - len(plain)%16
	b := []byte(plain)
	for i := 0; i < pad; i++ {
		b = append(b, byte(pad))
	}
	block, _ := aes.NewCipher(padKey(key))
	out := make([]byte, len(b))
	cipher.NewCBCEncrypter(block, padKey(iv)).CryptBlocks(out, b)
	return hex.EncodeToString([]byte("$#"+key+"#$")) + hex.EncodeToString(out) + hex.EncodeToString([]byte(iv))
}

func TestParseVariants(t *testing.T) {
	cases := map[string][]byte{
		"plain":     []byte(sample),
		"image-b64": append([]byte("\xff\xd8\xff garbage "), []byte("**"+base64.StdEncoding.EncodeToString([]byte(sample)))...),
		"aes":       []byte(encrypt(sample, "k3y0abcd", "iv13chars1234")),
	}
	for name, body := range cases {
		cfg, err := ParseConfig(body, "http://host/dir/tv")
		if err != nil {
			t.Fatalf("%s: %v", name, err)
		}
		if len(cfg.Sites) != 1 || cfg.Sites[0].Key != "a" {
			t.Fatalf("%s: 站点解析错误 %+v", name, cfg.Sites)
		}
		if cfg.Spider != "http://host/dir/x.jar;md5;abc" {
			t.Fatalf("%s: 相对 spider 未补全: %s", name, cfg.Spider)
		}
		if cfg.Sites[0].ExtString() != "http://host/dir/e.json" {
			t.Fatalf("%s: 相对 ext 未补全: %s", name, cfg.Sites[0].ExtString())
		}
	}
}
