#!/usr/bin/env python3
"""Генератор синтетического корпуса для golden-сверки C# ↔ Kotlin (tools/golden/corpus).
Ссылки и тела подписок подобраны под ветки кода и полевые случаи (см. комментарии). Секретов нет — всё выдумано.
Запуск: python3 tools/golden/make-corpus.py  (перезаписывает corpus/links/*.txt и corpus/bodies/*)."""
import base64, json, os
HERE = os.path.dirname(os.path.abspath(__file__))
C = os.path.join(HERE, "corpus")
U = "11111111-2222-3333-4444-555555555555"

def b64(s, urlsafe=False, pad=True):
    e = (base64.urlsafe_b64encode if urlsafe else base64.b64encode)(s.encode()).decode()
    return e if pad else e.rstrip("=")

def vm(d, **kw):
    return "vmess://" + b64(json.dumps(d, ensure_ascii=False), **kw)

vless = [
    f"vless://{U}@example.com:443?encryption=none&security=tls&type=ws&path=%2Fws&host=cdn.example.com&sni=example.com&fp=chrome#VLESS%20WS",
    f"vless://{U}@example.com:443?encryption=none&mode=packet-up&path=%2Fassets%2Fv2&security=reality&pbk=SOMEKEY&sid=ab12&sni=s.example.com&type=xhttp&fp=chrome&spx=%2F#xhttp-reality",
    f"vless://{U}@1.2.3.4:8443?security=reality&flow=xtls-rprx-vision&pbk=PUB&sid=00&sni=www.microsoft.com&fp=firefox&type=tcp#%D0%A0%D0%BE%D1%81%D1%81%D0%B8%D1%8F%20%F0%9F%87%B7%F0%9F%87%BA",
    f"vless://{U}@[2001:db8::1]:443?type=grpc&serviceName=svc&security=tls&alpn=h2%2Chttp%2F1.1#ipv6-grpc",
    f"vless://{U}@example.com?type=ws#no-port",
    f"vless://{U}@example.com:0?type=ws#port-zero",
    f"VLESS://{U}@Example.COM:443?security=tls#upper-scheme",
    f"vless://@example.com:443?security=tls#no-uuid",
    f"vless://{U}@example.com:443?security=false&type=raw#sec-false",
    f"vless://{U}@example.com:443?security=auto&type=tcp&headerType=http&path=%2Findex&host=a.com#sec-auto-http",
    f"vless://{U}@example.com:443?type=kcp&seed=s33d&headerType=wechat-video#kcp",
    f"vless://{U}@example.com:443?type=mkcp&headerType=srtp#mkcp",
    f"vless://{U}@example.com:443?type=httpupgrade&path=%2Fup&host=h.com&security=tls&sni=s.com#httpupgrade",
    f"vless://{U}@example.com:443?type=splithttp&path=%2Fsp#splithttp",
    f"vless://{U}@example.com:443?type=quic&security=tls#quic-unsupported",
    f"vless://{U}@example.com:443?type=h2&security=tls#h2-unknown",
    f"vless://{U}@example.com:443?allowInsecure=1&security=tls#insecure",
    f"vless://{U}@example.com:443?insecure=1&security=tls#insecure2",
    f"vless://{U}@example.com:443?a=1&a=2&=x&novalue&empty=&path=%zz#dup-keys-bad-escape",
    f"vless://{U}@example.com:443?path=%2F%3Fed%3D2048&host=x.com&type=ws#early-data",
    f"vless://{U}@example.com:443?type=ws#remark with space",
    f"vless://{U}@example.com:443?type=ws#plus+sign%2Bplus",
    f"vless://{U}@example.com:443?type=ws#%E2%9A%A1%EF%B8%8F%20Fast%20%7C%20DE",
    f"vless://{U}@-bad-.example.com:443#bad-host",
    f"vless://{U}@example.com:abc#bad-port",
    f"vless://{U}@example.com:99999?type=ws#huge-port",
    f"vless://{U}@127.0.0.1:1080#loopback",
    f"vless://{U}@256.1.1.1:443#bad-ipv4-as-host",
    f"vless://{U}@123.example.com:443#digit-label",
    f"vless://{U}@example.123:443#digit-tld",
    f"vless://user%40mail@example.com:443#encoded-userinfo",
    f"  vless://{U}@example.com:443?type=ws#leading-spaces  ",
    f"vless://{U}@example.com:443/?type=ws&security=tls#slash-before-query",
    f"vless://{U}@example.com:443?type=ws&security=tls&sni=%D0%BF%D1%80%D0%B8%D0%BC%D0%B5%D1%80.%D1%80%D1%84#idn-sni",
    f"vless://{U}@xn--e1afmkfd.xn--p1ai:443?security=tls#punycode",
    f"vless://{U}@example.com:443?security=tls&fp=chrome&alpn=h2#ок кириллица",
    f"vless://{U}@example.com:443?type=ws#frag#second-hash",
    f"vless://{U}@example.com:443?type=ws&path=/plain/path&host=a.com#unencoded-path",
    f"vless://{U}@[::ffff:1.2.3.4]:443#ipv4-mapped",
    f"vless://{U}@[fe80::1%25eth0]:443#scope-id",
    f"vless://{U}@[1:2:3]:443#bad-ipv6",
]
trojan = [
    "trojan://p%40ss%3Aword@example.com:443?sni=example.com#trojan-encoded-pass",
    "trojan://secret@example.com:443?security=none&type=ws&path=%2Ft#trojan-none-ws",
    "trojan://secret@example.com#trojan-no-port",
    "trojan://secret@example.com:8443?type=grpc&serviceName=g&security=tls&fp=chrome&alpn=h2#trojan-grpc",
    "trojan://@example.com:443#trojan-no-pass",
    "trojan://secret@example.com:443?security=reality&pbk=K&sid=1#trojan-reality",
    "Trojan://secret@example.com:443#mixed-case",
]
vd = {"v": "2", "ps": "vmess-ws", "add": "example.com", "port": "443", "id": U, "aid": "0", "scy": "auto",
      "net": "ws", "type": "none", "host": "cdn.example.com", "path": "/ray", "tls": "tls", "sni": "example.com", "fp": "chrome", "alpn": "h2,http/1.1"}
vmess = [
    vm(vd),
    vm({**vd, "port": 443, "ps": "port-number"}),
    vm({**vd, "port": 443.0, "ps": "port-double"}),
    vm({**vd, "port": "443 ", "ps": "port-space"}),
    vm({**vd, "id": "", "ps": "no-id"}),
    vm({**vd, "add": " ", "ps": "blank-add"}),
    vm({**vd, "tls": "", "scy": "", "net": "", "ps": "defaults"}),
    vm({**vd, "tls": "reality", "ps": "reality"}),
    vm({**vd, "net": "grpc", "path": "grpcsvc", "ps": "grpc"}),
    vm({**vd, "net": "xhttp", "mode": "stream-one", "ps": "xhttp-mode"}),
    vm({**vd, "ps": "Россия 🇷🇺 «кавычки»"}),
    vm({**vd, "ps": 12345}),
    vm({**vd, "ps": None}),
    vm({**vd, "tls": "auto", "ps": "tls-auto-junk"}),
    vm({**vd, "net": "kcp", "type": "wechat-video", "ps": "kcp"}),
    vm({**vd, "net": "tcp", "type": "http", "host": "a.com", "path": "/p", "ps": "tcp-http"}),
    vm(vd, pad=False),
    vm({**vd, "ps": "urlsafe ~~~ ???"}, urlsafe=True),
    "vmess://" + b64(json.dumps(vd))[:40] + "\n" + b64(json.dumps(vd))[40:],
    "vmess://" + b64("{'add':'example.com','port':443,'id':'" + U + "','ps':'single-quotes'} trailing garbage"),
    "vmess://" + b64("/* comment */ {add: example.com, port: 443, id: " + U + ", ps: unquoted; net=ws}"),
    "vmess://" + b64('{"add":"example.com","port":"0x1BB","id":"' + U + '","ps":"hex-port-string"}'),
    "vmess://" + b64('{"add":"example.com","port":0x1BB,"id":"' + U + '","ps":"hex-port-literal"}'),
    "vmess://" + b64('{"add":"example.com","port":0443,"id":"' + U + '","ps":"octal-port"}'),
    "vmess://" + b64('{"add":"example.com","port":true,"id":"' + U + '","ps":"bool-port"}'),
    "vmess://" + b64('{"add":"example.com","port":{"a":1},"id":"' + U + '","ps":"obj-port","host":{"x":"/y"}}'),
    "vmess://" + b64('["not","object"]'),
    "vmess://" + b64("not json at all"),
    f"vmess://{U}@example.com:443?type=ws&security=tls#vmess-std-query",
    "vmess://###",
    "vmess://",
    "VMESS://" + b64(json.dumps(vd)),
    "vmess://﻿" + b64(json.dumps(vd)),
    "vmess://" + b64("﻿" + json.dumps({**vd, "ps": "bom-inside"})),
]
ss = [
    "ss://" + b64("aes-256-gcm:pass123", urlsafe=True, pad=False) + "@example.com:8388#ss-sip002-b64",
    "ss://" + b64("chacha20-ietf-poly1305:p@ss:with:colons") + "@1.2.3.4:443#ss-sip002-padded",
    "ss://aes-128-gcm:plainpass@example.com:8388#ss-plain-userinfo",
    "ss://2022-blake3-aes-256-gcm:a2V5MQ%3D%3D%3Aa2V5Mg%3D%3D@example.com:443#ss-2022-encoded",
    "ss://" + b64("aes-256-gcm:pass") + "@example.com:8388/?plugin=obfs-local%3Bobfs%3Dhttp#ss-plugin",
    "ss://" + b64("aes-256-gcm:pass") + "@[2001:db8::2]:8388#ss-ipv6",
    "ss://" + b64("aes-256-gcm:pass") + "@example.com#ss-no-port",
    "ss://" + b64("AES-256-GCM:legacyPass@legacy.example.com:8388") + "#ss-legacy%20%D0%BB%D0%B5%D0%B3",
    "ss://" + b64("aes-256-gcm:pw@host.example.com:8388/") + "#ss-legacy-slash",
    "ss://" + b64("aes-256-gcm:pw") + "@legacyat.example.com:8388#ss-legacy-left-b64",
    "ss://" + b64("aes-256-gcm:@example.com:8388") + "#ss-empty-pass",
    "ss://" + b64(":pw@example.com:8388") + "#ss-empty-method",
    "ss://" + b64("nocolon") + "@example.com:8388#ss-no-colon",
    "ss://" + b64("aes-256-gcm:pw@example.com:notaport") + "#ss-legacy-bad-port",
    "ss://" + b64("aes-256-gcm:pw\n@example.com:8388") + "#ss-legacy-newline",
    "ss://garbage!!!#ss-garbage",
    "ss://" + b64("aes-256-gcm:pw@example.com:8388") + "#bad%zzescape",
]
other = [
    "hysteria2://pass@example.com:443?sni=a#hy2",
    "hy2://pass@example.com:443#hy2-short",
    "tuic://uuid:pass@example.com:443#tuic",
    "wireguard://key@example.com:51820#wg",
    "socks://dXNlcjpwYXNz@example.com:1080#socks",
    "HYSTERIA2://x@y:1#upper-hy2",
    "http://example.com/sub",
    "just some text",
    "#comment-like",
    "vless:/missing-slash@example.com:443",
]
os.makedirs(os.path.join(C, "links"), exist_ok=True)
for name, rows in [("vless", vless), ("trojan", trojan), ("vmess", vmess), ("ss", ss), ("other", other)]:
    with open(os.path.join(C, "links", name + ".txt"), "w", encoding="utf-8", newline="\n") as f:
        f.write("## сгенерировано make-corpus.py — не править руками\n")
        for r in rows:
            assert "\r" not in r
            # Ссылка с \n (vmess с переносом) в корпус построчно не ложится → \n заменяется пробелом-маркером тела
            f.write(r.replace("\n", "") + "\n")

# Тела подписок (один файл = одно тело HTTP-ответа)
links_all = vless[:5] + trojan[:2] + vmess[:3] + ss[:3]
cfg = lambda remarks, proto, settings, stream: {"remarks": remarks, "outbounds": [
    {"tag": "proxy", "protocol": proto, "settings": settings, "streamSettings": stream},
    {"tag": "direct", "protocol": "freedom"}]}
vnext = lambda addr, port, **u: {"vnext": [{"address": addr, "port": port, "users": [{"id": U, **u}]}]}
servers = lambda addr, port, **s: {"servers": [{"address": addr, "port": port, "password": "pw", **s}]}
furk = [
    {"remarks": "Автовыбор", "outbounds": [{"tag": "proxy", "protocol": "vless", "settings": vnext("0.0.0.0", 1)}]},
    cfg("DE-1", "vless", vnext("de.example.com", 443, encryption="none", flow="xtls-rprx-vision"),
        {"network": "tcp", "security": "reality", "realitySettings": {"serverName": "www.google.com", "fingerprint": "chrome", "publicKey": "PK", "shortId": "ab", "spiderX": "/"}}),
    cfg("NL-ws", "vless", vnext("nl.example.com", 443, encryption=""),
        {"network": "ws", "security": "tls", "tlsSettings": {"serverName": "nl.example.com", "alpn": ["h2", "http/1.1"], "allowInsecure": True}, "wsSettings": {"path": "/ws", "headers": {"Host": "cdn.nl"}}}),
    cfg("FI-vmess", "vmess", vnext("fi.example.com", 8443, security=""), {"network": "grpc", "grpcSettings": {"serviceName": "gs"}}),
    cfg("", "trojan", servers("tr.example.com", 443), {"network": "httpupgrade", "security": "tls", "httpupgradeSettings": {"path": "/u", "host": "h"}}),
    cfg("SS", "shadowsocks", servers("ss.example.com", 8388, method="aes-256-gcm"), None),
    cfg("XH", "vless", vnext("[2001:db8::5]", 443), {"network": "splithttp", "splithttpSettings": {"path": "/x", "host": "xh", "mode": "auto"}}),
    cfg("KCP", "vless", vnext("k.example.com", "443"), {"network": "kcp", "kcpSettings": {"header": {"type": "dtls"}, "seed": "sd"}}),
    cfg("TCP-HTTP", "vless", vnext("t.example.com", 80), {"network": "raw", "rawSettings": {"header": {"type": "http", "request": {"path": ["/a", "/b"], "headers": {"Host": ["th.com"]}}}}}),
    {"remarks": "no-proxy-tag", "outbounds": [{"tag": "direct", "protocol": "freedom"}, {"tag": "x", "protocol": "trojan", "settings": servers("np.example.com", 443)}]},
    {"remarks": "no-outbounds"},
    "not-an-object",
]
bodies = {
    "b64-std.txt": b64("\n".join(links_all)),
    "b64-urlsafe-nopad.txt": b64("\n".join(links_all), urlsafe=True, pad=False),
    "b64-wrapped-76.txt": "\n".join([b64("\n".join(links_all))[i:i+76] for i in range(0, len(b64("\n".join(links_all))), 76)]) + "\n",
    "plain.txt": "\n".join(links_all) + "\n",
    "plain-crlf.txt": "\r\n".join(links_all) + "\r\n\r\n",
    "plain-with-comments.txt": "# profile-title: Test\n# support-url: https://t.me/x\n\n" + "\n".join(links_all[:3]),
    "b64-vmess-only.txt": b64("\n".join(vmess[:2])),
    "b64-not-links.txt": b64("hello world, nothing here"),
    "empty.txt": "",
    "whitespace.txt": "  \n\t\n",
    "json-array.json": json.dumps(furk, ensure_ascii=False, indent=2),
    "json-single.json": json.dumps(furk[1]),
    "json-broken.json": '[{"outbounds": [',
    "clash-yaml.txt": "proxies:\n  - name: a\n    type: vless\n    server: example.com\n",
    "html-error.txt": "<html><body>429 Too Many Requests</body></html>",
}
os.makedirs(os.path.join(C, "bodies"), exist_ok=True)
for name, body in bodies.items():
    with open(os.path.join(C, "bodies", name), "w", encoding="utf-8", newline="") as f:
        f.write(body)
print("corpus ok:", sum(len(x) for x in [vless, trojan, vmess, ss, other]), "links,", len(bodies), "bodies")
