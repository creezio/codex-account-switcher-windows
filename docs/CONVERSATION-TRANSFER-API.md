# Copier et transférer une discussion par plugin, API ou webhook

Les trois accès utilisent le moteur de copie et de transfert du logiciel Windows.
Ils ne délèguent aucune mission et n'envoient aucun prompt. Une copie conserve
l'original ; un transfert l'archive après vérification de la copie et relecture
des deux historiques. La destination reçoit un nouvel identifiant de discussion.

## Plugin Switcher

Après connexion de la conversation appelante au relais :

1. `list_instances`, puis `list_instance_conversations(instance, query?, cursor?)`.
2. `copy_conversation` ou `transfer_conversation`, avec `session`, `id` (32 hex
   minuscules), `source`, `destination`, `threadId`, `title`, `explicitRequest=true`.
3. Conserver l'`id` renvoyé : c'est l'identifiant durable à passer à
   `get_conversation_transfer` et `resume_conversation_transfer`. Il est distinct
   de la clé de soumission et appartient à cette conversation appelante.

`queued` et `running` ne signifient pas réussite. Attendre `completed`, vérifier
`targetThreadId`, et pour un transfert `sourceArchived=true`. Après une réponse
de soumission perdue, réutiliser la même clé et la même sélection. Après examen
d'un blocage, reprendre l'opération enregistrée plutôt que créer une autre copie.

## API et webhooks d'administration

Base : `/v1/admin/ROUTE_ID/conversation-transfers`. Utiliser l'enveloppe
**Switcher-Admin v1** existante, avec origine, utilisateur administrateur autorisé,
empreinte du corps, horodatage, nonce et signature. Tout POST exige
`Idempotency-Key`. Une nouvelle requête de reprise/réessai doit avoir une nouvelle
signature et un nouveau nonce, en conservant sa clé d'idempotence et son contenu.
Les jetons ordinaires de chat/webhook ne donnent pas ces droits d'administration.
Voir [la gestion d'administration](PORTAL-MANAGEMENT.md) et [les webhooks](WEBHOOKS.md).

| Méthode | Chemin relatif | Corps / résultat |
| --- | --- | --- |
| POST | `/list` | `{ "instance": "INSTANCE_ID", "query": "titre", "cursor": "curseur optionnel" }` ; liste paginée sans historique privé |
| POST | base | corps ci-dessous ; `202`, opération durable |
| GET | `/OPERATION_ID` | état et identifiant de destination, sans lancer ni reprendre de traitement |
| POST | `/OPERATION_ID/resume` | `{}` ; `202`, reprise de la même opération |

Corps de soumission (4 Kio maximum, aucun champ inconnu ou doublonné) :

```json
{
  "kind": "copy_thread",
  "source": "INSTANCE_SOURCE_ID",
  "destination": "INSTANCE_DESTINATION_ID",
  "threadId": "ID_DE_LA_DISCUSSION_SOURCE",
  "title": "Copie pour la relecture",
  "explicitRequest": true
}
```

Pour transférer, utiliser `"kind": "transfer_thread"`. Un serveur ou un
émetteur de webhook appelle exactement ce même endpoint signé. L'administrateur
doit avoir accès aux deux instances dans ce portail ; l'accès aux conversations
existantes doit être activé. Aucun compte ou droit n'est ajouté par l'appel.

Le traitement continue localement après la réponse `202`. Un arrêt du processus
est affiché comme `interrupted` et ne déclenche pas de reprise automatique.
`blocked` conserve les reçus natifs et le problème à résoudre. Le suivi et la
reprise restent limités à l'administrateur qui a soumis l'opération. Une clé
réutilisée avec un autre titre, une autre discussion, un autre mode ou un compte
réaffecté est refusée. La révocation d'accès bloque les étapes suivantes.

## Conditions natives

La discussion source doit être au repos et les instances natives disponibles.
L'outil ne ferme ni ne lance de fenêtre, n'interrompt pas un tour en cours et
ne poursuit pas automatiquement un objectif importé. Le logiciel doit fonctionner
sur le PC ou serveur qui héberge ces instances. Les fichiers, conversations
cloud, accès aux plugins et ressources ne sont pas transférés avec l'historique.
Les formats et vérifications sont ceux de [la copie native](THREAD-COPY.md)
et du [moteur de transferts](TRANSFERS.md).
