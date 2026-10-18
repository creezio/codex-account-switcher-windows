# Déclencher un agent depuis une application externe

## Administration distante des comptes et instances

### Étude et choix du parcours

L’assistant propose la connexion navigateur habituelle de Codex et, en alternative, le code d’appareil officiel. La première utilise un retour `localhost` vers le PC qui héberge Codex. Sur un autre appareil, l’utilisateur colle l’adresse de retour affichée par son navigateur dans l’assistant : le Site transmet cette réponse au seul processus de connexion réservé sur le PC. Il reste nécessaire d’y autoriser le compte humain ; le Switcher ne collecte aucun mot de passe et ne crée pas de compte ou d’abonnement OpenAI.

Le prototype avec le CLI fourni par l’application a confirmé le démarrage, les champs du défi, l’annulation native et la sortie sans compte connecté. Le mode appareil est encore soumis à son activation dans les paramètres de sécurité du compte ou du workspace. Il n’expose pas le callback localhost sur Internet. Références : [authentification](https://learn.chatgpt.com/docs/auth), [App Server](https://learn.chatgpt.com/docs/app-server).

Le Site est la télécommande privée du Switcher local. Les jetons et leur renouvellement restent sur le PC ; ils ne passent ni dans le navigateur ni dans les variables serveur du Site. Ce parcours ne transforme pas le Site en service mutualisé de connexion à des comptes GPT.

### Plan et fonctionnement implémentés

1. Une autorisation d’administration distincte des clés de prompts protège la création de comptes. Le serveur du Site signe la méthode, le chemin, l’acteur ChatGPT fourni par Sites, son origine, la route, la date, un nonce et le contenu exact. Le PC vérifie cette preuve et sa liste locale d’administrateurs avant toute lecture du corps, puis à la finalisation.
2. L’assistant demande le nom puis un compte déjà enregistré ou une nouvelle connexion. Pour un compte connu, le PC recherche en lecture seule ses identifiants les plus récents parmi les instances déjà autorisées, sans remplacer ceux du coffre ni des instances existantes. Si aucun jeton d’accès utilisable ne reste valide plus de deux minutes, le même parcours propose le lien et le code OpenAI ; le compte connecté doit correspondre au compte choisi. L’expiration du jeton d’identité ne déclenche pas à elle seule une reconnexion ; la vérification native du compte reste obligatoire avant l’état prêt. Une inscription possède un identifiant réservé et une clé d’idempotence : une réponse perdue se reprend avec les mêmes identifiants.
3. Pour un nouveau compte, un moteur local isolé fournit le lien officiel correspondant au mode choisi. La connexion habituelle conserve PKCE et l’état générés par Codex ; le relais de retour accepte uniquement son adresse locale et son état exacts. Aucun port OAuth n’est exposé publiquement. Les liens et réponses d’autorisation restent éphémères ; les jetons finaux restent dans le profil local. Le délai de dix minutes est une limite locale du Switcher, pas une durée d’expiration annoncée par OpenAI. Annulation, expiration ou interruption ne relancent jamais une connexion automatiquement.
4. Le PC lit l’identité effectivement connectée. L’utilisateur confirme cet aperçu avant tout ajout au coffre. Un compte existant n’est pas silencieusement remplacé ; un nouveau profil est limité à sa nouvelle instance dès sa première sauvegarde.
5. Le Switcher vérifie que le moteur natif reconnaît le compte de la nouvelle instance, puis prépare le relais et ses skills sans démarrer la fenêtre Codex ni envoyer de prompt. Une connexion inutilisable ne produit pas d’état « prête ». Les nouveaux identifiants restent dans cette nouvelle instance ; ceux des profils et instances existants sont conservés. L’accès depuis ce portail est un choix explicite, limité à cette instance. Aucun compte ajouté ne devient administrateur du Site et aucun partage de ressources existant n’est étendu.
6. En cas d’échec après création, la préparation reprend la même instance. Les écritures du coffre sont sérialisées et une sauvegarde obsolète est refusée avant de remplacer des données. Les anciens chats et applications restent ouverts.

L’ajout d’une instance conserve les autorisations des autres instances et leurs missions déjà en attente. Chaque accès possède sa propre révision ; modifier ou révoquer cet accès invalide ses anciens mandats. Une instance préparée sans fenêtre est consultable dans le portail sans ouvrir l’application Codex. Le passage à une fenêtre ouverte ne lance pas un second moteur en parallèle.

### Configuration

Dans **Paramètres → Administration du Site → Configurer l’administration distante**, choisir l’origine HTTPS du Site, le portail et les adresses administratrices. Le mode est désactivé par défaut. La clé serveur générée est distincte de toute clé webhook et stockée avec DPAPI sur le PC. Elle ne doit jamais être placée dans le code client.

Variables serveur du Site, à appliquer lors de sa publication :

| Variable | Rôle |
| --- | --- |
| `PORTAL_ADMIN_ALLOWED_EMAILS` | Administrateurs parmi les utilisateurs déjà autorisés du Site. |
| `SWITCHER_ADMIN_ROUTE_ID` | Identifiant du portail choisi localement. |
| `SWITCHER_ADMIN_KEY` | Secret de signature serveur, à déclarer comme secret. |

L’accueil peut rester public. Les routes `/api/admin/capabilities` et `/api/admin/instance-enrollments` demandent l’identité ChatGPT fournie par Sites. Les mutations exigent une origine exacte et une clé `Idempotency-Key`. Les sous-routes `/{id}`, `/{id}/confirm` et `/{id}/cancel` sont liées à l’administrateur qui a commencé l’inscription. Les identifiants d’inscription font 32 caractères hexadécimaux ; les identifiants opaques de comptes et empreintes d’identité en font 64. Aucun chemin de fichiers, jeton ou réglage MCP arbitraire n’est accepté dans leur JSON.

Le PC, le moteur webhook du Switcher et son tunnel doivent rester disponibles. Une connexion OpenAI humaine complète demeure nécessaire pour ajouter un compte réellement nouveau. Le mode navigateur refuse de démarrer si les ports de connexion natifs sont déjà utilisés afin de préserver une connexion Codex en cours.

### Installer et connecter un plugin à distance

Dans **Ressources → Ajouter un plugin**, l’administrateur choisit un plugin du catalogue officiel disponible pour le compte de l’instance. Le PC exécute les commandes documentées `codex plugin list --available --json` et `codex plugin add … --json` avec ce profil précis, sans conversation ni appel au modèle. Le navigateur ne peut fournir ni commande libre, ni chemin local, ni identifiants d’un autre compte.

L’installation et l’autorisation d’un service sont deux étapes distinctes. Pour Gmail ou un autre service hébergé, **Connecter dans ChatGPT** ouvre la page officielle fournie pour ce service. L’utilisateur distant se connecte avec le compte ChatGPT indiqué dans l’assistant et y autorise son service. Le retour dans Switcher puis **Vérifier les connexions** relit l’état utilisable dans l’instance locale ; un plugin activé ne suffit pas pour afficher « Disponible ». Les autorisations du service appartiennent au compte ChatGPT et peuvent donc aussi être disponibles dans ses autres instances. Switcher ne copie pas les mots de passe ou jetons du service depuis le navigateur distant.

Le choix est limité aux services effectivement proposés par ce compte. Une intégration Booking doit être présente dans son catalogue et fournir un parcours de connexion distant ; cette interface ne transforme pas un site web quelconque en plugin. Un serveur MCP local avec son propre callback demande un parcours adapté et n’est pas présenté comme une connexion hébergée.

Les routes signées `/api/admin/instances/{instanceId}/plugins` et `/plugin-installs` conservent les mêmes contrôles d’administrateur et d’origine. Chaque installation a un reçu durable et une clé d’idempotence. Une réponse perdue se reprend avec le même reçu ; une mutation incertaine n’est jamais renvoyée automatiquement. Le compte, le dossier, l’autorisation du portail et la source du plugin sont vérifiés de nouveau avant toute installation.

Le moteur réservé à la connexion désactive la découverte de plugins, d’apps et de skills pour éviter les clones de dépôts avant confirmation. Ces options s’appliquent uniquement à ce processus : les futurs agents gardent leur intégration normale. L’annulation nettoie seulement le dossier réservé et les fichiers générés dont la propriété est vérifiée ; un contenu inconnu reste conservé avec un avertissement.

Dans **Webhooks**, chaque entrée possède une adresse, une clé indépendante et une destination configurée localement : instance, compte permanent, dossier de travail et éventuellement chat existant. Une application envoie une mission à cette entrée ; elle ne choisit jamais un autre compte, un autre dossier, des permissions ou une commande système dans le JSON.

## Configuration dans le Switcher

1. Ouvrir **Webhooks → Connexion externe** et activer le service local. Le port proposé est `47831`, accessible uniquement sur `127.0.0.1`.
2. Choisir **Tunnel temporaire** pour un essai, ou **Tunnel nommé** pour une adresse stable. Sélectionner un binaire `cloudflared.exe` déjà installé. Le Switcher détecte les installations locales et le `PATH` ; il ne télécharge ni n'installe silencieusement un service Windows.
3. Créer une entrée, choisir son instance et son dossier, puis préciser si les missions créent un chat ou poursuivent un chat précis. Les permissions choisies sont celles de cette destination ; les confirmations natives éventuelles restent visibles dans Codex/Switcher.
4. Générer la clé de l'entrée et la copier dans le coffre de secrets de l'application externe. Elle n'est affichée qu'à cette étape. Une rotation invalide immédiatement l'ancienne clé.
5. Copier l'adresse de l'entrée. Une nouvelle URL temporaire doit être reportée dans les applications après chaque redémarrage du tunnel.

Le moteur webhook fonctionne dans un processus léger distinct. Fermer la fenêtre du Switcher ne coupe pas un service activé. Désactiver le service arrête son serveur et le seul connecteur Cloudflare dont le Switcher possède le reçu de lancement ; cela ne ferme aucune instance Codex.

### Une adresse stable avec Cloudflare

Créer un tunnel distant dans Cloudflare, puis configurer son nom public, par exemple `hooks.example.com`, vers **`http://127.0.0.1:47831`**. Renseigner dans le Switcher `https://hooks.example.com` et le **jeton du tunnel**, pas un jeton API de compte Cloudflare. Si le port local change, mettre aussi à jour cette origine dans Cloudflare. La création de la zone DNS, du tunnel nommé et de ses règles Cloudflare n'est pas automatisée par cette version.

Le jeton est enregistré avec la protection Windows DPAPI et transmis au processus enfant via `TUNNEL_TOKEN`. Il n'apparaît ni dans la ligne de commande, ni dans un fichier YAML ou un journal en clair. La configuration utilisateur `~/.cloudflared` n'est pas modifiée : le connecteur utilise explicitement un fichier vide dédié. Les journaux du connecteur sont traités en mémoire avec une limite, puis remplacés par des diagnostics fixes ; les lignes brutes ne sont pas enregistrées.

Le tunnel temporaire fournit une URL `https://….trycloudflare.com`. Cloudflare le destine aux essais : son nom change au redémarrage et il ne possède pas de garantie de disponibilité. Pour une application utilisée régulièrement, choisir le tunnel nommé. Voir les [Quick Tunnels officiels](https://developers.cloudflare.com/tunnel/get-started/quick-tunnels/) et les [jetons de tunnel](https://developers.cloudflare.com/tunnel/reference/tunnel-tokens/).

## Envoyer une mission

```http
POST /v1/hooks/mon-agent/jobs HTTP/1.1
Host: hooks.example.com
Authorization: Bearer whk_VOTRE_CLE
Idempotency-Key: commande-2026-001
Content-Type: application/json

{
  "title": "Analyser la demande client",
  "prompt": "Prépare une proposition de correction et indique les fichiers concernés.",
  "context": "Référence métier : DEM-42. Ne publie pas sans validation."
}
```

Exemple JavaScript, exécuté sur un serveur externe possédant la clé :

```js
const response = await fetch(`${process.env.SWITCHER_URL}/v1/hooks/mon-agent/jobs`, {
  method: "POST",
  headers: {
    Authorization: `Bearer ${process.env.SWITCHER_WEBHOOK_KEY}`,
    "Content-Type": "application/json",
    "Idempotency-Key": "commande-2026-001"
  },
  body: JSON.stringify({
    title: "Analyser la demande client",
    prompt: "Prépare une proposition de correction.",
    context: "Référence DEM-42 ; validation requise avant publication."
  })
});
const job = await response.json();
if (!response.ok) throw new Error(job.error);
// job.id identifie le reçu durable ; 202 indique une prise en charge, pas une fin.
```

`prompt` est obligatoire. `title` et `context` sont optionnels. Ces trois champs texte et un objet `options` facultatif sont acceptés, sans doublon. Voir le [contrat complet du composeur](PORTAL-COMPOSER.md) pour les pièces jointes, plugins, skills, modèles, effort et mode Plan. Limites : 120 caractères pour le titre, 20 000 pour le prompt et le contexte, 64 Kio pour le corps UTF-8 complet. Les instructions permanentes, le prompt, le contexte et une réserve de 1 024 caractères doivent ensemble tenir dans l'enveloppe native de 24 000 caractères. La clé d'idempotence doit contenir 1 à 128 caractères ASCII visibles. Ne jamais placer de clé secrète dans l'URL ou dans du code navigateur public.

Le serveur vérifie les en-têtes d'authentification avant d'allouer ou de lire le corps d'une mission. Une clé absente, invalide ou révoquée produit `401` sans attendre l'envoi du JSON. L'admission vérifie les droits une deuxième fois après réception, pour prendre en compte une désactivation ou une rotation pendant le transfert. Les lectures HTTP partagent une limite de temps absolue de dix secondes.

Une répétition de la **même clé et du même contenu** renvoie le même reçu, même après une reconnexion. Réutiliser une clé avec un contenu différent produit `409`. En cas de délai réseau, réessayer avec la clé d'origine.

## Lire le résultat

```http
GET /v1/hooks/mon-agent/jobs/IDENTIFIANT_RENVOYE HTTP/1.1
Host: hooks.example.com
Authorization: Bearer whk_VOTRE_CLE
```

La réponse contient `id`, `title`, `state`, `threadId`, `relayJobId`, `createdUtc`, `updatedUtc`, `error` et, lorsqu'il est disponible, `result`. Une entrée ne peut pas consulter les missions des autres entrées. Les états distinguent notamment :

| État | Signification |
| --- | --- |
| `queued` | Mission admise durablement, pas encore envoyée. |
| `waiting` | Instance ou moteur indisponible ; la demande reste en attente. |
| `sending` | Envoi natif en cours. |
| `monitoring` | Reçu du relais présent ; le travail ou son résultat est suivi. |
| `attention` | L'agent attend une permission, une réponse ou une autre intervention native ; aucun deuxième travail n'est envoyé. |
| `uncertain` | L'envoi a pu arriver, mais aucune preuve ne permet de le confirmer : aucun renvoi automatique. |
| `completed` / `failed` / `blocked` / `cancelled` | Résultat final ou intervention à traiter. |

L'interface **Journal des missions** présente les reçus et les erreurs. Un travail annoncé terminé ne constitue pas une vérification indépendante de sa qualité ou de sa publication. Une instance fermée attend son ouverture en mode interface ; le mode sans fenêtre utilise le moteur local déjà prévu par le Switcher, selon la route choisie.

## Converser depuis un Site privé

Le portail `sites/switcher-live` fournit une interface de conversation : nouveaux chats, réponses dans le même chat, messages d’avancement et activité des outils. Son serveur ajoute la clé d’entrée aux requêtes vers le Switcher ; le navigateur ne reçoit jamais cette clé. Les variables d’hébergement sont `SWITCHER_ORIGIN`, `SWITCHER_ROUTE` et le secret `SWITCHER_WEBHOOK_KEY`. L’accueil peut rester public ; toutes les fonctions sont protégées par la connexion ChatGPT et la liste de comptes autorisés, contrôlées sur le serveur.

Une entrée configurée pour créer de nouveaux chats expose les adresses suivantes avec la même authentification Bearer :

- `GET /v1/hooks/mon-agent/chats` : conversations créées par cette entrée.
- `GET /v1/hooks/mon-agent/chats/IDENTIFIANT_MISSION_INITIALE` : messages et activité.
- `POST /v1/hooks/mon-agent/chats/IDENTIFIANT_MISSION_INITIALE/messages` : réponse, avec un nouvel `Idempotency-Key` et le même corps JSON qu’une mission.
- `GET /v1/hooks/mon-agent/chats/IDENTIFIANT_MISSION_INITIALE/events` avec une négociation WebSocket : instantanés `{"type":"snapshot","data":...}` quand la discussion évolue.

L’identifiant désigne la mission webhook initiale, pas un identifiant arbitraire de chat Codex. Les autres conversations de l’instance restent inaccessibles. Les réponses ciblent le chat confirmé par le reçu natif ; un chat occupé ou dont l’état est inconnu refuse un nouvel envoi. Un renvoi identique retrouve son reçu d’origine, même une fois terminé.

Le flux suit les événements enregistrés par Codex, avec une mise à jour au plus une fois par seconde. Il affiche les messages, les outils et les résumés explicitement exposés ; il ne fournit ni raisonnement privé ni garantie de diffusion mot par mot. Les prévisualisations sont bornées et les identifiants secrets reconnus sont masqués. L’interface signale une troncature. Les permissions natives se traitent dans Codex ou le Switcher.

Le WebSocket utilise l’authentification serveur du Site. Il n’accepte aucun secret en paramètre d’URL, ni aucune commande par trame. Une rotation de clé ou une désactivation coupe le suivi ; l’interface se reconnecte ou annonce explicitement son mode HTTP de secours. Les sockets du récepteur sont marquées non héritables sous Windows, afin qu’un processus enfant ne bloque pas le port après une mise à jour.

## Reconnexion et limites du récepteur

Le Switcher ne prend en charge qu'un connecteur Cloudflare géré pour ce service. Il vérifie PID, heure de démarrage, chemin du binaire et arguments avant tout arrêt. Un connecteur d'une autre application n'est jamais arrêté. Les tentatives de relancement sont espacées progressivement et s'arrêtent après six échecs ; corriger puis enregistrer la configuration relance un cycle limité. Les déconnexions de transport d'un connecteur encore vivant sont gérées par `cloudflared`.

Le serveur accepte au maximum 60 nouvelles missions et 180 lectures par minute et par entrée authentifiée ; la file limite les demandes en attente et refuse un afflux excessif. Les erreurs `401`, `409`, `429` et `503` doivent être traitées par l'application appelante. Après un envoi incertain, consulter le reçu et le chat avant toute nouvelle mission. Aucun « succès » artificiel, aucune approbation native automatique et aucun élargissement de partage ne sont effectués.

Pour le tunnel nommé, des règles Cloudflare Access peuvent fournir une protection supplémentaire. Leur configuration et leurs identifiants d'accès restent distincts de la clé d'entrée du Switcher. Le PC, le service et sa connexion Internet doivent rester disponibles pour recevoir les demandes.


## Permissions, modèle et effort par message

Le champ facultatif `options` permet de sélectionner le modèle et les permissions sur une entrée simple comme sur un portail multi-instance :

```json
{
  "title": "Nouvelle mission",
  "prompt": "Réalise la mission décrite.",
  "options": {
    "model": "gpt-6.1-sol",
    "reasoningEffort": "ultra",
    "permissionProfile": "full-access"
  }
}
```

`permissionProfile` accepte `inherit`, `read-only`, `workspace-write` et `full-access`. Un champ omis ou `inherit` conserve la politique existante. Un choix explicite modifie uniquement le chat ciblé et persiste pour ses prochains tours. Une clé d'entrée est nécessaire et les instances restent limitées aux destinations autorisées du portail. Les confirmations de commande déjà en attente ne sont jamais automatiquement acceptées. Le statut `202` n'atteste pas que la politique a été appliquée : consulter le job et le champ `permission` du chat après le démarrage réel. Voir [le contrat complet du composeur](PORTAL-COMPOSER.md).
