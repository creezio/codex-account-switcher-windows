# Relais configurable — interface 0.7, protocole 0.6

Le relais transmet des travaux entre des chats de comptes Codex différents sur le même PC Windows. Chaque utilisateur définit ses rôles, projets, ressources et règles. Revue, tests, recherche, accès à un plugin privé et publication sont des usages possibles ; aucun n'est imposé.

## Installation et mise à jour

1. Conserver tout le contenu du ZIP, dont les DLL du runtime .NET 10, les fichiers JSON et `plugins/`.
2. Quitter l'ancien switcher par son icône près de l'horloge → **Quitter**. La croix réduit seulement la fenêtre. Ne pas faire fonctionner deux versions du moteur.
3. Lancer le nouveau `CodexAccountSwitcher.exe`. Les comptes, instances et conversations restent dans leurs dossiers existants.
4. **Agents → Installer l'intégration** : sélectionner les profils souhaités. Le plugin `creezio-relay` est installé par le CLI officiel dans une marketplace locale propre au profil. Il fournit deux skills et onze outils MCP.
5. Après mise à jour, ouvrir un nouveau chat pour charger la nouvelle intégration. Les chats existants peuvent conserver leur ancien serveur MCP jusqu'à leur rechargement. Ne pas écraser un exécutable chargé ; utiliser le dossier de livraison, puis réinstaller l'intégration pour mettre ses chemins à jour.

L'installation automatique dans les instances gérées est facultative dans **Paramètres → Collaboration**. La session habituelle nécessite une installation manuelle. Une intégration désactivée ou retirée dans Codex n'est pas réactivée par l'installation automatique. Le bouton d'installation explicite permet de la remettre.

## Connexion et permissions

Connecter un canal par profil et dossier de projet dans **Agents → Connecter une instance**. Coller la consigne générée dans le chat voulu. Le registre contrôle le compte, le profil, le processus, le dossier et la conversation réelle. Les profils gérés se reconnectent après redémarrage si leur identité reste identique. Une instance externe nécessite une nouvelle connexion explicite.

**Le mode Accès complet est propre au chat.** Approuver une commande, même pour la session, ne transforme pas un chat `workspace-write / on-request` en `danger-full-access / never`. Une fenêtre ou un autre chat affichant Accès complet ne prouve pas le mode du nouveau chat créé par l'API.

Pour chaque nouvelle tâche, le moteur crée d'abord un chat avec une consigne de préparation **sans outil et sans mandat de travail**. Il lit ensuite son contexte de permissions enregistré par Codex. Si le mode requis n'est pas confirmé, la tâche reste « Permissions à régler » et le travail n'est pas transmis. Dans ce chat précis, sélectionner le mode souhaité puis envoyer : « Permissions confirmées, réponds sans outil ». Le moteur relit le contexte avant de continuer. Il ne change pas les permissions, ne répond pas aux approbations et ne contourne pas un refus.

L'exigence `full-access` peut être définie par agent ou canal. Avec `inherit`, un chat déjà utilisé conserve son mode ; pour un nouveau chat, Accès complet reste exigé si le chat de connexion l'avait. Un mode inconnu bloque la préparation. Si un mode soumis aux approbations est accepté, Codex peut légitimement demander des confirmations. Les validations propres aux plugins restent indépendantes des permissions de commandes locales.

Une approbation en cours apparaît « Approbation Codex » dans Travaux. Elle se traite dans le chat destinataire. **Arrêter le moteur ne stoppe pas un tour Codex déjà lancé.** Utiliser le bouton Arrêter de ce chat pour interrompre son exécution.

## Configurer ses usages

Dans **Rôles, projets et règles**, les descriptions sous chaque champ expliquent les valeurs. Les identifiants utilisent des minuscules, chiffres et tirets ; les listes sont séparées par des virgules ; `*` accepte toutes les valeurs dans le périmètre du projet.

| Onglet | À configurer |
|---|---|
| Agents | Canal, rôle, capacités déclarées, types de tâches et projets acceptés, modèle facultatif, concurrence, seuil de quota et permissions. `AutoRoute` rend l'agent candidat au routage. `ReuseConversation` permet de réutiliser un chat terminé du même projet et son historique. |
| Projets | Dossier local exact, canaux source et cible autorisés, instructions, délégation `explicit` ou `rules`, limites de concurrence, de profondeur, de tâches par groupe et de délai de démarrage. |
| Ressources | Projet, identifiant externe, canaux autorisés, capacités et instructions propres à la ressource. Aucun transfert de connexion. |
| Règles | Projet, type de tâche, source, destinations, capacités, priorité et explication de la situation où déléguer. |

La délégation est **explicite par défaut**. Un projet `rules` autorise seulement les règles correspondantes dans le mandat de l'utilisateur. Les règles ne sont pas un analyseur sémantique : le skill utilise leur description, le moteur contrôle les champs structurés et les périmètres. Un destinataire explicite ne dispense jamais des restrictions de projet, de ressource et de capacité.

Exemple générique : un canal `developpement`, un canal `revue` doté de la capacité déclarée `review`, un projet `mon-projet` associé à leur dossier, puis une règle `relecture` qui route le type `review` vers `revue`. Pour un outil privé, ajouter sa capacité au compte qui le possède et créer une ressource limitée à ce canal. Le destinataire vérifie lui-même que l'outil et la ressource sont réellement accessibles.

Le routage considère le périmètre, les règles, la disponibilité, les limites récentes et la charge. Il conserve sa destination après acceptation, sauf réaffectation explicite des demandes automatiques non démarrées autorisée dans le projet. Une cible explicitement choisie ne change pas. Si le seul propriétaire autorisé manque de quota, le travail attend ; un autre compte n'obtient aucun droit supplémentaire. Un même compte dans plusieurs instances partage ses limites.

## Utiliser depuis un agent

Le skill `creezio-relay:delegate-task` appelle `get_setup`, puis connecte le chat avec `scripts/bind-chat.ps1` fourni par le plugin. Une commande initiale est nécessaire : l'interface MCP examinée ne fournit pas d'identifiant de chat appelant vérifiable à chaque appel. Le script utilise les variables réelles de Codex, vérifie la conversation et produit une session temporaire chiffrée. Ne jamais fabriquer ces variables ou réutiliser la session d'un autre chat.

Le skill lit ensuite `list_agents`, soumet avec `submit_job` et suit avec `get_job` ou `wait_job`. Les appels MCP suivants remplacent les commandes shell répétées. Une tâche contient un titre, un mandat, un type libre, un projet, un accès `read`, `write` ou `external`, les capacités/ressources nécessaires et, au besoin, une révision et des empreintes SHA256 relatives au dossier.

- `ReturnToSource` remet le résultat au chat émetteur et relance l'agent : cela consomme de l'utilisation.
- `ReplyTo` poursuit un travail terminé dans son chat destinataire.
- `DependsOn` attend une réussite déclarée des travaux précédents du même chat et projet.
- `Parent` lie une sous-tâche au mandat réellement reçu dans ce chat. Les limites de profondeur et de nombre sont appliquées. Terminer le tour parent après soumission : un parent qui attend en boucle peut conserver la place ou le dossier dont son enfant a besoin.
- Une clé `Id` stable évite une deuxième soumission après perte de réponse. Un contenu différent avec la même clé est refusé.

Le skill destinataire termine avec `CREEZIO_OUTCOME {"status":"succeeded"}`, ou `failed`, `blocked`, `cancelled`. Pour un résultat long, `report_result` stocke jusqu'à 250 000 caractères et des empreintes ; le retour attend la fin effective du tour. `read_result` lit des pages. Le message envoyé au chat source contient au maximum un extrait de 12 000 caractères.

Une réussite déclarée ne prouve pas un effet externe : vérifier le reçu du fournisseur lorsqu'il existe. Une fin de tour sans résultat structuré est `unverified`. Aucun accusé automatique ne doit repartir en boucle.

## Moteur, reprise et fichiers

Le processus caché `CreezioRelay.exe --worker` travaille indépendamment de la fenêtre. Un verrou empêche deux moteurs de prendre le même travail. **Arrêter le moteur** persiste jusqu'à **Démarrer le moteur**, même si un agent soumet un travail. Sans travail, le moteur s'arrête après un court délai, sauf option de maintien actif. Aucun démarrage à l'ouverture de Windows n'est installé.

L'historique chiffré distingue l'envoi, la réponse observée, le résultat déclaré et la remise au chat source. Après un envoi incertain, le moteur recherche les marqueurs dans Codex, au plus trois fois, sans renvoyer la demande. Si aucune preuve ne permet de trancher, « À vérifier » reste affiché. **Relire le résultat** relance cette vérification ; **Classer après vérification** libère la place après contrôle manuel, sans répéter l'action.

La source peut fermer après acceptation : le destinataire travaille, puis le retour attend sa réouverture. Un destinataire fermé n'empêche pas la remise d'un résultat déjà conservé. Une instance lente n'occupe pas de verrou global pendant ses appels réseau/IPC.

Les mêmes chemins désignent les mêmes fichiers pour toutes les instances. Le moteur sérialise les travaux susceptibles d'écrire dans un même dossier et les accès à une même ressource configurée. Ces verrous concernent les tâches du relais ; un éditeur externe peut toujours changer le fichier. Les empreintes sont vérifiées avant dispatch. Dans Diagnostics → Espaces Git, créez ou réutilisez un worktree, puis autorisez son chemin dans le projet (un chemin par ligne). Aucune fusion ni installation de dépendances automatique.

`Annuler la demande en attente` annule une demande encore en file. Pour un tour Codex actif, elle consigne une demande : l'interface native examinée n'expose pas d'arrêt ciblé confirmé. Utiliser **Arrêter** dans le chat. Le relais ne prétend jamais que le tour est arrêté avant de l'observer.

## Stockage et limites

Les données sont dans `%LOCALAPPDATA%\Creezio\CodexAccountSwitcher\relay`, chiffrées par Windows DPAPI. Les nouveaux travaux résident dans `jobs-v3/`, à l'abri des moteurs v0.4 et v0.5. Leurs historiques restent lisibles ; leurs demandes actives doivent être terminées avant la migration, car elles ne sont pas exécutées par le nouveau moteur. L'ancienne version ne sait pas reprendre les nouveaux contrats : terminer ou annuler les travaux v0.6 avant de revenir en arrière.

Les capacités sont déclarées et l'inventaire des plugins confirme leur installation/activation, pas l'accès à toutes leurs ressources. Le MCP utilise un protocole stdio JSON-RPC minimal vérifié avec Codex, sans serveur réseau. La liaison bureau s'appuie sur l'interface locale des outils de l'application, dont la compatibilité peut évoluer. Les permissions sont lues dans les fichiers de contexte locaux ; si leur format devient inconnu, la préparation reste bloquée.

Le switcher ne modifie pas les droits du fournisseur, ne copie pas les connexions entre agents et ne partage pas les secrets de plugins. Les profils sous un même utilisateur Windows ne constituent toutefois pas une frontière de sécurité système. Les réglages `read/write/external` sont un mandat appliqué au destinataire, pas un nouveau bac à sable OS.

Le suivi et les resets suivent les politiques choisies dans Limites. Une supervision indépendante de la fenêtre peut être activée. Le moteur de relais lit les quotas et attend leur rétablissement ; il n'achète aucun crédit et ne consomme aucun reset lui-même. Le binaire est une bêta non signée ; les tests et limites de qualification sont détaillés dans [IMPLEMENTATION.md](IMPLEMENTATION.md).


## Attendre des sous-tâches

Le destinataire peut soumettre des enfants avec `Parent` et `ReturnToSource=false`,
puis appeler `await_children` et terminer son tour sans `report_result`. Le moteur
attend la fin réelle du tour avant de libérer sa capacité et son dossier. Quand les
enfants sont terminés, le parent repasse par la file et ses contrôles de portée,
quota et concurrence, puis reprend avec les résultats. Une tâche bloquée ou un
envoi incertain reste à traiter ; il n'est jamais considéré comme réussi.

## Piloter les départs et les retours

- **Suspendre les départs** conserve les demandes et poursuit le suivi des résultats.
- **Terminer puis arrêter** accepte uniquement les groupes déjà admis et leurs descendants.
- **Arrêter le suivi du relais** arrête le moteur, pas les agents Codex déjà lancés.
- Le projet choisit le retour immédiat, groupé (au plus dix résultats du même chat
  et de la même identité) ou manuel, consultable dans Travaux sans relancer l'agent.
- **Tester le routage** simule la sélection sur le brouillon sans créer de tâche.
  Les décisions et raisons sont conservées lors de la vraie soumission.

La configuration exportée contient des noms, chemins et instructions choisis par
l'utilisateur : la relire avant partage. Les identifiants de connexion et les
sessions de chats n'en font pas partie. Pour demander de l'aide, préférer l'export
expurgé de Diagnostics.
