#!/usr/bin/env python3
"""Конвертер Kotlin `data class X(...)` → C# `sealed record X { ... }` для портирования сторов 1:1.

Сохраняет порядок полей, дефолты и ВСЕ комментарии (в них «почему так» — главное). Имена полей не меняет
(camelCase, как в Kotlin; [JsonPropertyName] — чтобы JSON-файлы совпадали со схемой Android).
Set → HashSet<T> (не IReadOnlySet: System.Text.Json не умеет его читать — поймано тестом совместимости).
Тело класса (вычисляемые свойства, функции) НЕ переносит — его переносят руками.
Использование: kt2cs_dataclass.py <file.kt> <ClassName>  → C# в stdout.
"""
import re, sys

TYPES = {"Double": "double", "Int": "int", "Long": "long", "String": "string", "Boolean": "bool", "Float": "float"}

def cs_type(t):
    t = t.strip()
    nullable = t.endswith("?")
    base = t[:-1] if nullable else t
    m = re.match(r"(Set|List|Map)<(.+)>$", base)
    if m:
        kind, args = m.group(1), [cs_type(a) for a in split_args(m.group(2))]
        r = {"Set": f"HashSet<{args[0]}>", "List": f"IReadOnlyList<{args[0]}>",
             "Map": f"IReadOnlyDictionary<{', '.join(args)}>"}[kind]
    else:
        r = TYPES.get(base, base)
    return r + ("?" if nullable else "")

def split_args(s):
    out, depth, cur = [], 0, ""
    for c in s:
        if c == "<": depth += 1
        if c == ">": depth -= 1
        if c == "," and depth == 0: out.append(cur); cur = ""; continue
        cur += c
    out.append(cur)
    return [x.strip() for x in out]

def cs_default(d, t):
    d = d.strip()
    ct = cs_type(t)
    m = re.match(r"(setOf|listOf|mutableListOf)\((.*)\)$", d, re.S)
    if m:
        items = [qualify(x) for x in split_args(m.group(2))] if m.group(2).strip() else []
        inner = ct.split("<", 1)[1].rstrip(">?")
        if m.group(1) == "setOf": return f"new HashSet<{inner}> {{ {', '.join(items)} }}" if items else f"new HashSet<{inner}>()"
        return "[" + ", ".join(items) + "]"
    if d in ("emptyList()", "listOf()"): return "[]"
    if d in ("emptySet()", "setOf()"):
        inner = ct.split("<", 1)[1].rstrip(">?"); return f"new HashSet<{inner}>()"
    if d in ("emptyMap()", "mapOf()"):
        inner = ct.split("<", 1)[1].rstrip(">?"); return f"new Dictionary<{inner}>()"
    return qualify(d)

def qualify(x):
    return x.strip()

def convert(src, name):
    m = re.search(r"data class " + re.escape(name) + r"\s*\(", src)
    if not m: raise SystemExit("not found: " + name)
    i, depth = m.end(), 1
    while depth:
        c = src[i]
        if c == "(": depth += 1
        elif c == ")": depth -= 1
        elif c == '"':  # пропустить строковый литерал
            j = i + 1
            while src[j] != '"' or src[j-1] == "\\": j += 1
            i = j
        i += 1
    body = src[m.end():i-1]
    out = []
    pending = ""
    for raw in body.split("\n"):
        line = raw.rstrip()
        s = line.strip()
        if not s: out.append(""); continue
        if s.startswith("//") or s.startswith("/*") or s.startswith("*"):
            out.append("    " + s if not s.startswith("*") else "     " + s); continue
        pending += " " + s
        # поле завершено, если скобки сбалансированы и строка кончается запятой (или это последнее поле)
        code = re.sub(r'"(?:\\.|[^"\\])*"', '""', pending)
        code_nc = code.split("//")[0]
        if code_nc.count("(") != code_nc.count(")") or code_nc.count("<") != code_nc.count(">"):
            continue
        # строковые литералы → плейсхолдеры (внутри бывают «//» в URL), потом вернём
        lits = []
        def hide(mm):
            lits.append(mm.group(0)); return "\x00%d\x00" % (len(lits) - 1)
        masked = re.sub(r'"(?:\\.|[^"\\])*"', hide, pending)
        unhide = lambda t: None if t is None else re.sub(r"\x00(\d+)\x00", lambda mm: lits[int(mm.group(1))], t)
        fm = re.match(r'\s*(?:@\w+(?:\([^)]*\))?\s+)*val\s+(\w+)\s*:\s*([^=]+?)(?:\s*=\s*(.+?))?\s*,?\s*(//.*)?$', masked)
        if not fm:
            out.append("    // TODO(kt2cs): " + pending.strip()); pending = ""; continue
        fname, ftype, fdef, fcom = fm.group(1), fm.group(2), unhide(fm.group(3)), unhide(fm.group(4))
        ct = cs_type(ftype)
        req = "required " if fdef is None else ""
        init = f" = {cs_default(fdef, ftype)};" if fdef is not None else ""
        out.append(f'    [JsonPropertyName("{fname}")] public {req}{ct} {fname} {{ get; init; }}{init}' + (f"  {fcom.strip()}" if fcom else ""))
        pending = ""
    while out and not out[-1]: out.pop()
    while out and not out[0]: out.pop(0)
    return f"public sealed record {name}\n{{\n" + "\n".join(out) + "\n}\n"

if __name__ == "__main__":
    print(convert(open(sys.argv[1], encoding="utf-8").read(), sys.argv[2]), end="")
