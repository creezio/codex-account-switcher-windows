# Pilotage natif de Codex Cloud

Le Switcher utilise le compte permanent de l'instance sélectionnée. Il n'ouvre
pas ChatGPT web, ne copie pas une session navigateur et ne passe pas par une
conversation locale pour appeler le service Cloud.

## Transport et isolation

- Adaptateur séparé pour le protocole du Codex Windows 26.1007.2314.0.
- HTTPS vers `codex-cloud-backend.chatgpt.com` pour les configurations.
- WebSocket JSON-RPC sur le même service pour les conversations Cloud.
- Identité de client propre au Switcher ; aucune usurpation du client desktop.
- Jeton du profil sélectionné lu en mémoire. Aucune écriture de `auth.json`.
- Vérification de l'instance, du dossier, du compte permanent et des identifiants
  account/user du jeton avant et après chaque appel.
- Aucune redirection HTTP, aucun repli vers un autre compte ou le CLI Cloud
  historique, aucune approbation native donnée automatiquement.

Ce service n'est pas une API publique documentée. Une évolution incompatible
arrête l'opération avec un diagnostic ; un résultat non vérifié n'est pas une
réussite. Les configurations Cloud et les environnements historiques du CLI
sont deux catalogues distincts.

## Vérifications réelles du 15 octobre 2026

Sur les comptes Creezio et Certivan, le moteur `CreezioRelay.exe` a effectivement :

1. lu les configurations avec l'authentification propre à chaque compte ;
2. créé un nouvel environnement privé sur le seul dépôt laboratoire
   `creezio/codex-cloud-switcher-lab-20261015`, réseau désactivé ;
3. relu cette configuration, renommé l'environnement et relu son nouveau nom ;
4. répété la demande avec le même identifiant local, sans nouveau POST de création.

Les fichiers d'authentification sont restés identiques et les processus Codex
existants ont été préservés. La lecture WebSocket initialize/list fonctionne
également sur les deux comptes.

La création renvoie une révision 1 avec un statut serveur `ready`. Le Switcher
distingue cet état de la publication : il a ensuite démarré la préparation
native dans chaque nouvel environnement, suivi le tour Cloud, préparé sa
version initiale et demandé sa publication explicitement. Les deux versions
publiées ont été relues en révision 2, prêtes. Une troisième configuration
neuve, autorisant les domaines GitHub nécessaires, a suivi le même parcours.

Le parcours de modification d'une configuration déjà publiée reste bloqué :
la création d'un nouveau brouillon renvoie HTTP 403, une réponse HTML et
`cf-mitigated: challenge`. Le refus a été reproduit sur les deux configurations
de laboratoire, avant et après publication. Le Switcher ne tente pas de passer
ce contrôle et n'annonce aucune modification réussie. Ce diagnostic ne prouve
pas un défaut des permissions GitHub du compte.

Une mission C a été exécutée sur le compte certivan, dans la première
configuration où le réseau était désactivé. Le Switcher a récupéré sa réponse,
son commit local et son fichier par lecture native. Ses tests ont réussi,
mais son push a reçu un refus du proxy. Ce résultat a permis de vérifier
que « mission terminée » ne doit jamais signifier « PR publiée ».

Une mission D, dans le troisième environnement avec les domaines GitHub
explicitement autorisés, a ensuite produit la branche
`codex/switcher/22fd364273b040248e8c0b8b6b32d80b`, le commit
`689cee86636fe8aa584cdca7ee4d11adb0f8a451` et la
[PR de laboratoire nº 3](https://github.com/creezio/codex-cloud-switcher-lab-20261015/pull/3).
La vérification GitHub indépendante confirme : compte exécutant certivan,
un seul fichier ajouté (`mission_d.py`), base `main` inchangée à
`a3fbdbe1faa0abea2da269eb09b9e1b13b2f841e`, PR ouverte et non fusionnée.
Les [contrôles GitHub](https://github.com/creezio/codex-cloud-switcher-lab-20261015/actions/runs/38714721913)
ont réussi sur Windows et Ubuntu : 24 tests, dont 6 pour la mission D.
Le résultat a été récupéré dans le journal de missions du Switcher.

Les preuves techniques expurgées restent dans `work/cloud-native-acceptance.json`,
`work/cloud-native-setup-acceptance.json`,
`work/cloud-native-modify-published.json`,
`work/cloud-github-egress-acceptance.json` et
`work/cloud-github-publish-job.json`. Elles ne contiennent aucun jeton.
Les objets du laboratoire sont conservés pour vérification ; aucun
environnement de production n'est réutilisé.

## Reprise des opérations

Chaque création ou renommage possède un identifiant de demande stable et une
empreinte. Le journal conserve uniquement les identifiants, les états et les
codes d'erreur. Les commandes d'installation et les secrets n'y sont pas stockés.
Une réponse de création perdue reste incertaine : aucun deuxième environnement
n'est créé automatiquement. Un reçu terminé provoque une relecture authentifiée,
pas le renvoi d'une ancienne configuration mise en cache.

La modification publiée utilise un brouillon propre, une version de base et une
révision épinglées, puis une opération distante suivie séparément. Le Switcher
refuse d'écraser un brouillon existant et conserve les références de suivi après
sa fermeture. Le succès n'est annoncé qu'après relecture d'une nouvelle version
prête. Un échec ou une interruption n'est jamais transformé en succès.

## Organisation des missions

Les missions Cloud sont distinctes des demandes vers une fenêtre Codex locale.
Le destinataire est un compte et une configuration Cloud explicitement choisis.
Le conteneur Cloud possède son propre checkout : les fichiers locaux non publiés
ne s'y trouvent pas automatiquement.

Une mission de code reçoit sa branche `codex/switcher/<identifiant>`. Son résultat
doit fournir tests, SHA et référence de PR. Une réponse du modèle, un commit, une
publication de branche, une PR et une fusion sont des étapes distinctes.
L'orchestrateur doit vérifier les preuves GitHub avant toute intégration ; une
réponse Cloud n'autorise pas une fusion implicite. Les permissions et capacités
du compte destinataire restent celles accordées par les services.

## CLI

La comparaison du CLI installé 0.162.0-alpha.17.2 avec les distributions stables
0.162.1 et alpha 0.163.0-alpha.11 n'a montré aucune nouvelle API de gestion de ces
configurations dans les six fichiers du client Cloud concernés. Aucune mise à
jour ou dépendance supplémentaire n'a été installée pour ce prototype.

## Étapes de qualification

1. Lecture, création et renommage natifs : validés sur deux comptes.
2. Préparation initiale et publication : validées sur trois configurations neuves.
3. Modification d'une configuration déjà publiée : refus HTTP 403 confirmé.
4. Mission, branche et PR depuis un nouvel environnement : validées avec
   l'accès réseau GitHub configuré explicitement, CI Windows et Ubuntu réussie.
5. Récupération du résultat dans le Switcher et vérification GitHub : validées.
   Le retour automatique dans un chat orchestrateur est couvert par les tests
   du moteur, mais n'a pas été qualifié avec un vrai chat dans cette recette.
6. Catalogues GitHub déjà connectés : validés avec l'authentification propre
   à chaque compte. Première connexion OAuth depuis le Switcher : non implémentée.

Les étapes non qualifiées ne doivent pas être présentées comme une recette
complète Cloud → GitHub → retour orchestrateur.
