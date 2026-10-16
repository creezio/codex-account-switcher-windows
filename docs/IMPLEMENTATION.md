> Version 0.6 : consulter [la qualification produit](PRODUCT-VALIDATION.md).
> Le document ci-dessous conserve les preuves de la version 0.5.

# Relais configurable : livraison et qualification

État au 3 octobre 2026, v0.5.0-beta.1. Ce document complète l'[audit initial](EVOLUTION-PLAN.md) et décrit ce qui existe réellement. Les comptes, ressources et règles de la recette ne sont pas inclus dans l'application distribuée.

## Réalisation

Le produit conserve C# / WinForms / .NET Framework 4.8, sans téléchargement de SDK ni dépendances NuGet. Le binaire console héberge le CLI, le moteur indépendant et le serveur MCP stdio. Un plugin local regroupe les deux skills et la connexion MCP. L'installation utilise les commandes officielles de marketplace du CLI ; le transport vers les chats reste l'interface locale du bureau Codex.

| Limite de l'audit | Réponse dans cette version |
|---|---|
| Commandes JSON manuelles | Deux skills et dix outils MCP. Une liaison initiale reste nécessaire dans chaque chat pour prouver son origine. |
| Mandat spécifique à Sites | Enveloppe générique ; rôles, instructions, types de tâches et ressources définis par l'utilisateur. |
| Connexion manuelle | Assistant de canal, diagnostic et session liée au chat réel. Aucun identifiant de chat inventé par le modèle. |
| Redémarrage d'instance | Reconnexion des profils gérés, avec vérification du compte, du profil, du PID et de son heure de démarrage. |
| Moteur attaché à la fenêtre | Processus séparé, verrou d'unicité, état persistant et arrêt manuel durable. |
| Source fermée | Origine vérifiée à l'admission ; exécution et remise différée indépendantes. |
| Routage manuel | Agents, projets, règles et ressources configurables. Inventaire installé/activé via CLI ; capacités explicitement déclarées. |
| Quota confondu avec autorisation | Ressource limitée à ses canaux ; quota récent pris en compte ensuite. Le propriétaire indisponible reste destinataire en attente. |
| Permissions mal héritées | Préparation sans outil, contrôle du contexte effectif avant le mandat, état d'approbation visible, réutilisation optionnelle de chats terminés. Lecture corrigée autour de minuit/local/UTC. |
| Projet du chat | Identifiant de projet Codex accepté seulement après vérification. Sans projet enregistré, chat sans projet et dossier explicitement transmis. Ce dernier cas a été testé réellement. |
| Verrou global pendant IPC | Appels hors du verrou global ; bail par message. |
| File séquentielle limitée à 30 | Tâches asynchrones bornées, ordre par dernier contrôle, pas de plafond fixe de 30 ; réconciliation espacée. |
| Relectures de tout l'historique | Cache de métadonnées invalidé par fichier, listes paginées dans l'interface et le MCP. Pas de nouvelle base de données. |
| Réponse limitée aux derniers tours | Curseurs de lecture, résultat structuré jusqu'à 250 000 caractères, pages de lecture et excerpt pour retour. |
| Texte final assimilé à succès | Transport, résultat déclaré et preuve externe distincts ; texte non structuré marqué `unverified`. |
| Échecs non retournés | Échecs et annulations observés suivent la file de retour ; retour déjà livré non répété. |
| Envoi incertain | Intention persistée, identifiants/marqueurs, recherche dans Codex avant toute reprise ; jamais de renvoi aveugle. |
| Annulation active | Annulation de file ; demande d'interruption consignée. Arrêt natif ciblé indisponible dans les outils examinés : bouton Arrêter dans Codex nécessaire. |
| Auteurs concurrents | Verrous coopératifs par dossier et ressource, SHA256 avant envoi. Espaces distincts configurables ; aucune création/fusion automatique de worktrees. |
| Parents et dépendances | Parent lié au vrai destinataire, dépendances du même chat/projet, plafonds de profondeur, de tâches, de concurrence et délai de démarrage. Agrégation par l'agent source. |
| Même utilisateur Windows | Contrôles de périmètre, DPAPI et jetons de liaison limités. Ce n'est pas une isolation système entre processus du même utilisateur. |
| Payloads et longues réponses | Extraits, pagination, références et empreintes ; aucun transfert de connexions privées. |
| API bureau interne | Vérification des outils nécessaires, rejet des contextes inconnus, versions explicites et tests locaux. La stabilité future de cette API n'est pas garantie. |

Le SDK MCP n'a pas été ajouté : un serveur stdio minimal couvre les méthodes nécessaires sans changer la chaîne de compilation. Son initialisation et ses outils sont chargés par le vrai Codex CLI. Cette qualification porte sur les versions indiquées ci-dessous, pas sur tous les clients MCP.

La file v0.5 utilise `jobs/`, séparé du dossier `messages/` lu par la v0.4. La recette a effectivement révélé qu'une ancienne fenêtre réduite pouvait continuer à traiter des demandes. Les nouveaux contrats ne sont plus exposés à son moteur. L'historique précédent reste lisible. Ne pas considérer un retour à la v0.4 comme une migration des nouveaux travaux.

## Preuves acquises

- **144 tests hors ligne** : identité, stockage, reset, instances, droits de projet, capacités, idempotence, manifeste, quota inconnu, dépendances, source fermée, retours, réponses longues, permissions, UTC/local, reprise, concurrence, cache et verrou temporaire Windows.
- Compilation des deux exécutables avec avertissements traités comme erreurs, sans téléchargement de dépendances.
- Test d'un **vrai processus de moteur séparé**, sans compte : unicité, file durable, arrêt, absence de redémarrage implicite et reprise après redémarrage.
- Installation répétée du plugin dans un profil de qualification : empreinte stable, plugin activé, deux skills et outils MCP réellement chargés par **Codex CLI 0.158.0-alpha.2.1**.
- Validation des manifestes du plugin et des deux skills ; rendu des écrans Travaux, Configuration et Nouvelle demande avec données fictives.
- **Recette sur deux comptes distincts**, bureau **OpenAI.Codex 26.924.2738.0** : soumission par MCP, revue reçue dans le chat source ; refus 404 réel d'une ressource privée sur le compte développeur ; accès confirmé par le propriétaire ; fichier préparé partagé, publication v3 sur le même Site, résultat et SHA256 revenus au développeur. Le statut `succeeded` du déploiement a été relu indépendamment avec Sites. Aucun reset consommé par cette recette.
- Cette recette a été perturbée par les demandes d'approbation du chat source (`workspace-write / on-request`) et par le moteur v0.4 encore ouvert. Elle prouve l'échange et l'action du propriétaire, **pas une exécution entièrement autonome de tous les nouveaux composants**. La source a été suspendue ensuite ; l'action déjà soumise avait abouti. Le moteur ne prétend pas annuler rétroactivement une publication.
- Après fermeture de la v0.4, **test réel du nouveau moteur** : création d'un chat de préparation sans outil, lecture du contexte effectif, mandat transmis seulement après confirmation `full-access`, résultat structuré `succeeded` conservé. Les cas de décalage et de contexte inconnu sont couverts hors ligne sans validation automatique d'approbation.

## Limites de qualification

- Les chats source existants conservent leurs permissions et peuvent conserver leur ancienne instance MCP. L'installation d'un plugin ne les passe pas en Accès complet.
- Les droits réels d'un plugin privé arbitraire ne sont pas déductibles d'une étiquette ou de sa seule installation. Le destinataire doit effectuer la vérification adaptée.
- Aucun arrêt ciblé natif, administrateur de worktrees, compte Windows séparé, service de démarrage Windows ou adaptateur de publication sans interface n'est livré. Ces extensions de l'audit sont des périmètres distincts ou dépendent d'une interface non qualifiée.
- `MaxMinutes` borne l'attente avant démarrage. Il ne tue pas un tour actif : l'API d'interruption manque. Le parent doit terminer son tour pour libérer les ressources nécessaires aux enfants.
- L'exécution dans un projet Codex enregistré n'a pas été éprouvée sur ce poste, qui ne retourne aucun projet enregistré. Le fallback sans projet conserve le dossier dans le mandat ; il n'en fait pas automatiquement le répertoire initial du chat.
- Pas de qualification multi-version Windows, multi-DPI ou de tous les formats futurs des journaux Codex. Les formes inconnues bloquent la préparation.

## Reproduire

```powershell
./scripts/build.ps1
./scripts/test.ps1
./scripts/worker-smoke.ps1
./scripts/integration-smoke.ps1 # Codex CLI installé requis ; profil de test sans compte
./scripts/package.ps1 -SkipBuild
```

Le ZIP inclut uniquement le produit, le plugin et la documentation. Les profils, canaux, fichiers privés de recette et exécutables de test sont exclus. Les sources de recette réelle sont paramétrées, sans compte personnel ni identifiant de Site intégré.
