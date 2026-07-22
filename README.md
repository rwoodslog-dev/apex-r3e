# APEX — Coach télémétrie RaceRoom

Analyse de télémétrie pour RaceRoom Racing Experience. Enregistre tes tours,
affiche un dashboard temps réel, et compare tes performances.

**Aucune installation.** Un seul `.exe`, rien à configurer.

---

## Installation

1. Télécharge **APEX.exe** depuis la [dernière release](../../releases/latest)
2. Double-clique dessus
3. Lance RaceRoom et va en piste

Le dashboard s'ouvre automatiquement sur `http://localhost:8422`.

> Si Windows SmartScreen bloque le fichier : clic droit → Propriétés → coche
> **Débloquer** → OK. C'est normal pour un exe non signé.

---

## Ce que ça fait

**Pendant que tu roules** — dashboard temps réel : vitesse, rapport, régime,
accélérateur, frein, direction, temps au tour en cours et écart avec ton
meilleur tour.

**Après la session** — chaque tour est enregistré en CSV (51 colonnes,
60 Hz) dans le dossier `laps/`, classé par circuit.

**Pour analyser** — le [dashboard en ligne](../../) accepte tes fichiers
en glisser-déposer : temps par circuit, écarts, vitesses max/min,
temps en roue libre, pourcentage plein gaz.

---

## Options

```
APEX.exe --hz 100              enregistre à 100 Hz (défaut 60)
APEX.exe --out D:\telemetrie   change le dossier de sortie
APEX.exe --port 9000           change le port du dashboard
APEX.exe --keep-invalid        garde les tours passés par les stands
APEX.exe --no-browser          n'ouvre pas le navigateur
```

---

## Données enregistrées

51 colonnes par échantillon. Les principales :

| Colonne | Unité | Note |
|---|---|---|
| `lap_distance` | m | l'axe de référence pour comparer deux tours |
| `speed_kmh` | km/h | converti depuis les m/s de R3E |
| `throttle` / `brake` | 0-1 | |
| `steer` | -1..1 | |
| `rpm` | tr/min | converti depuis rad/s |
| `g_lon` / `g_lat` | g | freinage / appui latéral |
| `brake_press_*` | | par roue, détecte le blocage |
| `slip_*` | ratio | **dérivé** (voir ci-dessous) |
| `tire_temp_*` | °C | moyenne des 3 zones |

Ordre des roues : `fl` `fr` `rl` `rr`.

**Note sur le slip** — RaceRoom n'expose pas `slip_ratio` ni `slip_angle`
(contrairement à Assetto Corsa). Ces colonnes sont calculées à partir de
`tire_speed` vs `car_speed` :
- négatif sous freinage = la roue tourne moins vite que la voiture → **blocage**
- positif à l'accélération → **patinage**

---

## Compiler soi-même

Pas nécessaire — les releases sont compilées automatiquement. Mais si tu veux :

```
csc /optimize /out:APEX.exe src\Program.cs src\Telemetry.cs ^
    src\WebServer.cs src\EmbeddedDashboard.cs
```

`csc.exe` se trouve dans `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\`.

Après avoir modifié `src/live.html`, régénère le dashboard embarqué :

```
python src/embed.py
```

Le workflow GitHub Actions le fait tout seul à chaque push.

---

## Architecture

```
src/
  Program.cs             boucle principale : lit la SHM, écrit les CSV, diffuse le live
  Telemetry.cs           décodage de la shared memory (offsets vérifiés)
  WebServer.cs           serveur HTTP + WebSocket, sans dépendance
  live.html              dashboard temps réel (embarqué dans l'exe)
  dashboard.html         dashboard hors ligne (glisser-déposer)
  EmbeddedDashboard.cs   généré depuis live.html
docs/
  index.html             version GitHub Pages du dashboard hors ligne
```

**Pourquoi lire les octets bruts plutôt que marshaller la struct ?**
Le `R3E.cs` officiel utilise des structs génériques (`TireData<T>`,
`Vector3<T>`) que `Marshal.PtrToStructure` ne sait pas convertir — ça
planterait à l'exécution. Les offsets sont donc calculés depuis le `r3e.h`
officiel v3.5 et lus directement.

**Pourquoi TcpListener et pas HttpListener ?**
`HttpListener` demande une réservation d'URL (`netsh`) donc des droits
admin. `TcpListener` sur loopback n'en a pas besoin : double-clic et ça marche.

---

## Compatibilité

Testé contre l'API shared memory R3E **v3.5**
([r3e.h officiel](https://github.com/kwstudios-sweden/r3e-api)).

Si RaceRoom change de version d'API, APEX affiche un avertissement au
démarrage plutôt que de produire silencieusement des données fausses.

---

## Licence

MIT — fais-en ce que tu veux.
