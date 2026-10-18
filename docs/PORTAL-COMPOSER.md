# Le composeur et son API webhook

Le Site Switcher Live et les applications externes utilisent le même modèle de message. Le Site conserve sa connexion ChatGPT et sa liste de comptes autorisés. Une application serveur utilise la clé Bearer de son entrée Switcher : ne mettez jamais cette clé dans une application Web publique.

## Choisir une destination et ses capacités

Pour une entrée configurée avec un portail multi-instance :

```http
GET /v1/hooks/mon-portail/instances
Authorization: Bearer whk_VOTRE_CLE
```

Seules les instances explicitement autorisées sont retournées. Leurs identifiants restent stables lors d'un renommage. `local` identifie la session principale lorsque celle-ci a été partagée.

```http
GET /v1/hooks/mon-portail/instances/INSTANCE_ID/capabilities
Authorization: Bearer whk_VOTRE_CLE
```

Cette réponse fournit les plugins, skills et modèles réellement découverts sur le compte concerné. Utilisez leurs `id`, pas leur nom d'affichage. `models[].reasoningEfforts` fournit les efforts proposés pour chaque modèle. Les indicateurs `supportsPlanMode` et `plugins[].invokeSupported` signalent les options prises en charge par la connexion actuelle. Un catalogue de modèles ne garantit pas le quota disponible : seule l'exécution du tour le vérifie.

Pour retrouver les chats, utiliser `GET .../instances/INSTANCE_ID/chats`. La liste récente est bornée. Un chat natif est désigné par son GUID ; un nouveau chat peut d'abord être désigné par l'identifiant durable retourné lors de sa création. Ces deux formes rejoignent ensuite la même discussion.

## Joindre des fichiers ou les fichiers d'un dossier

Uploader chaque fichier avant d'envoyer le message :

```http
POST /v1/hooks/mon-portail/instances/INSTANCE_ID/attachments
Authorization: Bearer whk_VOTRE_CLE
Idempotency-Key: upload-bon-commande-001
Content-Type: application/json

{
  "name": "brief.txt",
  "relativePath": "dossier-client/brief.txt",
  "mimeType": "text/plain",
  "contentBase64": "Qm9uam91cg=="
}
```

La réponse `201` contient `attachment: {id, name, relativePath, size, mimeType}` et `replayed`. `relativePath` est facultatif ; il conserve l'organisation du dossier sans donner accès au système de fichiers du navigateur. Les chemins absolus, traversées `..`, liens Windows et noms réservés sont refusés. Pour une entrée simple qui cible une seule instance, utiliser `POST /v1/hooks/mon-agent/attachments`.

Limites : **8 Mio par fichier, 10 fichiers et 24 Mio par message**. Les uploads seuls acceptent un JSON de 12 Mio ; les messages restent limités à 64 Kio. Les images PNG, JPEG, GIF et WebP sont contrôlées. Le stockage possède un quota de 1 Gio, dont 256 Mio au maximum de fichiers non envoyés par entrée, avec une réserve de 20 Gio d'espace libre sur le PC.

Réutiliser exactement la même clé et le même fichier après un délai réseau. Changer le contenu sous la même clé renvoie `409`. Un upload appartient à une seule entrée, une seule instance et son compte permanent ; son identifiant n'est pas transférable vers une autre instance.

Le récepteur local exige un `Content-Length` connu et n'accepte ni le transfert `chunked` ni `Expect: 100-continue`. Avec un ancien client .NET Framework, définir `request.Headers.ExpectContinue = false`. Pour les corps Base64 volumineux avec curl, ajouter `-H "Expect:"`. Les appels navigateur `fetch` du Site sont gérés automatiquement par sa passerelle serveur.

Les copies **non envoyées** expirent après 24 heures. Le moteur du Switcher les vérifie au démarrage puis toutes les cinq minutes, par lots bornés. Les fichiers référencés par un message sont conservés pour son historique. Les originaux sur votre ordinateur ne sont jamais effacés. Une ancienne clé d'upload expirée renvoie `410` ; elle ne crée pas une nouvelle copie silencieusement.

## Envoyer toutes les options ensemble

Créer un chat : `POST .../instances/INSTANCE_ID/chats`.

Poursuivre un chat : `POST .../instances/INSTANCE_ID/chats/CHAT_ID/messages`.

```json
{
  "title": "Analyser le dossier client",
  "prompt": "Analyse le brief joint et prépare un plan de travail.",
  "context": "Référence client : DEM-42.",
  "options": {
    "model": "gpt-6.1-sol",
    "reasoningEffort": "ultra",
    "permissionProfile": "full-access",
    "collaborationMode": "plan",
    "plugins": ["ID_RETOURNE_PAR_CAPABILITIES"],
    "skills": ["IDENTIFIANT_OPAQUE_DU_SKILL"],
    "attachments": ["IDENTIFIANT_OPAQUE_DU_FICHIER"]
  }
}
```

L'exemple contient des identifiants à remplacer par ceux des réponses de découverte et d'upload. Tous les champs d'`options` sont facultatifs. `collaborationMode` accepte `default` ou `plan` ; omettre le champ conserve le réglage du chat. Les listes de plugins et skills sont chacune limitées à 20 éléments distincts.

Une entrée simple accepte aussi `options` dans `POST /v1/hooks/mon-agent/jobs` et dans ses réponses de chat. Elle reste attachée à la destination configurée localement.

Chaque envoi nécessite une **nouvelle** `Idempotency-Key`. En cas d'incertitude réseau, réutiliser cette clé avec le **même** prompt, les mêmes options et les mêmes identifiants de fichiers. Les options font partie de l'empreinte durable du message : changer de modèle, de permissions ou de fichier sous la même clé est refusé. `202` confirme l'admission, pas l'exécution réussie. Un sélecteur devenu indisponible bloque explicitement la mission avant son prompt métier ; il n'est pas ignoré.

`options.permissionProfile` choisit explicitement les permissions natives de **ce chat** :

| Valeur | Effet |
| --- | --- |
| Champ omis, `null` ou `inherit` | Conserver les permissions actuelles ; aucune modification implicite. |
| `read-only` | Lecture seule, demandes d'approbation natives si nécessaire. |
| `workspace-write` | Écriture dans le dossier natif du chat, demandes d'approbation natives si nécessaire. |
| `full-access` | Accès complet : bac à sable désactivé et politique d'approbation `never`. |

Le Site propose **GPT-6.1 Sol · Ultra · Accès complet** pour les nouveaux chats. Pour un chat existant, le sélecteur propose de conserver ses réglages. L'API ne change pas silencieusement les anciens clients : préciser les trois champs pour demander ce préréglage. Un changement explicite de permissions est persistant sur le chat concerné, comme dans Codex ; il ne modifie pas les autres chats ni les paramètres globaux de l'instance. Le Switcher vérifie le reçu natif et les permissions effectivement enregistrées ; une erreur de contrat ou une restriction native reste visible.

Les comptes, dossiers de travail, connexions OAuth et autorisations de publication restent ceux de la destination. Un profil ne donne pas accès aux ressources d'un autre compte et n'installe aucun plugin. Une mention sélectionne un plugin déjà disponible ; elle ne désactive pas les autres outils du compte. Les demandes d'approbation déjà ouvertes ne sont pas acceptées automatiquement par le Switcher.

Un nouveau chat local sans projet reçoit le dossier natif créé par Codex. Le dossier métier configuré sur l'entrée reste le périmètre de la mission ; ce n'est pas une promesse de création du chat dans ce dossier. Le Switcher fige le dossier natif confirmé et refuse un changement ultérieur pendant son envoi enrichi.

## Suivre le même chat

Consulter `GET /v1/hooks/mon-portail/jobs/JOB_ID` pour le reçu, ses options et son état. Pour le transcript, utiliser `GET .../instances/INSTANCE_ID/chats/CHAT_ID`. Les métadonnées des pièces jointes et les sélecteurs sont conservés dans l'historique sans exposer de clé de connexion ou de chemin de stockage interne.

Le WebSocket `.../instances/INSTANCE_ID/chats/CHAT_ID/events` utilise le même Bearer dans l'en-tête d'ouverture. Il émet des instantanés de messages et d'activité publics, au maximum une fois par seconde. Les applications qui ne peuvent pas fournir cet en-tête doivent passer par leur propre serveur, comme le Site ; aucune clé n'est acceptée en paramètre d'URL. Le raisonnement privé n'est pas exposé.

Un chat occupé, une collision d'identité entre profils ou une demande de permission native entraîne un état explicite. Aucun deuxième envoi n'est effectué pour masquer un état incertain. Consulter le reçu avant toute reprise.

## Ce qui reste spécifique à Codex

Le choix d'un navigateur ouvert et de son onglet, les objectifs natifs et les actions internes non exposées ne sont pas simulés par le composeur. Les interfaces MCP compatibles conservent la politique séparée de lecture seule du portail. Les confirmations natives restent à traiter dans Codex ou le Switcher.

L'envoi enrichi vers une fenêtre utilise le protocole IPC interne versionné de Codex. Il vérifie le compte, le profil et le processus de l'instance, l'unicité du chat entre les profils connus et son propriétaire natif. Un chat importé sous le même GUID dans deux profils est refusé. Ce protocole privé peut changer avec une mise à jour de Codex : le Switcher bloque alors explicitement l'envoi au lieu de supprimer les pièces jointes, d'envoyer au mauvais compte ou d'appliquer un faux mode Plan. Le mode sans fenêtre utilise les inputs structurés de l'app-server.

Le PC, le Switcher et son tunnel doivent rester disponibles. Les chats natifs en mode interface exigent que l'instance soit ouverte. Le mode sans fenêtre exige son activation préalable sur l'instance et la route.
