package subscriptions

import "testing"

func TestDecodePunycodeHost(t *testing.T) {
	cases := map[string]string{
		"www.xn--sss604efuw.net":           "www.饭太硬.net",
		"xn--z7x900a.net":                  "肥猫.net",
		"tvbox.xn--4kq62z5rby2qupq9ub.top": "tvbox.王二小放牛娃.top",
		"xn--i8sz35d1yr.top":               "盒子迷.top",
		"example.com":                      "example.com",
	}
	for in, want := range cases {
		if got := decodePunycodeHost(in); got != want {
			t.Errorf("%s → %s, 期望 %s", in, got, want)
		}
	}
}
