# Version 0.6.0-beta.1 — parcours et qualification

## Parcours disponible

La vue d'ensemble présente les composants et les travaux à traiter. Premiers pas
propose trois parcours : comptes, instances, collaboration. Aucun compte, service
privé ou fournisseur n'est imposé. L'interface de cette version est en français.

Agents et Projets utilisent un éditeur guidé avec options avancées, validation des
références, aperçu des modifications, import/export et restauration du précédent
enregistrement. Trois modèles facultatifs proposent relecture, développement ou
ressource privée ; leurs règles automatiques restent désactivées. La simulation
montre les choix et exclusions sans envoyer de demande.

Travaux propose recherche, filtres, pagination, motif d'attente et prochaine action.
Annuler la file ne prétend pas arrêter un tour Codex. Les départs peuvent être
suspendus, ou le moteur peut terminer les groupes acceptés avant de s'arrêter.
Les limites de concurrence s'appliquent au compte, à l'agent, au projet et aux
fichiers. Une réaffectation demande une option explicite et ne concerne que les
demandes automatiques sans chat déjà créé. Les retours peuvent être immédiats,
groupés ou manuels. `await_children` libère un parent après la fin effective de son
tour, puis le reprend avec les résultats de ses enfants.

Limites distingue les réinitialisations de quota des crédits achetés. Chaque
compte peut hériter du réglage global, autoriser l'automatisme, fonctionner
manuellement ou désactiver la consommation. Seuil, fenêtre déclenchante et
notifications sont configurables. La supervision peut continuer indépendamment de
la fenêtre ; elle est désactivée par défaut. Le cache partagé ne contient pas les
jetons d'authentification. L'intention de consommation est persistée avant envoi,
avec verrou par compte, reprise idempotente et relecture des autorisations.

Diagnostics fournit un export par liste blanche, un accès aux espaces Git,
aux empreintes de fichiers, aux temporaires identifiés et aux versions publiques.
La vérification de mise à jour ne télécharge ni ne remplace d'exécutable.

## Vérifications du 3 octobre 2026

| Vérification | Résultat |
|---|---|
| Compilation C# Windows, avertissements traités comme erreurs | Réussie, aucune dépendance installée |
| Tests hors ligne | **182 réussis**, y compris reprise, périmètres, permissions, idempotence, annulation, routage et concurrence |
| Processus du relais | Démarrage détaché, unicité, persistance, arrêt manuel et reprise vérifiés |
| Processus de supervision des quotas | Démarrage, unicité et arrêt coopératif vérifiés sans compte ni consommation |
| Intégration dans le vrai Codex CLI | Deux skills et les **onze outils MCP** chargés ; désactivation utilisateur conservée ; réinstallation explicite vérifiée |
| Rendus de l'interface | Huit écrans avec données fictives ; contrôle à 1024 × 700, formulaire d'envoi et politiques de limites |
| Historique volumineux | 2 500 demandes fictives, 25 actives, pages de 100 : 1 629 ms à froid, 26 ms avec cache sur la machine de recette |
| Espaces Git | Worktree réel depuis un commit ; modifications du dépôt source conservées ; réutilisation conservant les modifications locales |
| Nettoyage du benchmark | Données générées retirées à la fin ; compte rendu conservé |
| Échange réel entre deux comptes | Modification depuis le compte préparateur, publication par le propriétaire du site existant, reçu retourné, puis ACK final reçu ; aucun travail encore actif |

Les timings ne sont pas une garantie pour toutes les machines. La création du jeu
de 2 500 demandes avec intentions chiffrées et écritures synchrones a pris environ
16 minutes ; ce chiffre est distinct du temps de consultation de l'historique.
Les tests automatiques ne consomment aucune réinitialisation réelle.

La recette réelle a détecté une concurrence entre lecteurs et écrivains de
l'index. Les lectures de pages et d'activité partagent désormais le verrou de la
transaction ; l'acquisition attend brièvement un écrivain en cours. Le moteur ne
s'arrête plus sur cette contention de stockage. La lecture des métadonnées utilise
le handle ouvert, avec reprise bornée des erreurs transitoires de remplacement.
Un test vérifie explicitement qu'une lecture attend la fin de l'écriture.

## Résultat de la recette réelle

Le compte préparateur a modifié un paragraphe et transmis son SHA256 via le MCP.
Le propriétaire a contrôlé cette empreinte et publié la version 4 du site existant,
avec son audience conservée. Le résultat est revenu au préparateur, qui a envoyé
une seule demande d'accusé en poursuivant le même chat destinataire. Le retour
`ACK-RELAY-V06` a été reçu et la source a terminé sans relancer d'autre délégation.
Le statut `succeeded` du déploiement a été vérifié indépendamment avec l'outil natif
Sites du compte propriétaire. Quatre demandes de recette ont un résultat remis :
deux blocages de préparation explicitement rapportés, la publication réussie, puis
l'accusé final. Aucun succès n'a été annoncé pendant les blocages.

Deux incompatibilités du packager Sites sur Windows ont été résolues sans modifier
le plugin : utilisation de Git Bash au lieu du lanceur WSL et `TAR_OPTIONS=--force-local`
pour que GNU tar accepte une archive avec un chemin `C:`. Ces réglages concernaient
le seul processus de publication ; ils ne sont pas des valeurs par défaut du switcher.
Aucun déploiement n'avait été lancé lors des tentatives bloquées.

Le nouveau chat source s'était ouvert en `workspace-write / on-request`. Le test
est resté sans travail jusqu'à la sélection d'Accès complet par l'utilisateur dans
ce chat, puis la confirmation native du contexte. Le relais n'a validé aucune
approbation. Le compte préparateur n'a pas obtenu l'accès direct à la conversation
privée du propriétaire ; il a reçu uniquement le résultat remis par le relais.

## Mise à jour

1. Terminer les travaux des anciennes versions et quitter leur interface depuis
   l'icône près de l'horloge. Les fenêtres Codex restent ouvertes.
2. Décompresser la nouvelle livraison dans son propre dossier. Garder les deux
   exécutables, configurations et le dossier `plugins` ensemble.
3. Ouvrir le nouveau switcher et vérifier la vue d'ensemble.
4. Réinstaller l'intégration sur les profils souhaités. Les chats existants peuvent
   garder leur ancien serveur MCP ; utiliser un nouveau chat pour qualifier la
   nouvelle intégration. Ne pas supprimer un ancien binaire encore chargé.

Les historiques anciens restent lisibles, mais le moteur 0.6 exécute uniquement
les contrats `jobs-v3/`. Il n'adopte pas silencieusement des demandes actives d'un
ancien moteur. La nouvelle configuration utilise `policy-v2.dpapi` et conserve
une sauvegarde du précédent enregistrement. Un retour à une ancienne version ne
reprend pas les nouvelles demandes.

## Limites qui restent explicites

- Les permissions appartiennent à chaque chat Codex. Le relais ne les change pas,
  ne valide aucune approbation et bloque le travail si le précontrôle ne confirme
  pas le mode demandé. Le choix du mode dans un autre chat ne suffit pas.
- L'arrêt ciblé d'un tour reste à effectuer dans Codex. Un arrêt du moteur ne
  signifie pas que les agents actifs ont été arrêtés.
- Le transport bureau est une interface interne sensible aux versions de Codex.
  L'exécution sans interface avec les mêmes plugins privés n'est pas qualifiée.
- Un plugin installé et une capacité déclarée ne prouvent pas l'accès à une
  ressource. Cette vérification appartient au compte destinataire.
- Les espaces Git et manifestes sont explicites. Pas de fusion automatique,
  d'installation de dépendances ni d'isolement système entre processus du même
  utilisateur Windows. Les applications externes n'utilisent pas les verrous du relais.
- L'anglais complet, la qualification avec lecteur d'écran et un vrai bureau à
  fort DPI ne sont pas livrés comme vérifiés dans cette bêta. Les libellés et
  contrôles d'accessibilité de base sont présents.
- Les binaires sont **non signés** : aucun certificat Authenticode de signature de
  code n'est disponible sur la machine de livraison. `scripts/sign.ps1` permet de
  signer et vérifier les deux exécutables avec un certificat valide, puis
  `scripts/package.ps1 -SkipBuild` conserve ces signatures. Cette chaîne n'est pas
  présentée comme une signature publique déjà obtenue.
- Les historiques et données utilisateurs ne sont pas purgés automatiquement.
  Le nettoyage proposé concerne uniquement des temporaires portant une preuve
  d'appartenance et dont le processus propriétaire exact est arrêté.

Un ancien dossier de tests unitaires de cette session est conservé : la suppression
PowerShell a été refusée par le contrôle automatique. Les essais suivants ont
réutilisé un seul dossier de recette, nettoyé par le programme de test après usage.
