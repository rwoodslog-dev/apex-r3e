#!/usr/bin/env python3
"""Genere EmbeddedDashboard.cs depuis live.html (exe autonome)."""
import base64

def chunks_of(path):
    html = open(path, encoding='utf-8').read()
    b64 = base64.b64encode(html.encode('utf-8')).decode('ascii')
    return [b64[i:i+120] for i in range(0, len(b64), 120)]

def emit(name, chunks):
    out = ['    const string ' + name + ' =']
    for i, c in enumerate(chunks):
        end = ';' if i == len(chunks)-1 else ' +'
        out.append(f'        "{c}"{end}')
    return out

lines = ['// GENERE AUTOMATIQUEMENT par embed.py - ne pas editer a la main.',
         'using System;', 'using System.Text;', '',
         'static class EmbeddedDashboard', '{',
         '    static string _cache;',
         '    public static string Html {',
         '        get { if (_cache == null) _cache = Encoding.UTF8.GetString(Convert.FromBase64String(B64)); return _cache; }',
         '    }', '']
lines += emit('B64', chunks_of('live.html'))
lines.append('}')
open('EmbeddedDashboard.cs', 'w', encoding='utf-8').write('\n'.join(lines) + '\n')
print('embedded live.html')
