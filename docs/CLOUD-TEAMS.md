# Équipes Cloud et revue des missions

Un chat orchestrateur conserve le plan et les décisions. Il délègue des missions
bornées aux comptes choisis, puis vérifie leur livraison. Le Switcher transporte
les demandes et les résultats ; il ne choisit pas une stratégie métier et ne
fusionne pas les PR à la place de l’orchestrateur.

## Une mission GitHub, puis une livraison

- Une **issue** décrit le problème, le lot, le contexte et la mission attendue.
- Une **branche** isole les modifications du développeur :
  `codex/<compte>/<feature>/lot-<numéro>`.
- Une **PR** présente la livraison, les tests et le SHA à relire. Elle référence
  l’issue, par exemple avec `Closes #42` lorsque sa fusion doit la terminer.

Ne créez pas de PR vide pour une mission qui n’a pas encore de modifications.
Avant de distribuer le travail, rapprochez les anciennes PR, les preuves et
l’état de la branche de référence pour éviter les doublons.

Une issue autonome contient : objectif, acquis et références du plan, contexte
technique utile, périmètre et exclusions, dépendances, critères d’acceptation,
tests et preuves attendues, limites de coût ou d’appels externes, compte chargé
de la mission, et liens vers le job, la branche et la PR. Les valeurs sensibles
restent dans la configuration de l’environnement, jamais dans l’issue.

Le tableau peut suivre les étapes : **À préciser → Bloquée / Prête → En cours →
Revue → Corrections → Intégrée → Vérifiée en service**. Un retour d’agent et une
notification acceptée ne constituent pas une validation métier.

## Dimensionner l’équipe

Dans **Paramètres → Équipe Cloud**, choisissez le nombre de missions Cloud
simultanées, de 1 à 8. Le réglage initial reste 2 ; une équipe de trois comptes
peut choisir 3. Une seule mission du Switcher travaille à la fois par compte.
Les missions supplémentaires attendent. Réduire la limite laisse terminer les
missions déjà parties et agit sur les prochains départs.

L’orchestrateur répartit les missions selon les dépendances, les fichiers en
commun, les droits, les risques et les quotas disponibles. Ajouter un compte
n’impose pas de paralléliser des modifications incompatibles. Les fenêtres
Codex des développeurs peuvent rester fermées.

## Préparer une file de travail autonome

La file préparée conserve à l’avance des mandats complets. Le moteur du Switcher
peut envoyer un travail prêt lorsqu’un compte se libère, sans réveiller un agent
pour lui demander de refaire le planning. Elle n’invente pas de mission et ne
prend aucune décision de revue, de fusion ou de publication.

L’orchestrateur configure une équipe **pour un dépôt précis et un mandat humain
explicite**, puis prépare plusieurs travaux indépendants et leurs dépendances.
Chaque cible autorisée contient l’instance, son compte permanent attendu et son
environnement. Un autre compte ne remplace pas silencieusement une cible.

Chaque mission fige son texte complet, son lot, sa référence de spécification,
ses fichiers réservés, sa priorité et ses instances éligibles. Les réglages
modèle/effort/permissions sont figés à la préparation, avec les préférences de
l’utilisateur si aucun réglage explicite n’est fourni. L’identifiant de
configuration est préparé ; sa **version prête est vérifiée et figée lors de
l’admission du véritable job Cloud**. Préparation ne signifie donc pas que la
version d’environnement future est déjà vérifiée.

Les dépendances demandent par défaut une **acceptation après revue**. La fin du
tour Cloud ne suffit pas. Après vérification de la PR, du SHA et des preuves,
puis intégration si le projet l’exige, l’orchestrateur accepte le triplet exact
job/chat/tour livré. Le Switcher enregistre cette décision et libère les
successeurs ; il ne vérifie pas à lui seul qu’une fusion GitHub a été faite.
Le mode de dépendance `completed`, plus faible, doit être choisi explicitement
pour une mission pouvant réellement commencer sans cette acceptation.

Les outils MCP sont regroupés en cinq entrées :

| Outil | Fonction |
| --- | --- |
| `configure_cloud_team` | Enregistrer le dépôt, le mandat, les cibles et les limites de l’équipe. |
| `prepare_cloud_mission` | Préparer un mandat complet et immuable, avec fichiers, priorité et dépendances. |
| `list_cloud_queue` | Lire une vue compacte des états, des cibles, des IDs réels et des alertes. |
| `review_cloud_dependency` | Accepter une livraison précise après revue et éventuelle intégration. |
| `control_cloud_queue` | Pause/reprise, annulation avant départ ou acquittement d’une alerte d’inactivité. |

Toutes ces opérations exigent la session privée du **chat orchestrateur réel**.
Une autre conversation du même compte n’en devient pas propriétaire. Les
arguments publics ne permettent de choisir ni une identité source, ni un état
interne, ni une livraison prétendument vérifiée. Les jetons de session ne sont
jamais inclus dans les snapshots ou les registres de la file.

Exemple de configuration JSON pour la CLI, avec des identifiants fictifs :

```json
{
  "operation": "cloud-team-configure",
  "session": "SESSION_PRIVEE_DU_CHAT_APPELANT",
  "team": {
    "id": "11111111111111111111111111111111",
    "name": "Équipe application",
    "repository": "organisation/application",
    "mandate": "Développer les lots autorisés du cahier des charges de cette application, livrer des PR et leurs preuves ; revue et intégration par l’orchestrateur.",
    "explicitDelegation": true,
    "maximumParallel": 2,
    "targets": [{
      "instanceId": "ID_INSTANCE_AUTORISEE",
      "expectedAccountKey": "CLE_COMPTE_PERMANENT",
      "configurationId": "ID_CONFIGURATION_CLOUD"
    }]
  }
}
```

Préparer ensuite chaque travail avec `operation="cloud-queue-prepare"` et
`mission={id,teamId,title,prompt,repository,feature,lot,specification,
explicitDelegation,priority,reservedFiles,eligibleInstanceIds,dependencies}`.
`prompt` contient le contexte, l’objectif, les exclusions, les critères, les
tests et la livraison attendue. `dependencies` contient des objets
`{missionId,mode:"accepted"}` ; l’absence de `mode` conserve ce défaut.
`model`, `reasoningEffort` et `permissionProfile` peuvent être omis uniquement
si les préférences Cloud complètent les valeurs manquantes. Les trois réglages
doivent être déterminés avant la préparation ; sinon elle est refusée sans
envoi. Les valeurs sensibles restent dans la configuration protégée, pas dans
le texte du mandat.

Les autres commandes sont :

- `cloud-queue-list`, avec `session`, pour le snapshot compact sans prompts ;
- `cloud-queue-review`, avec `session`, `id`, `jobId`, `threadId`, `turnId`, après
  la décision de revue réelle : `id` désigne toujours la mission préparée
  d'origine, tandis que le triplet `jobId`/`threadId`/`turnId` désigne le
  **dernier** cycle de sa filiation, y compris après une correction. Le résultat
  d'un parent devenu obsolète ne peut pas être accepté ;
- `cloud-queue-control`, avec `session`, `action` et **soit** `teamId` **soit**
  `id` : actions `pause`/`resume`, ou `cancel` pour une mission non partie ;
- `cloud-queue-control` avec `action="reject"`, `id`, `jobId`, `threadId` et
  `turnId`, après revue d’un échec ou d’une annulation native terminale, ou d'une
  intervention requise **avant toute création de chat, de tour et de chat de
  préparation native** : libère ses fichiers sans accepter la mission ni
  satisfaire ses dépendances. Dans ce dernier cas, le Switcher annule d'abord
  explicitement le job et vérifie l'état durable `cancelled` avant de libérer
  les fichiers. Comme pour la revue, `id` reste celui de la mission préparée
  d'origine et le triplet observé désigne son dernier cycle ;
- `cloud-queue-control` avec `action="ack-idle"`, `teamId` et l’`episodeId`
  exact de l’alerte courante pour acquitter cet épisode.

Une pause ou une annulation de préparation ne prétend jamais interrompre un
tour Cloud déjà en cours. Après un résultat incertain, conserver le même ID et
inspecter les états ; ne pas créer un doublon. Une modification du mandat figé
demande une annulation avant départ et un nouveau travail autorisé.

Si une admission déjà attribuée est bloquée par `team-binding-changed` avant
qu'un job natif soit observé, rétablir sa cible et son chat source d'origine,
puis demander `cloud-queue-control` avec `action="resume"` et l'`id` de la mission.
Cette reprise revalide la liaison d'origine et conserve la requête, le compte
et l'environnement figés. Elle n'affecte pas la mission à une autre cible. Si
le job natif existe déjà, son état est rapproché sans retransmettre le mandat.

Une mission échouée conserve ses réservations jusqu’à cette décision de rejet.
Le triplet job/chat/tour doit correspondre exactement au résultat observé.
Lorsque le chat ou le tour n’a jamais été créé, transmettre explicitement
`null` pour cet identifiant ; ne pas l’inventer ni omettre le champ. Les états
actifs, incertains et les interventions associées à un chat ou à une préparation
native restent bloqués : le rejet ne prétend pas arrêter une exécution dont
l’état n’est pas établi. L'exception avant toute création décrite ci-dessus
exige une décision explicite et une annulation confirmée ; elle n'est jamais
déclenchée par le suivi automatique.

Le cycle recommandé est : réserver les fichiers et dépendances, remplir une
file prête suffisante, terminer le tour local, recevoir les résultats, revoir
et intégrer, accepter la livraison exacte, puis compléter le backlog. Ne pas
maintenir une boucle de prompts, de polling ou de `sleep`. Une alerte
d’inactivité signale un manque de travail prêt ou un blocage ; elle ne donne
pas au moteur le droit d’inventer du travail hors mandat.

Le planificateur examine la file localement toutes les cinq secondes ; les
contrôles Cloud suivent leur échéance de soixante secondes. Préparer et vérifier
le premier envoi dispose d’un budget local de 120 secondes, contre 45 secondes
pour les contrôles d’un tour déjà identifié. Ces budgets ne limitent pas la
durée du travail distant. Après un envoi non confirmé, le diagnostic conserve
la phase, la catégorie d’erreur et le code HTTP disponible, sans recopier les
messages privés ; aucun nouveau prompt n’est envoyé automatiquement.

## Demander une correction dans la même PR

Après lecture du résultat et vérification de GitHub, l’orchestrateur appelle
`follow_up_cloud_job` avec :

- `parentId` : identifiant du dernier cycle terminé de cette mission ;
- `id` : nouvel identifiant de demande, à réutiliser si sa réception est incertaine ;
- `title` et `prompt` : observations de revue, issue/PR, correction attendue,
  périmètre et tests ;
- `explicitDelegation=true` : uniquement si l’utilisateur a autorisé ce suivi
  ou la boucle de revue concernée ;
- `session` : connexion du chat orchestrateur réel, jamais celle d’un autre chat.

Le nouveau cycle conserve le compte, la conversation, l’environnement, les
dépôts, la branche et les réglages du parent. Il vérifie l’identité, le tour
précédent, l’état inactif et les permissions natives avant tout nouveau prompt.
Il ne recrée pas un chat ni une branche et ne change pas les permissions d’une
conversation utilisée. Un blocage reste visible au lieu d’être contourné.

Chaque cycle possède son résultat, son journal et sa notification. Dans
**Délégations**, le badge de correction et le lien vers la mission précédente
permettent de retrouver la revue. Pour une correction suivante, utilisez le
dernier cycle terminé comme parent. Aucun nouvel envoi aveugle n’est effectué
lorsque l’issue du précédent est incertaine.

L’équivalent CLI utilise `operation="cloud-job-follow-up"`, une `session` du
chat courant et `followUp={Id, ParentId, Title, Prompt, ExplicitDelegation}`.
Les commandes passent sur l’entrée JSON de `CreezioRelay.exe` de la livraison
complète. Le moteur de suivi doit être à jour ; les versions incapables de
reprendre le même chat sont refusées avant admission.

Un chat déjà ouvert peut conserver l’ancien catalogue MCP jusqu’au rechargement
de son intégration. Si `follow_up_cloud_job` n’y apparaît pas encore, utilisez
la commande JSON ci-dessus avec le `CreezioRelay.exe` de la version 0.21 ou
ultérieure. Exécutez d’abord le script `scripts/bind-chat.ps1` du plugin installé
dans le chat orchestrateur et gardez sa session privée. Cette commande poursuit
la mission existante ; elle ne justifie pas d’emprunter la session d’un autre
chat ni de relancer la mission dans une nouvelle conversation.

La relecture et la fusion restent des actions GitHub de l’orchestrateur,
autorisées par le mandat et les règles du projet.
