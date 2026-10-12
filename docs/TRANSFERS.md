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
