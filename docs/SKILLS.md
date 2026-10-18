# Skills par instance

Le catalogue local inspecte les fichiers sans lancer Codex ni de modele. Il inclut `CODEX_HOME/skills`, ses skills `.system`, les skills partages `~/.agents/skills`, les skills des caches de plugins et les quatre skills livres avec Creezio Relay. Les sources inaccessibles ou malformees restent signalees. Une copie de cache sans preuve de configuration ne devient pas une installation active.

Les fichiers des plugins, skills systeme et skills partages sont des sources protegees. Une personnalisation cree une copie dans `CODEX_HOME/skills/<name>` pour l'instance choisie. Son origine et l'identifiant du skill personnalise restent visibles. Cette copie peut etre choisie explicitement dans le compositeur ; le cache original demeure disponible.

## Administration HTTP

Les memes operations sont disponibles a l'interface desktop et a l'API d'administration signee :

- `GET /v1/admin/{route}/instances/{instance}/skills` : catalogue.
- `GET /v1/admin/{route}/instances/{instance}/skills/{skillId}` : document et metadonnees des fichiers.
- `POST /v1/admin/{route}/instances/{instance}/skills` : `{name, document, expectedRevision?, sourceSkillId?}`.
- `POST /v1/admin/{route}/instances/{instance}/skills/import` : `{archiveBase64, name?, expectedRevision?}`.
- `POST /v1/admin/{route}/instances/{instance}/skills/deploy` : `{sourceInstanceId, skillId, sourceRevision, name?, expectedRevision?}`.

`instance` accepte `local` ou un identifiant d'instance isolee. Les identifiants de skills sont opaques et propres a leur chemin ; aucun chemin fourni par le client n'est accepte. La lecture comme l'ecriture distante exigent l'administration signee et l'autorisation de l'instance. Un jeton de discussion ne donne pas de droit de modification. Un deploiement verifie les autorisations et identites des deux instances.

Chaque POST demande `Idempotency-Key`. Pour une creation, omettre `expectedRevision` ou envoyer une chaine vide. Pour une edition, fournir `sourceSkillId` et la revision du skill lu. Pour personnaliser une source protegee, fournir les memes champs, puis un document dont le nom correspond au nouveau `name`. Une personnalisation ne remplace jamais un skill utilisateur deja present ; l'editer explicitement apres actualisation. Un remplacement par import/deploiement exige la revision actuelle du skill utilisateur cible.

Les revisions SHA-256 couvrent tous les fichiers du paquet. Les recus ne stockent aucun document ni archive ; ils epinglent l'operation, ses identites et ses empreintes. Une preparation valide peut reprendre avec la meme cle apres interruption. Un paquet partiellement ecrit avant validation reste refuse et conserve pour verification locale. Les fichiers temporaires verifies sont retires apres publication.

## Format et limites

Un paquet contient `SKILL.md` a sa racine, eventuellement dans un unique dossier exterieur pour l'import ZIP. Le frontmatter doit contenir un `name` de 1 a 64 caracteres en minuscules, chiffres et tirets simples, et une `description` de 1 a 1024 caracteres. Les valeurs YAML simples ou citees et les descriptions multilignes `|`/`>` sont prises en charge. Les metadonnees optionnelles sont preservees ; aucun script du paquet n'est execute pendant une operation.

Limites : creation/edition de document UTF-8 64 Kio, lecture d'un document installe 256 Kio, archive ZIP 2 Mio, contenu decompresse 8 Mio, 128 fichiers, 1 Mio par fichier. Les chemins absolus, traversals, ADS Windows, noms reserves, collisions de casse, liens/jonctions et fichiers d'authentification/configuration sensibles sont refuses. Un skill installe depassant les limites d'import reste visible et son document peut etre consulte dans la limite de lecture ; cela n'autorise pas sa copie complete. Pour personnaliser un long document natif, preparer une copie dont les instructions respectent la limite d'ecriture de 64 Kio.

Un succes retourne `state: "deployed"` et `refreshRequired: true`. Les fichiers sont presents dans l'instance ; une nouvelle conversation charge leur nouvelle version. Les conversations deja ouvertes sont conservees.
