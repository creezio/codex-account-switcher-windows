# Skills, planifications et objectifs — Windows, Web et API

Cette livraison reprend le parcours OAuth navigateur et la création d’instances
de Switcher Live. Elle ajoute un catalogue administrable des skills et la gestion
des objectifs et planifications d’une discussion. Les panneaux Web et Windows
utilisent les mêmes services locaux ; les requêtes Web sont signées par le serveur
du Site et les API externes de confiance peuvent employer ce même contrat HTTP.

## Plan et critères de livraison

1. Conserver les parcours comptes/instances/plugins et leurs reçus idempotents.
2. Montrer les skills préinstallés, leurs origines et leur état ; créer, éditer,
   importer et déployer une personnalisation sans réécrire un cache fournisseur.
3. Lire et modifier les objectifs dans le stockage natif Codex, sans les simuler.
4. Exécuter les planifications par un ordonnanceur Switcher durable, distinct
   des automatisations internes de Codex et des surveillances en lecture seule.
5. Exposer ces services dans les interfaces et l’API, contrôler les révisions,
   les autorisations et les reprises, puis valider et publier la même livraison.

Les tests automatiques couvrent les changements de compte, les révocations,
les contenus concurrents, les imports hostiles et les réponses réseau perdues.
La recette réelle utilise une instance dédiée ; aucun message n’est envoyé à
l’instance Creezio. Les comptes, instances et historiques existants sont conservés.

## Autorisation de l’API

Préfixe : `/v1/admin/ROUTE_ID`. La route et les instances doivent être autorisées
dans l’administration du Switcher. Une clé Bearer de discussion conserve ses
droits de discussion : elle ne permet pas de gérer les comptes ou les fichiers.

Le contrat signé `Switcher-Admin v1` reste celui de `PortalAdministration` et du
proxy `app/administration-proxy.mjs`. Un client serveur de confiance fournit
les en-têtes `X-Switcher-Admin-Site`, `Route`, `User`, `Email`, `Time`, `Nonce`,
`Body` et `Signature`, avec leur préfixe `X-Switcher-Admin-` complet. `Body` est
le SHA-256 hexadécimal des octets UTF-8 exacts du corps. `Signature` est le
HMAC-SHA256, en hexadécimal, du texte séparé par des retours ligne :

```text
switcher-admin-v1
METHOD
/v1/admin/ROUTE_ID/CHEMIN
SITE_ORIGIN
ROUTE_ID
USER_ID
EMAIL
UNIX_SECONDS
NONCE_HEX_32
BODY_SHA256_HEX
IDEMPOTENCY_KEY_OU_CHAINE_VIDE
```

Le Site, l’email administrateur, la route, l’heure et le nonce sont vérifiés.
Chaque POST demande une `Idempotency-Key`. Après une coupure, conserver le même
corps et la même clé, puis signer une nouvelle enveloppe. Une mutation dont
l’exécution reste incertaine est signalée pour vérification ; aucun second
envoi n’est inventé. La clé d’administration reste sur le serveur de confiance.

## Endpoints

`INSTANCE` vaut `local` ou un identifiant d’instance autorisé. `CHAT` vaut un
GUID natif ou l’identifiant durable du chat créé par le portail.

| Méthode et chemin après le préfixe | Action |
| --- | --- |
| GET `/capabilities` | Comptes autorisés et créations en cours |
| POST `/instance-enrollments` | Nouvelle autorisation ou instance d’un compte enregistré |
| GET `/instance-enrollments/ID` | Progression et identité à confirmer |
| POST `/instance-enrollments/ID/browser-callback` | Retour OAuth officiel |
| POST `/instance-enrollments/ID/confirm` | Confirmer l’identité et préparer l’instance |
| POST `/instance-enrollments/ID/cancel` | Annuler cette préparation |
| GET `/instances/INSTANCE/plugins` | Catalogue réel des plugins |
| POST `/instances/INSTANCE/plugin-installs` | Installer un plugin choisi dans ce catalogue |
| GET `/instances/INSTANCE/plugin-installs/ID` | Reçu d’installation et connexion du service |
| GET `/instances/INSTANCE/skills` | Catalogue des skills avec origine et état |
| GET `/instances/INSTANCE/skills/SKILL_ID` | Document et liste bornée des fichiers |
| POST `/instances/INSTANCE/skills` | Créer, éditer ou personnaliser un skill |
| POST `/instances/INSTANCE/skills/import` | Importer un paquet ZIP |
| POST `/instances/INSTANCE/skills/deploy` | Copier une version d’un skill vers l’instance |
| GET `/instances/INSTANCE/chats/CHAT/management` | Objectif, capacités et planifications |
| POST `/instances/INSTANCE/chats/CHAT/goal` | Définir, modifier ou effacer l’objectif natif |
| POST `/instances/INSTANCE/chats/CHAT/schedules` | Créer ou modifier une planification |
| POST `/instances/INSTANCE/chats/CHAT/schedules/ID/delete` | Désactiver une planification et conserver son reçu |

Les requêtes sont des objets JSON plats avec champs connus, sans doublons.
Les budgets et révisions sont des entiers ; les jours sont une liste de texte.
Les fichiers, comptes, exécutables, permissions et dossiers ne sont jamais choisis
par un chemin transmis depuis le navigateur : le Switcher les résout localement.

## Skills

Voir [SKILLS.md](SKILLS.md) pour les limites et les opérations. Les documents
fournis par Switcher et par les plugins restent consultables. Une personnalisation
est installée dans les skills utilisateur de l’instance, avec sa provenance visible.
Les caches immuables et les fichiers originaux du fournisseur sont conservés.
L’éditeur et le composeur permettent de choisir explicitement cette personnalisation.

Création ou modification :

```json
{"name":"revue-client","document":"---\nname: revue-client\ndescription: Préparer la revue du dossier client demandé.\n---\nVérifier les pièces et présenter les décisions à prendre.\n","expectedRevision":null}
```

Pour éditer, fournir `sourceSkillId` et la `expectedRevision` retournée par le
catalogue. Pour importer : `archiveBase64`, `name` facultatif et `expectedRevision`.
Pour déployer : `sourceInstanceId`, `skillId`, `sourceRevision`, `name` et la
révision attendue du nom sur l’instance cible. Un remplacement concurrent renvoie
409. Les identifiants restent opaques ; aucun chemin local n’est accepté.

## Objectifs natifs

À partir de la beta.3, le pont livré avec Switcher peut joindre le moteur d’une
interface Codex ouverte pour gérer ses objectifs. Il est activé au prochain
lancement de cette interface. Une ancienne interface déjà ouverte demande une
seule relance manuelle ; Switcher ne la ferme pas automatiquement. Les panneaux
Windows, Switcher Live et l’API conservent les mêmes capacités et motifs de refus.

```json
{"action":"set","expectedVersion":"","objective":"Terminer la recette en conservant les comptes existants.","status":"paused","tokenBudget":40000}
```

La chaîne vide signifie qu’aucun objectif n’était présent. Pour modifier,
reprendre `goal.version`. Une nouvelle formulation remplace l’objectif et peut
réinitialiser sa comptabilité native ; modifier seulement son statut ou son budget
conserve le texte. `tokenBudget:null` retire le budget ; omettre le champ le conserve.
L’effacement demande seulement `action:"clear"` et `expectedVersion`.

Le service utilise les RPC natifs `thread/goal/get`, `set` et `clear`, avec l’origine
utilisateur explicite. Il ne reprend pas une discussion et ne démarre pas de tour
pour lire son objectif. Une fenêtre Codex peut rester ouverte lorsque son moteur
propriétaire est reconnu et accessible : les appels passent alors par ce moteur.
Un profil arrêté conserve son accès natif dédié. Aucun moteur concurrent n’est
démarré pour contourner un propriétaire inconnu ou indisponible.
`canReadGoal`, `canWriteGoal` et `goalIssue` dans la réponse restent l’autorité
pour activer les actions et expliquer un refus. Les panneaux réévaluent ces
capacités lors d’une actualisation, conservent les brouillons et affichent le motif
renvoyé. Les autres fonctions du panneau restent disponibles.

## Planifications Switcher

```json
{"name":"Revue du matin","prompt":"Vérifier les éléments nouveaux du dossier.","status":"PAUSED","frequency":"weekly","localTime":"09:00","daysOfWeek":["MO","TU","WE","TH","FR"],"timeZoneId":"Romance Standard Time"}
```

Employer le `timeZoneId` proposé par le PC dans la réponse de gestion. Les modes
sont `interval` avec `intervalMinutes`, `daily` et `weekly`. Pour modifier, ajouter
`id` et `revision` du reçu. `ACTIVE` est une demande explicite d’exécution ;
`PAUSED` n’envoie rien. La suppression est logique et conserve les preuves.

Le PC et Switcher doivent fonctionner. Un worker dédié redémarre depuis les
planifications actives ; son bail empêche deux ordonnanceurs de réclamer la même
occurrence. Chaque occurrence porte un identifiant durable avant son admission.
Le prochain passage reprend ce reçu après une coupure. Les discussions occupées
attendent ; une identité ou une autorisation révoquée met la planification en pause.
Le planning suit le fuseau du PC, y compris ses changements d’heure. Les échéances
manquées sont regroupées au retour du PC, sans rafale de messages historiques.

Les planifications Web empruntent la file et les droits du portail. Les
planifications créées localement utilisent la liaison locale vérifiée et le
pipeline de messages du Switcher. Elles ne créent pas une permission de portail.
Les connexions des plugins restent celles du compte sélectionné ; un service
distant suit sa propre page d’autorisation officielle et son état doit être revérifié.

## Vérification et livraison

Les groupes ciblés sont accessibles avec `scripts/test.ps1 -ManagementOnly`.
Le même runner fonctionne sous .NET Framework et `tests/Core.Net10.csproj`.
Les interactions WPF et la compatibilité des données sont contrôlées par
`scripts/test-desktop.ps1`. Le Site possède ses tests Node et sa vérification
TypeScript. La publication du Site conserve son projet et son audience ; la
livraison Windows suit [APPLICATION-UPDATES.md](APPLICATION-UPDATES.md) et passe
par le lanceur permanent, sans remplacer les exécutables des instances ouvertes.
