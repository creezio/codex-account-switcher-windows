# Accès complet des ressources entre instances

Dans la navigation principale de Switcher, ouvrir **Accès global** et cocher
**Accès complet global**. Le choix est enregistré immédiatement pour toute
l’application. Il reste accessible pendant l’inventaire d’un compte, sans
ouvrir ni connecter ce compte. Les anciens boutons des ressources ouvrent ce
même écran global.

Ce mode est désactivé par défaut. Une fois activé, toutes les instances peuvent
utiliser les ressources et connecteurs des autres instances en lecture et en
écriture, y compris la publication et la suppression lorsqu'elles sont proposées
par le service natif. Les instances, plugins et ressources ajoutés ensuite sont
inclus automatiquement. Aucune liste de ressources sélectionnées ni permission
de lecture seule de Switcher ne réduit ces accès globaux. Les partages
personnalisés sont ignorés, y compris les anciens identifiants de partages
désactivés ou limités qui sont encore utilisés par un client. Les interfaces de
sélection du destinataire, des ressources et des droits sont masquées pendant
ce mode : aucune configuration de partage n’est nécessaire.

Les partages personnalisés restent enregistrés séparément. Désactiver le mode
global les remet immédiatement en application. Changer ce réglage ne lance
aucune tâche et n'envoie aucun message. La découverte des instances est locale ;
seule la consultation d'un propriétaire choisi ouvre son inventaire d'outils.

Les connexions et autorisations propres aux services restent nécessaires. Le
transport partage leurs ressources ; il ne transfère pas les secrets de connexion,
le bureau Windows ni les interpréteurs d'une autre instance.

## API et webhook d'administration

Le même endpoint HTTP est disponible sur le serveur webhook de Switcher :

```text
GET  /v1/admin/ROUTE_ID/resource-access
POST /v1/admin/ROUTE_ID/resource-access
```

Il utilise l'enveloppe d'administration signée documentée dans
[PORTAL-MANAGEMENT.md](PORTAL-MANAGEMENT.md). Les clés ordinaires de chat et de
webhook ne donnent pas ce droit. Le réglage est global au PC, même si la requête
est authentifiée au moyen d'une route d'administration.

Lire l'état courant, puis transmettre sa `revision` exacte et une nouvelle
`Idempotency-Key` :

```json
{"enabled":true,"expectedRevision":"REVISION_LUE_PAR_GET"}
```

Envoyer `false` pour revenir aux partages personnalisés. Une modification
concurrente produit un conflit HTTP 409. Le renvoi de la même clé et du même
contenu retourne le reçu initial sans réappliquer le choix : il ne réactive donc
pas un mode désactivé entre-temps. Pour connaître l'état actuel, utiliser `GET`.
Si un changement a été interrompu à un moment incertain, son ancienne intention
n'est pas rejouée : relire l'état avant de soumettre une nouvelle demande.

Le résultat contient `enabled`, `revision`, `updatedUtc`, `scope: "all-instances"`,
`access: "read-write"`, `includesFutureInstances` et `includesFutureResources`.
Les capacités d'administration exposent `canManageResourceAccess: true`.
