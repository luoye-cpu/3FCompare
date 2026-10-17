import hashlib
import os

d = r'C:\PLAN\3FCompare\3fp\patches\vfr-pr-20261016'
rows = []
for root, dirs, files in os.walk(d):
    for name in sorted(files):
        p = os.path.join(root, name)
        if name in {'MANIFEST.sha256.txt'} or p.endswith('.7z') or p.endswith('.sha256'):
            continue
        digest = hashlib.sha256(open(p, 'rb').read()).hexdigest()
        rel = os.path.relpath(p, d).replace(os.sep, '/')
        rows.append('%s  %s  %d' % (digest, rel, os.path.getsize(p)))
rows.sort(key=lambda r: r.split()[1])
open(os.path.join(d, 'MANIFEST.sha256.txt'), 'w', encoding='utf-8').write('\n'.join(rows) + '\n')
print('manifest entries', len(rows))
