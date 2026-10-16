# Conversations et projets entre instances

## Contrat de la version 0.14

Chaque action a son propre écran et décrit son effet avant exécution :

| Action | Destination | Source |
| --- | --- | --- |
| Copier une conversation | Historique vérifié, nouvel identifiant | Conservée |
| Transférer une conversation | Historique vérifié, nouvel identifiant | Archivée après vérification |
| Ouvrir les fichiers d’un projet | Projet local, sans historique importé | Conservée ; mêmes fichiers |
| Cloner un projet local | Projet et conversations locales actives | Conservée |
| Transférer les conversations d’un projet | Projet et conversations locales actives | Conversations archivées après vérification de l’ensemble |

Un transfert n’est pas un déplacement conservant l’identifiant. L’archivage est réversible depuis Codex. Le conteneur du projet source et les fichiers restent en place. Les conversations cloud, accès Sites/Pages, plugins, secrets de connexion et comptes ne sont pas transférés. Aucun prompt n’est exécuté pendant l’import.

Les projets peuvent réutiliser les mêmes dossiers. Une copie indépendante des fichiers est proposée pour les projets à un seul dossier ; elle exige un nouveau dossier, refuse les liens/jonctions, et compare les fichiers avant et après copie. Les dépendances sont également des fichiers : vérifier l’estimation avant de confirmer. Les anciens chemins cités dans l’historique ne sont pas réécrits.

## Étapes et contrôles

1. Afficher les projets locaux et leurs appartenances explicites ; ne pas déduire le projet d’un chat de son seul dossier.
2. Enregistrer une opération chiffrée avec les comptes, la sélection et des identifiants stables.
3. Vérifier les conversations au repos, leur historique et les chemins.
4. Préparer le projet destinataire par l’API native. Tant que Codex ne l’affiche pas, conserver l’opération en attente et ne pas archiver la source.
5. Copier et vérifier chaque conversation, puis l’associer au projet par l’outil natif.
6. Pour un transfert, revérifier toutes les sources et toutes les destinations avant le premier archivage ; reprendre un archivage interrompu sans refaire les copies.
7. Tester erreurs, reprise, changements de compte, histoire modifiée, chemins et cas réels isolés.

## Limite native constatée

Codex 26.1007 garde un index de projets distinct de l’API `project/import`. Le Switcher enregistre le projet par cette API, puis ajoute uniquement son entrée dans l’index local lorsque l’instance est fermée. Un verrou empêche le lancement concurrent des instances gérées ; les autres réglages sont conservés et un changement concurrent du fichier bloque l’écriture atomique. Aucun cache d’instance en cours n’est modifié.

Le bouton **Actualiser Codex et reprendre** demande confirmation, contrôle tous les chats locaux, ferme seulement la destination, enregistre l’entrée, puis la rouvre. Une erreur tente de rouvrir l’instance fermée. Une instance occupée ou dont l’état est incertain n’est pas arrêtée. Un premier chat doit déjà exister dans chaque instance pour ses outils natifs. La réussite exige la confirmation du projet et des associations dans la barre latérale native.

Les appartenances récentes sont lues dans Codex et les anciennes dans les métadonnées explicites du profil. Si l’appartenance d’anciens chats n’est pas vérifiable, le transfert global est bloqué ; les actions individuelles restent disponibles. Le Switcher ne complète pas silencieusement un projet en devinant à partir des dossiers.

## Historiques modernes

Codex peut déplacer les messages d’affichage du journal JSONL vers SQLite à l’ouverture d’un chat. L’import lit alors les messages par `thread/items/list`, conserve leur ordre et leur regroupement par tour dans un instantané privé, puis vérifie l’historique visible et le contexte modèle séparément. Aucun journal source ni aucune base SQLite n’est modifié directement. Une pièce jointe dont l’affichage ne peut pas être reproduit bloque l’import avant la création de la copie.

## Validation du 12 octobre 2026

- 397 tests du moteur réussis sous .NET Framework 4.8 et 397 sous .NET 10 ; reprise après erreur, changement de compte, historique modifié, archivage différé et copie de fichiers inclus.
- 158 contrôles WPF réussis, avec captures du choix des cinq actions, du résumé et de la fenêtre compacte. Compatibilité des données entre les deux runtimes vérifiée.
- Recette native sur Codex Desktop 26.1007.2314.0 et app-server 0.162.0-alpha.17.2 : transfert d’un projet fictif vers un autre compte, fichiers indépendants, association visible et archivage confirmé du seul chat fictif source.
- Recette inverse : clonage du projet fictif avec dossier commun, conservation de sa source et vérification des messages visibles et du contexte modèle dans la copie.
- Les comptes authentifiés ont été vérifiés séparément par `account/read` et comparés aux comptes permanents des instances. Aucun tour de modèle n’a été lancé pendant ces imports ; ce contrôle ne mesure pas la facturation d’un futur tour.

Les essais réels ont utilisé uniquement des conversations fictives créées pour la recette. L’instance principale et les discussions existantes ont été préservées. Les adaptateurs d’index et d’historique restent dépendants de la version de Codex : un format non reconnu suspend l’opération plutôt que d’annoncer une migration réussie.

## Complément 0.14.1 : historiques volumineux et images

- 411 tests moteur sur chaque runtime et 158 contrôles WPF, avec compilation sans avertissement.
- Clonage autorisé d’un projet existant avec deux discussions et un dossier commun ; environ 538 Mio d’historiques et 24 images locales. Comptes de source et de destination vérifiés séparément ; aucune discussion originale archivée ni fichier de projet dupliqué.
- Le premier journal dépasse l’ancienne limite de 256 Mio. Le second contient cinq blocs de contexte dépassant la limite native de 16 Mio par ligne. Leur import utilise une recompression PNG sans perte dans le seul instantané privé ; les fichiers d’origine restent inchangés.
- Vérification indépendante du contexte : les 1 428 éléments de modèle et six blocs de compactage du second historique sont conservés, avec données raster et métadonnées PNG identiques. Le seul élément ajouté est l’avertissement natif d’un tour interrompu. L’interface native conserve les textes et les images, dans l’ordre.
- L’import initial dont la conversion avait omis du contexte a été remplacé, puis archivé uniquement dans la destination après validation complète. Les originaux et la première copie vérifiée ont été préservés. Les preuves contenant les identifiants réels restent locales.

Les limites de lecture et du convertisseur, ainsi que les refus avant création pour les contenus trop grands qui ne peuvent pas être recompressés sans perte, sont détaillés dans [THREAD-COPY.md](THREAD-COPY.md).

## Complément 0.14.2 : anciens forks et images intégrées

- Clonage réel autorisé d’un projet de trois discussions vers un autre compte, avec mêmes fichiers, associations natives confirmées et aucun original archivé.
- Vérification indépendante des 15 567 éléments de modèle et 56 compactages. Les 100 anciennes annotations d’exécution retirées par Codex correspondent exactement aux appels conservés : identifiant, nom et arguments. Les valeurs vides ajoutées aux compactages sont adaptées dans l’instantané privé. Les journaux originaux restent identiques octet par octet.
- Les images intégrées dans les messages sont conservées et vérifiées. Les pages de dix éléments permettent la lecture des anciennes sorties volumineuses.
- L’affichage partiel d’un ancien fork est reconstruit depuis l’API native, dans le seul instantané privé, puis vérifié après migration sans changement du contexte modèle.
- Une récupération ponctuelle de 31 PNG disparus du dossier temporaire a utilisé les données exactes encore encodées dans les messages modèle. L’encodage des anciens fichiers temporaires n’était plus disponible ; aucune identité binaire avec ces fichiers disparus n’est revendiquée. Ce dépannage ciblé reste distinct du service d’import, qui continue de refuser les images locales introuvables.

Les preuves contenant les identifiants, images et chemins réels sont locales et ne sont pas publiées.
