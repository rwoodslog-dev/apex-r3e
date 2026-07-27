#!/usr/bin/env python3
"""Genere EmbeddedDashboard.cs depuis live.html (exe autonome).
   Decoupe le base64 en blocs de 60 Ko pour eviter l'erreur
   'expression too long' du compilateur .NET Framework."""
import base64

html = open('live.html', encoding='utf-8').read()
b64 = base64.b64encode(html.encode('utf-8')).decode('ascii')

# decoupe en blocs de 60000 chars max (bien sous la limite csc)
BLOCK = 60000
blocks = [b64[i:i+BLOCK] for i in range(0, len(b64), BLOCK)]

lines = [
    '// GENERE AUTOMATIQUEMENT par embed.py - ne pas editer a la main.',
    'using System;', 'using System.Text;', '',
    'static class EmbeddedDashboard', '{',
    '    static string _cache;',
    '    public static string Html {',
    '        get {',
    '            if (_cache == null) {',
]

# chaque bloc est un string literal separe, concatene via StringBuilder
lines.append('                var sb = new StringBuilder(%d);' % len(b64))
for i, blk in enumerate(blocks):
    # decoupe chaque bloc en lignes de 200 chars pour la lisibilite
    chunks = [blk[j:j+200] for j in range(0, len(blk), 200)]
    lines.append('                sb.Append(')
    for k, c in enumerate(chunks):
        end = ');' if k == len(chunks)-1 else ' +'
        lines.append('                    "%s"%s' % (c, end))

lines += [
    '                _cache = Encoding.UTF8.GetString(Convert.FromBase64String(sb.ToString()));',
    '            }',
    '            return _cache;',
    '        }',
    '    }',
    '}',
]

open('EmbeddedDashboard.cs', 'w', encoding='utf-8').write('\n'.join(lines) + '\n')
print('embedded live.html (%d Ko base64, %d blocs)' % (len(b64)//1024, len(blocks)))
