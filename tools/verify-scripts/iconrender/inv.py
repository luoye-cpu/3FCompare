import re, unicodedata, sys
src = open('src/3FCompare/Localization/LanguageManager.cs', encoding='utf-8').read()

def block(name):
    i = src.index(f'{name} = new()')
    j = src.index('\n    };', i)
    return src[i:j]

pat = re.compile(r'\["([^"]+)"\]\s*=\s*"((?:[^"\\]|\\.)*)"')
def parse(name):
    d = {}
    order = []
    for m in pat.finditer(block(name)):
        d[m.group(1)] = m.group(2)
        order.append(m.group(1))
    return d, order

zh, zho = parse('Chinese')
en, eno = parse('English')
print("keys zh=%d en=%d" % (len(zh), len(en)))
print("missing:", sorted(set(zh) ^ set(en)))
print()

def glyphs(v):
    out = []
    for ch in v:
        cat = unicodedata.category(ch)
        cp = ord(ch)
        if cat in ('So', 'Sm', 'Sk', 'Sc') \
           or (0x2190 <= cp <= 0x21FF) or (0x25A0 <= cp <= 0x25FF) \
           or (0x2B00 <= cp <= 0x2BFF) or cp in (0x2713, 0x2717) \
           or (cat == 'Po' and ch in '\u2014\u2013\u2026\u00b7'):
            out.append(ch)
    return out

rows = []
for k in zho:
    gz = glyphs(zh[k]); ge = glyphs(en.get(k, ''))
    if gz or ge:
        rows.append((k, zh[k], en.get(k, ''), ''.join(gz), ''.join(ge)))

print("含图形字符的 key 数:", len(rows))
print()
for k, zv, ev, gz, ge in rows:
    print(f"{k}\n  ZH={zv!r}  glyphs={gz!r}  codepoints={[hex(ord(c)) for c in gz]}")
    print(f"  EN={ev!r}  glyphs={ge!r}  codepoints={[hex(ord(c)) for c in ge]}")
