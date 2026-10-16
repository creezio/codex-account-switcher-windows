# Validation 0.8.0-beta.1 — 3 octobre 2026

## Résultats

| Vérification | Résultat | Portée |
|---|---|---|
| Moteur .NET Framework 4.8 | 217 tests réussis | Fonctions précédentes, messages directs, TLS, assistance, contrat des hooks |
| Moteur .NET 10 | 217 tests réussis | Même suite dans le runtime WPF |
| Interface WPF | 39 contrôles réussis | Navigation et sidebar, 9 pages, brouillons, composeur, assistance, recherche, sauvegarde, thèmes, clavier, surfaces compactes |
| Compatibilité DPAPI/JSON | Réussie dans les deux sens | Coffre, politiques et historique de 57 tâches entre Framework et .NET 10 |
| Compilation portable | Réussie | Runtime .NET 10 inclus, avertissements traités comme erreurs |
| Plugin et skills | Validés | Manifeste et trois skills, quatorze outils MCP exposés |

Neuf nouveaux scénarios de message utilisateur, quatorze de partage distant et
onze d'assistance complètent les tests précédents. Les données sont isolées du
coffre réel. Rapports et captures WPF : `work/desktop-validation`, hors Git.

## Essais avec Codex desktop réellement ouvert

- Message utilisateur vers un chat existant : résultat attendu reçu, `completed`.
- Création d'un chat puis transmission après prévol : résultat attendu reçu.
- Client Switcher → TCP/TLS → serveur Switcher → chat natif Codex → retour TLS :
  résultat `SWITCHER_TLS_OK` reçu et tâche terminée.
- Réponse d'assistance du client TLS → ticket du propriétaire → chat natif Codex :
  `SWITCHER_ASSISTANCE_OK` reçu, ticket `delivered`.

Les deux derniers essais utilisaient deux magasins isolés sur la même machine,
avec une vraie connexion TLS et le véritable adaptateur Codex. Ils ne constituent
pas une recette sur deux PC. Le serveur temporaire est arrêté, son invitation
révoquée et son écoute désactivée après le test. Les prompts demandaient des
réponses sans outil ; aucune publication ni modification de projet client.
Historiques locaux : `work/console-live` et `work/remote-live`, hors Git.

## Refus et pannes vérifiés

Chat non reconnu ou occupé, restriction de projet, reprise hors ligne, livraison
incertaine sans renvoi aveugle, certificat ou secret incorrect, révocation,
expiration, compte/dossier modifié, conversation privée, outil arbitraire,
dépassement de taille, retrait des sorties d'outils dans les transcriptions partagées.

Assistance : demande sans règle ni mandat refusée, déduplication persistante,
réponse unique, attente d'un chat occupé, analyse en lecture seule, périmètre du
chat source, droit distant spécifique et conservation des autres hooks.
Un marqueur falsifié ne contourne pas le filtre ; seule la réponse exacte liée
à un envoi d'assistance enregistré est exemptée.

## Limites non validées

- **Deux PC Windows distincts** : non testé. Le serveur proposé refuse l'accès
  SSH, y compris avec l'identifiant Windows enregistré pour ce serveur. Un accès
  RDP fonctionnel ne prouve pas l'acceptation du même accès par SSH. Aucun secret
  publié dans les sources ou journaux ; aucun changement distant.
- **Hook natif** : contrat et installation testés ; chargement et approbation dans
  Codex non testés. Filtre expérimental, désactivé par défaut, nécessitant une
  validation native. Il ne remplace pas une politique de sécurité.
- **Cursor IDE** : non pris en charge. L'essai CLI a été abandonné à la demande de
  l'utilisateur. Aucun adaptateur Cursor livré ni compte Cursor connecté pendant
  la recette. La suppression des fichiers de cet essai a été refusée par le
  contrôle automatique ; ils ne figurent pas dans la livraison.
- Pas de synchronisation de fichiers ni des registres de sous-tâches entre PC.
  Retour distant limité au texte final recueilli. Protocole desktop local sensible
  aux évolutions de Codex, à requalifier lors des mises à jour.

L'ancien switcher et les MCP des conversations existantes sont préservés. Quitter
l'ancien switcher via son menu avant de lancer la 0.8.
