# Validation de la version 0.18.0-beta.1

Recette du 15 octobre 2026 sous Windows, avec le CLI natif
`0.162.0-alpha.17.2` du Codex installé. Aucune instance Codex ouverte n’a été
fermée. Les PID et dates de démarrage des fenêtres protégées ont été comparés
avant et après chaque recette.

Vérifications automatisées : **670 tests** dans la suite générale .NET10,
**183 tests ciblés finaux .NET10**, **124 tests ciblés finaux Framework** et
**409 tests d’interface** sur la livraison portable. Les tests ciblés recouvrent
la suite générale ; ces nombres ne doivent pas être additionnés comme des cas
indépendants. Les courses d’approbation ont aussi été répétées 200 fois.
La suite ciblée se relance avec `scripts/test.ps1 -HeadlessOnly`, ou avec
`dotnet run --project tests/Core.Net10.csproj -- <dossier-vide> --headless`.

- Cloud : mission fictive terminée dans le seul environnement laboratoire
  autorisé, compte propriétaire vérifié, réponse finale exacte, fenêtre fermée.
- Ressources : inventaire des plugins et lecture réelle d’une liste de pages
  sur deux profils fermés, sans démarrer un tour de modèle. Une recette finale
  passe aussi par `ToolTunnel.Owner` et `Connection` de production : 274 outils,
  une page listée, compte vérifié et registre de performances retiré au Dispose.
- Connexion : renouvellement natif réellement exécuté sur le profil fermé,
  avec identité permanente inchangée. Les cas expiré, révoqué, profil occupé,
  mauvais compte, GET401 et CONNECT401 sont aussi couverts par des tests.
- Agent local : tour natif terminé, liaison MCP au chat et lecture des instances
  réussies, session comparée en mémoire, compte et thread vérifiés, réponse
  finale UTF8 exacte. Aucun shell ni fichier utilisé dans cette recette réussie.
- Écriture locale : une autre recette a demandé une confirmation native après
  échec du bac à sable Windows. Elle a expiré sans réponse reçue par le moteur ;
  le fichier n’a donc pas été déclaré validé. Aucune approbation automatique.
- Interventions : fichiers et diff natifs doivent être connus avant d’activer
  l’acceptation d’une modification. Questions, réponses, refus, expiration,
  isolation par compte/tour et conservation des brouillons sont testés.
- Interopérabilité : coffre, règles, tâches et contrats d’outils relus dans les
  deux sens entre .NET Framework et .NET10.

La mémoire mesurée inclut l’arbre du helper, son moteur natif et ses processus
MCP. Pics résidents : **368,2 et 357,4 Mio** pour les premières recettes d’outils,
**395,5 Mio** pour le parcours de tunnel de production,
**390,2 Mio** pour la recette locale MCP réussie (222,5 Mio privés, 17 processus,
127 échantillons). Chaque recette a ensuite confirmé **zéro enfant restant**.
Ces pics constituent le coût observé des helpers, pas une moyenne permanente.
Les familles GUI ouvertes observées consommaient environ 1,35 à 3,34 Gio selon
le moment et le travail. La comparaison concerne des comptes et charges
différents : aucun pourcentage de gain A/B n’est revendiqué. Aucune fenêtre
supplémentaire n’a été ouverte pour un benchmark alors que le PC manquait de RAM.

Les premiers essais ont révélé et corrigé un encodage stdin OEM sous Framework,
un PID de canal périmé, une dépendance du binding MCP aux variables filtrées par
le natif et un risque de fermeture du moteur pendant le traitement d’un retour.
Les reçus ambigus restent conservés sans renvoi automatique de mandat.
Le suivi Cloud est lancé par le relais propriétaire hors de l’arbre jetable du
moteur local ; la fin d’un tour ne tue donc pas son worker Cloud enfant. Le
retour dans une source sans fenêtre reste suivi jusqu’à la fin de son tour.
Les sources de recette sont `tests/HeadlessAcceptance.cs` et
`scripts/test-headless-live.ps1` ; les preuves locales restent dans `work/`, hors Git.

## Historique : version 0.5.0-beta.1

Vérifications du 3 octobre 2026 : **144 tests hors ligne réussis**, processus de
moteur réellement démarré/arrêté/repris, installation idempotente du plugin,
chargement réel des deux skills et des outils MCP par Codex, compilation et rendu
des nouvelles fenêtres. Le détail, les preuves de recette entre comptes et les
limites de qualification figurent dans [IMPLEMENTATION.md](IMPLEMENTATION.md).

## Historique : version 0.4.0

Vérifications du 2 octobre 2026, Windows et OpenAI.Codex 26.924.2738.0.

- Compilation des exécutables GUI et console sans téléchargement de dépendances,
  avec tous les avertissements C# traités comme erreurs.
- **100 tests hors ligne réussis** : les 68 existants et 32 tests du relais.
  Couverture : DPAPI, identité, chemins, version du dossier, idempotence, concurrence,
  arrêt après envoi ambigu, résultats finaux, coupure du retour, reprise sans renvoi,
  conversations initiales et continuations natives, troncature et permissions.
- Rendu visuel de l'écran Relais en taille normale et minimale, avec données fictives.
- **Test réel entre deux comptes distincts**, session habituelle et instance gérée,
  utilisant les véritables outils Codex et Sites :
  1. Compte A : création et publication v1 d'un Site privé de test.
  2. Compte B : `get_site` renvoie effectivement NOT_FOUND ; édition de la v2 dans
     le même dossier physique puis demande envoyée par CreezioRelay.
  3. Compte A : nouvelle conversation, contrôle SHA256, publication v2 sur le même
     `project_id` et la même URL. Statut de déploiement relu indépendamment : succeeded.
     Audience relue : custom, propriétaire seul, aucun groupe.
  4. Réponse remise à la conversation B, puis accusé envoyé par B et confirmé dans
     la même conversation A. L'état du relais est completed pour les deux demandes.
- Le test a révélé que Codex représente les prompts relayés comme une sortie
  `functionCallOutput`, non comme un `userMessage`. Corrigé et couvert par un test.
- **Permissions** : un ancien chat B conservait read-only/on-request malgré la
  sélection générale Accès complet. Reconnexion depuis un chat dont le contexte
  effectif était danger-full-access/never, puis création réelle d'un nouveau chat B :
  calcul du SHA256 réussi sans élévation ni approbation. Le relais vérifie ces
  permissions avant les envois qui exigent Accès complet et ne modifie aucune policy.
- Le compte B disposait réellement de 0 % de quota Codex et d'un reset disponible.
  Le mécanisme déjà autorisé a consommé un reset ; relecture après les tâches :
  quota hebdomadaire Codex 99 %, resets disponibles 0. Aucun achat de crédits.
- Aucune installation de runtime supplémentaire ; réutilisation du profil géré,
  du checkout et des dépendances déjà présents. La session de coordination initiale
  est restée ouverte. Les données et historiques du test sont conservés localement.

Limites : adaptation à un protocole local interne, test sur une seule version
Codex/Windows ; la lecture des permissions est conservatrice et peut demander une
reconnexion. Aucun compte ni Site de production n'a été migré. Les autres comptes
propriétaires doivent connecter leur propre canal. Une réponse remise ne prouve
pas qu'un nouvel agent a fini son travail ; ce test inclut explicitement l'accusé.

Les preuves personnelles restent locales ; aucun jeton ni identité réelle dans le
rapport public. Les vérifications historiques suivantes concernent la v0.3.

---

# Validation de la version 0.3.0

Vérifications effectuées le 1er octobre 2026 sur l'hôte Windows de développement.

## Effectué

- Compilation C# avec le compilateur Windows .NET Framework, avertissements
  traités comme erreurs ; aucun téléchargement de dépendances.
- **68 tests automatisés hors ligne**, dont les 20 vérifications initiales : identité par utilisateur et espace,
  refus des fichiers invalides, quotas inconnus, limites multiples, valeurs
  anciennes, chiffrement DPAPI, coffre corrompu préservé, collisions de fichiers
  temporaires, client ouvert, modes keyring/auto/ephemeral, paramètres TOML,
  bascule nominale, sauvegarde, restauration octet pour octet, échec de sauvegarde,
  absence initiale de connexion, écritures concurrentes, confinement des
  suppressions, restauration d'un ancien mode API key et conservation de l'historique.
- **24 vérifications du reset automatique** : métadonnées absentes ou malformées,
  compte local uniquement, seuil exact à 1 % et 0 %, fenêtre hebdomadaire, identité
  du quota dans le format historique,
  fraîcheur, compte changé juste avant l'envoi, crédits expirés/incompatibles,
  nombre seul, priorité d'expiration, intention durable avant envoi, reprise après
  timeout/redémarrage avec la même clé, maximum de trois tentatives, lecture après
  acceptation, délai de cinq minutes, résultats négatifs/inconnus, concurrence,
  annulation et migration des paramètres.
- Essai du protocole avec **Codex CLI 0.158.0-alpha.2.1** : serveur isolé,
  initialisation JSON-RPC et lecture d'un état non connecté.
- **Lecture réelle des quotas du compte local**, via le mode externe
  `chatgptAuthTokens` du serveur Codex ; une catégorie de quotas reçue. Le fichier
  de connexion actif est resté identique avant et après. Ni jetons, ni identité,
  ni valeurs personnelles publiés dans les journaux de test.
- Lecture réelle de `rateLimitResetCredits` : métadonnées présentes et interprétées
  par le nouveau modèle. **Aucun appel de consommation effectué pendant cet essai.**
- Arrêt des serveurs auxiliaires créés et suppression de leurs dossiers de travail
  à la fin des essais.
- Rendu et inspection visuelle de la fenêtre avec deux comptes fictifs.
- **24 tests multi-instance** : migration v1, coffre chiffré préservé, associations
  persistantes et futures, refus des comptes exclus, bascule limitée au profil choisi,
  instance active protégée, exclusion mutuelle, archivage réversible, révocation des
  associations, déduplication des comptes pour les resets, exclusion des profils fermés,
  recommandation filtrée, import d'une connexion propre à un espace, synchronisation des
  jetons récents, retrait sans suppression des profils, chemins invalides, configuration
  légère, mode keyring refusé, environnement isolé, vérification PID + heure de démarrage,
  mise à jour d'une connexion ancienne avant ouverture et refus d'ouvrir une archive vide.
- **Essai réel avec OpenAI.Codex 26.924.2738.0** : nouvelle fenêtre initialisée avec
  un compte distinct, détection par un gestionnaire fraîchement recréé, lecture réelle
  des quotas, fermeture de son seul arbre de processus. Les processus d'origine et les
  empreintes de leurs fichiers auth.json/config.toml sont restés inchangés.
- Deux lancements consécutifs du même profil : un avertissement réseau au premier,
  aucun au second. Aucun nouveau téléchargement du runtime bureautique n'a été créé.
- La séparation de deux instances gérées, dont l'une ouverte, est couverte hors ligne.
  L'essai réel porte sur la session habituelle et une instance gérée simultanées.

## À valider en interaction

- Exécution d'une tâche complète dans deux fenêtres de bureau simultanées.
- Consommation d'un crédit réel lorsqu'un compte atteint le seuil : les tests ne
  dépensent pas de crédit pour simuler artificiellement une situation de limite.
- Bascule réelle entre deux comptes, après fermeture volontaire de Codex, puis
  contrôle du profil affiché après sa réouverture.
- Recette sur plusieurs versions Windows, thèmes système et densités d'affichage.

Le badge « Compte local » et la vérification du fichier ne prouvent pas à eux seuls
que l'application de bureau a chargé ce compte. La bêta ne prétend pas une reprise
automatique des conversations. Seule l'instance créée pour le test a été fermée.
