# Validation 0.19.0-beta.1 — 16 octobre 2026

## Recettes

- Suite complète du moteur sous .NET 10 : **718 tests réussis** après la revue
  de l’ancien chemin de maintenance.
- Suite d’interface de la livraison portable : **435 tests réussis**, avec
  contrôles de géométrie Stockage à 1280 × 860 et 980 × 620, prévisualisation,
  confirmation annulée, filtres, annulation de l’analyse, résultats de verrou et
  lecture du rapport automatique. Compatibilité Framework ↔ .NET 10 validée.
- Après l’ajout de l’affichage individuel des bases et gros fichiers, puis les
  dernières corrections de revue : **41 tests Stockage réussis sous Framework
  et 41 sous .NET 10**. Les fichiers
  d’historique sont classés comme tels et ne sont pas proposés à la suppression.
- Liens physiques et jonctions NTFS réels, verrou Windows réel, PID vivant,
  réutilisé ou inaccessible, changement de propriétaire après préflight,
  sources/identités sous faux marqueur, annulation et suppression partielle.
- Budgets indépendants du disque du PC dans les tests : seuils, marge, débordement,
  volume cible, export grandissant, refus avant fork et reprise de migration.
- L’ancienne API de maintenance utilise le moteur protégé : conservation d’un
  fichier d’identité, refus d’un lot mixte, verrou Windows détaillé, remontée
  d’une analyse partielle avec jonction et nettoyage d’un scratch réellement
  jetable. Le dialogue WinForms exécute l’analyse hors du thread d’interface.

## Contrôles réels

Un nouveau service RPC Codex, sans connexion à un compte ni création de chat,
a été initialisé puis fermé. Son processus a disparu, son home temporaire a été
retiré et le nombre de répertoires `temporary` est revenu de zéro à zéro.
Ce contrôle a utilisé le runtime Framework natif du moteur ; PowerShell Core
ne peut pas exécuter directement certaines API Framework de gestion des ACL.
Il a été répété avec succès après le renforcement des modes de partage des
handles et l’actualisation finale de la livraison portable.

La base d’historique du compte principal a été mesurée en lecture seule : environ
5,77 Gio, catégorie `history`, `CanClean=false`, aucune erreur de mesure.

La 0.19 a remplacé uniquement le Switcher principal et sa fenêtre répond.
Les fenêtres Codex 18228 et 21128 ainsi que l’hôte d’instance 2212 ont gardé leur
PID et leur date de création. Le rapport `storage-maintenance.dpapi` a été écrit
au démarrage, ce qui confirme l’exécution réelle de la maintenance configurée.
Le Switcher principal final porte le PID 79408, démarré à 10:50:36 heure locale ;
ses fichiers ont été actualisés uniquement après l’arrêt de l’ancien principal.

Le garde-fou de compilation a refusé une actualisation de la livraison 0.18
encore utilisée ; son exécutable est resté identique. Le garde-fou d’archive a
refusé de créer le ZIP sous 20 Gio libres, avant modification des livrables.
Les scripts PowerShell ont passé le contrôle syntaxique.

## Empreinte et fichiers conservés

La livraison réutilise **468 fichiers de runtime** par liens physiques immuables,
environ 171 Mio. Ses fichiers applicatifs propres occupent environ **4,96 Mio**.
Pas de SDK, dépendances ni copie de projet supplémentaire installés. Les dossiers
de tests moteur ont été retirés par leurs `finally` ; les captures et données de
recette de l’interface réutilisent le répertoire existant.

Espace libre observé : environ **3,84 Gio avant** et **3,82 Gio après**. L’activité
des autres processus empêche d’attribuer précisément ce delta à cette livraison.
Aucun gain de plusieurs Gio n’est revendiqué. Les historiques, projets et
livraisons encore utilisées sont conservés.

Le contrôle automatique a refusé avant PowerShell la suppression de l’ancien
dossier `work/cli-schema-audit` (`blocked by policy`). Il est conservé. Aucun
autre interpréteur, marqueur rétroactif ou nouvelle route de suppression n’a été
utilisé pour le retirer. Les anciens profils de recette sans preuve de propriété
ne sont pas ajoutés à la liste de nettoyage automatique.

Un probe expérimental de changement de jonction a également été refusé avant
exécution par le contrôle automatique. Aucun autre interpréteur ne l’a repris.
Cette course n’est donc pas présentée comme une exploitation démontrée. Les
handles des répertoires et de leur chaîne d’ancêtres refusent désormais les
partages WRITE et DELETE ; les tests ordinaires de jonctions et de nettoyage
par lots passent avec ces modes plus stricts.

La maintenance ne maîtrise pas les installations ou commandes arbitraires des
agents. Elle ne purge pas les conversations ou bases natives et ne modifie pas
le fichier d’échange Windows. Les hooks/filtres Git peuvent dépasser l’estimation
connue d’un checkout ; cette limite n’est pas présentée comme une garantie.
