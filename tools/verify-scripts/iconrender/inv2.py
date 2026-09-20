import re, unicodedata
from collections import Counter
src = open('src/3FCompare/Localization/LanguageManager.cs', encoding='utf-8').read()

def block(name):
    i = src.index(f'{name} = new()')
    j = src.index('\n    };', i)
    return src[i:j]

pat = re.compile(r'\["([^"]+)"\]\s*=\s*"((?:[^"\\]|\\.)*)"')
def parse(name):
    d = {}
    for m in pat.finditer(block(name)):
        d[m.group(1)] = m.group(2)
    return d

zh = parse('Chinese'); en = parse('English')
cnt = Counter()
keys = {}
for tbl, tag in ((zh, 'zh'), (en, 'en')):
    for k, v in tbl.items():
        for ch in v:
            if ord(ch) < 128:
                continue
            if 0x4E00 <= ord(ch) <= 0x9FFF:      # CJK 汉字
                continue
            if 0x3000 <= ord(ch) <= 0x303F:      # CJK 标点
                continue
            if 0xFF00 <= ord(ch) <= 0xFFEF:      # 全角
                pass
            cnt[ch] += 1
            keys.setdefault(ch, []).append(f'{tag}:{k}')

print('非 ASCII / 非汉字 / 非 CJK 标点 字符统计:')
for ch, c in sorted(cnt.items(), key=lambda x: -x[1]):
    print(f'  {ch!r} U+{ord(ch):04X} {unicodedata.category(ch):3s} {unicodedata.name(ch, "?"):40s} x{c}')
    print(f'      {", ".join(keys[ch])}')
