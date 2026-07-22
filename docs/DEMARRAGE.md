[DEMARRAGE.md](https://github.com/user-attachments/files/30277203/DEMARRAGE.md)
# Demarrage rapide

## Installer

1. Telecharge **APEX.exe** depuis la derniere release
2. Mets-le ou tu veux (par exemple `C:\APEX`)
3. Double-clique

C'est tout. Aucune installation.

> SmartScreen bloque ? Clic droit sur APEX.exe > Proprietes > coche
> **Debloquer** > OK. Normal pour un exe non signe.

## Utiliser

Double-clique sur `APEX.exe`. Une fenetre noire s'ouvre et ton navigateur
affiche le dashboard.

Lance RaceRoom et va **en piste**. Le dashboard se remplit tout seul :

- vitesse, rapport, regime en direct
- accelerateur / frein / direction
- temps du tour en cours
- ecart avec ton meilleur tour
- la liste des tours se remplit a chaque passage sur la ligne

`Ctrl+C` dans la fenetre noire pour arreter.

Tes tours sont enregistres dans le sous-dossier `laps\`.

## Analyser plus tard

Deux possibilites :

- **En ligne** : va sur la page GitHub Pages du projet et glisse ton
  dossier `laps` dessus
- **Hors ligne** : ouvre `dashboard-hors-ligne.html` (fourni dans le ZIP)

Tout se passe dans ton navigateur, rien n'est envoye sur internet.

## Options

Cree un raccourci vers APEX.exe, clic droit > Proprietes, et ajoute les
options a la fin de la Cible :

```
--hz 100              enregistre a 100 Hz (defaut 60)
--out D:\telemetrie   change le dossier de sortie
--port 9000           change le port du dashboard
--keep-invalid        garde les tours passes par les stands
--no-browser          n'ouvre pas le navigateur automatiquement
```

## Problemes

**Bloque sur "En attente de RaceRoom..."**
La shared memory R3E ne s'active qu'une fois **en piste**. Rester dans
le menu principal ne suffit pas.

**Le navigateur ne s'ouvre pas**
Va manuellement sur http://localhost:8422

**"Le port est deja utilise"**
APEX prend automatiquement un autre port libre — regarde l'adresse
affichee dans la fenetre noire.

**Avertissement de version SHM**
RaceRoom a change son API shared memory. Les donnees peuvent etre
decalees. Ouvre un ticket sur le repo GitHub.
