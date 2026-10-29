#!/usr/bin/env python3
"""
Собрать и ПОДПИСАТЬ сведения об обновлении Windows-версии: update.json + update.json.sig (docs/decisions.md §13.6).

  python3 tools/release/make-update.py --msi out/XGatesWin-0.2-win-x64.msi --notes-file notes.txt

- versionCode/versionName берутся из Directory.Build.props (VersionCode/VersionNameDisplay), packageName — из
  src/XGates.Core/AppInfo.cs: те же значения, что вшиты в сборку, расхождение невозможно.
- update.json пишется UTF-8 БЕЗ BOM: подпись — над точными байтами файла, приложение проверяет их же.
- Подпись: ECDSA P-256 + SHA-256 (openssl dgst), base64 одной строкой. Ключ — вне репозитория
  (по умолчанию ~/.local/share/x-gates-win/update-signing/ec-p256-private.pem или $XGATES_UPDATE_KEY).
- После подписи — САМОПРОВЕРКА открытым ключом, ВШИТЫМ в приложение (UpdateSignature.cs): не совпал ключ —
  релиз не выпускать (установленные копии такое обновление отвергнут).
Секреты в вывод не попадают: openssl читает ключ сам, скрипт его не печатает.
"""
import argparse, base64, hashlib, json, os, re, subprocess, sys, tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
PLATFORM = "win-x64"   # = UpdateChecker.PLATFORM


def read(p):
    with open(os.path.join(ROOT, p), encoding="utf-8") as f:
        return f.read()


def prop(name):
    m = re.search(rf"<{name}>([^<]*)</{name}>", read("Directory.Build.props"))
    if not m: sys.exit(f"нет <{name}> в Directory.Build.props")
    return m.group(1).strip()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--msi", required=True, help="установщик релиза (имя файла = имя вложения релиза)")
    ap.add_argument("--notes-file", help="краткое описание изменений (UTF-8)")
    ap.add_argument("--out", help="каталог для update.json/.sig (по умолчанию — рядом с MSI)")
    ap.add_argument("--key", default=os.environ.get("XGATES_UPDATE_KEY",
                    os.path.expanduser("~/.local/share/x-gates-win/update-signing/ec-p256-private.pem")))
    ap.add_argument("--verify-pubkey", help="открытый ключ для самопроверки (по умолчанию — вшитый в UpdateSignature.cs)")
    a = ap.parse_args()

    if not os.path.isfile(a.key): sys.exit(f"нет ключа подписи: {a.key}")
    code = int(prop("VersionCode"))
    name = prop("VersionNameDisplay")
    pkg = re.search(r'APPLICATION_ID\s*=\s*"([^"]+)"', read("src/XGates.Core/AppInfo.cs")).group(1)
    with open(a.msi, "rb") as f:
        sha = hashlib.sha256(f.read()).hexdigest()
    notes = ""
    if a.notes_file:
        with open(a.notes_file, encoding="utf-8-sig") as f:
            notes = f.read().strip()

    manifest = {
        "versionCode": code,
        "versionName": name,
        "packageName": pkg,
        "notes": notes,
        "artifacts": [{"abi": PLATFORM, "fileName": os.path.basename(a.msi), "sha256": sha}],
    }
    out = a.out or os.path.dirname(os.path.abspath(a.msi))
    os.makedirs(out, exist_ok=True)
    mf = os.path.join(out, "update.json")
    with open(mf, "wb") as f:
        f.write((json.dumps(manifest, ensure_ascii=False, indent=2) + "\n").encode("utf-8"))

    der = subprocess.run(["openssl", "dgst", "-sha256", "-sign", a.key, mf], check=True, capture_output=True).stdout
    with open(mf + ".sig", "w", encoding="ascii", newline="\n") as f:
        f.write(base64.b64encode(der).decode("ascii") + "\n")

    # Самопроверка ключом, который РЕАЛЬНО проверит приложение.
    if a.verify_pubkey:
        pub = open(a.verify_pubkey, encoding="ascii").read()
    else:
        pub = re.search(r"-----BEGIN PUBLIC KEY-----.*?-----END PUBLIC KEY-----",
                        read("src/XGates.Core/Update/UpdateSignature.cs"), re.S).group(0)
    with tempfile.TemporaryDirectory() as t:
        pp, sp = os.path.join(t, "pub.pem"), os.path.join(t, "sig.der")
        open(pp, "w", encoding="ascii").write(pub + "\n")
        open(sp, "wb").write(der)
        v = subprocess.run(["openssl", "dgst", "-sha256", "-verify", pp, "-signature", sp, mf], capture_output=True, text=True)
    if v.returncode != 0:
        os.remove(mf + ".sig"); os.remove(mf)   # неподписанный манифест не оставляем — чтобы не выложить по ошибке
        sys.exit("САМОПРОВЕРКА НЕ ПРОШЛА: ключ подписи не соответствует открытому ключу приложения — релиз НЕ выпускать")
    print(f"update.json: versionCode={code} versionName={name} {os.path.basename(a.msi)} sha256={sha}")
    print(f"подпись: {mf}.sig — самопроверка вшитым ключом пройдена")


if __name__ == "__main__":
    main()
