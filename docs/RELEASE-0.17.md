# Version 0.17.0-beta.2 — Windows x64

## Fiabilisation de la beta.2

Une mission dont l’envoi est incertain, mais dont la conversation Cloud existe,
continue d’occuper sa place dans la limite de concurrence. Le moteur respecte
une mise en pause survenue pendant la lecture d’une configuration avant tout
envoi de mandat. Le résultat destiné au chat orchestrateur attend aussi la fin
de ses outils ou demandes interactives, même lorsque son statut général indique
déjà le repos.

## Environnements Codex Cloud

L’onglet **Instances → Cloud** utilise le compte permanent de l’instance choisie.
Il lit son catalogue GitHub, crée une configuration privée et sépare la
préparation du conteneur, son suivi et sa publication explicite. Le renommage
est confirmé par une relecture du service. Un compte sans connexion GitHub doit
d’abord terminer le consentement officiel ; le Switcher ne fabrique aucune
connexion ni permission.

Les opérations conservent une identité et un reçu privés. Après une réponse
perdue, le suivi rapproche l’état distant ; il ne répète pas aveuglément une
création ou un prompt. La vérification lie chaque appel à l’instance, son compte
et sa configuration. Un brouillon existant n’est pas écrasé automatiquement.

## Missions et orchestration

**Tâches → Missions Cloud** ouvre une liste avec le compte exécutant, un
statut en français et le résultat sélectionnable. Le formulaire impose le choix
du compte et d’un environnement publié. Une réponse d’un ancien compte pendant
le chargement ne remplit pas le sélecteur d’un autre compte. Une mission conserve
le même identifiant après un échec local ou un double clic.

Le rafraîchissement préserve la mission sélectionnée, les positions de lecture,
la sélection de texte et le diagnostic ouvert. Fermer cette fenêtre arrête son
timer visuel. Si le superviseur ne démarre pas, la mission reste enregistrée et
**Démarrer le suivi** le relance sans soumettre un deuxième mandat.

La délégation explicite depuis un agent local utilise les outils Cloud du plugin.
L’environnement distant possède son propre checkout : publier les fichiers
nécessaires sur GitHub avant de lui confier un travail. Les branches des missions
sont distinctes. Une réponse du modèle, un commit local, un push, une PR, la CI
et une fusion sont des preuves différentes ; aucune fusion n’est automatique.

## Validation et limites

- Compilation WPF sans avertissement ni erreur ; 312 vérifications UI réussies,
  avec transport simulé pour tous les nouveaux tests d’interface Cloud.
- Compatibilité des données Framework ↔ .NET 10 vérifiée dans les deux sens.
- Lecture, création, renommage, préparation native et publication initiale
  qualifiés sur deux comptes dans des environnements de laboratoire neufs.
- Mission native exécutée et réponse récupérée. Le premier environnement avait
  le réseau désactivé : ses tests ont réussi, mais son push a été refusé par le
  proxy. Un résultat de mission terminé ne prouve donc pas une livraison GitHub.
- Une nouvelle configuration avec accès réseau GitHub explicite a ensuite permis
  le push et la création de la [PR de laboratoire nº 3](https://github.com/creezio/codex-cloud-switcher-lab-20261015/pull/3),
  vérifiée indépendamment : seul `mission_d.py`, branche
  `codex/switcher/22fd364273b040248e8c0b8b6b32d80b`, SHA
  `689cee86636fe8aa584cdca7ee4d11adb0f8a451`. La
  [CI GitHub](https://github.com/creezio/codex-cloud-switcher-lab-20261015/actions/runs/38714721913)
  a réussi avec 24 tests. Cette PR reste ouverte et non fusionnée.
- La création d’un brouillon d’un environnement déjà publié a reçu HTTP 403 avec
  réponse HTML. Le diagnostic l’affiche sans attribuer arbitrairement le refus
  aux permissions du compte. Aucun changement d’identité HTTP ni réemploi de
  cookies n’est tenté pour passer ce refus.
- Le transport repose sur le protocole interne du Codex Windows
  26.1007.2314.0 ; il ne s’agit pas d’une API publique de gestion Cloud.
- La première connexion OAuth GitHub et la remise réelle dans un chat
  orchestrateur n’ont pas encore été qualifiées de bout en bout.

La recette réelle détaillée et son dernier état se trouvent dans
[CLOUD-NATIVE-PILOT.md](CLOUD-NATIVE-PILOT.md). Les tests UI utilisent des comptes
fictifs et ne ferment aucune instance utilisateur.

## Livraison et mise à jour

Décompresser tout le ZIP, puis ouvrir **CodexAccountSwitcher.exe**. Le runtime
.NET 10 est fourni ; aucun SDK n’est nécessaire sur le PC destinataire. Le
moteur de relais conserve .NET Framework 4.8. Windows 10/11 x64 est requis.

Quitter l’ancien Switcher avant d’ouvrir la nouvelle interface. Garder les
fenêtres Codex ouvertes. Les profils, conversations et données sont conservés.
Ne pas supprimer un ancien dossier encore utilisé par un hôte d’instance ou un
serveur MCP. Un nouveau chat recharge la nouvelle version du plugin.

Pour préparer cette livraison sur le PC de développement, les fichiers de
runtime identiques sont liés physiquement à la livraison précédente après
comparaison SHA-256 avec les packs officiels déjà en cache. Les exécutables du
Switcher et du relais, les configurations, plugins et documents restent des
fichiers distincts. Le ZIP contient de vrais fichiers autonomes, sans dépendance
à l’ancien dossier. Le marqueur local empêche une compilation ultérieure
d’écrire dans ces liens de runtime partagés.
