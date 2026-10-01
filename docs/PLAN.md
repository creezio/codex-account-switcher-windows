# Plan de réalisation — Codex Account Switcher pour Windows

## Objectif

Application native Windows 10/11, en français, avec comptes ChatGPT enregistrés,
quotas Codex, bascule manuelle et icône dans la zone de notification.
Dépôt public : `creezio/codex-account-switcher-windows`.

## Première version

1. **Socle léger** : C# / Windows Forms, .NET Framework 4.8 inclus dans les versions
   Windows ciblées, compilation avec le compilateur Windows existant ; aucun NuGet.
2. **Comptes** : import de la connexion locale, ajout par le parcours OAuth officiel
   Codex, noms personnalisés et suppression du coffre local.
3. **Protection** : coffre chiffré DPAPI pour l'utilisateur Windows, permissions
   restreintes, écritures atomiques et aucune journalisation des jetons.
4. **Quotas** : interrogation du serveur officiel `codex app-server` via JSON-RPC,
   pour chaque compte ; fenêtres, dates de réinitialisation et erreurs explicites.
5. **Bascule** : stockage fichier uniquement, sauvegarde du compte précédent,
   refus tant que Codex/ChatGPT est ouvert, remplacement atomique de `auth.json`,
   contrôle de l'identité et restauration en cas d'échec. Aucune interruption des
   tâches ni modification des conversations ou de la configuration Codex.
6. **Interface** : tableau de bord, compte recommandé parmi les quotas frais,
   actualisation manuelle/périodique et notifications facultatives.
7. **Crédits de reset (0.2.0)** : afficher la disponibilité et l'expiration ;
   contrôler le compte local chaque minute, consommer un crédit à 1 % ou moins,
   conserver une intention idempotente dans le coffre, vérifier les quotas après
   acceptation et offrir une désactivation immédiate.
8. **Livraison** : tests de sécurité et de bascule avec comptes fictifs, essai du
   protocole local sans connexion, vérification visuelle, ZIP portable, somme SHA-256
   et pipeline GitHub Actions pour reproduire la compilation et les tests.

## Validation nécessitant l'utilisateur

Une connexion OAuth réelle exige l'intervention de l'utilisateur dans son navigateur.
La recette de bascule entre deux comptes réels exige la fermeture de Codex ; elle ne
sera pas présentée comme effectuée pendant cette conversation active.

## Version 0.3.0 — plusieurs instances

1. Migrer le coffre vers un format v2 conservant comptes, options et sauvegardes.
2. Séparer les instances persistantes de la session habituelle et des comptes du coffre.
3. Autoriser un compte dans toutes les instances, futures comprises, ou dans une sélection.
4. Lancer le paquet Microsoft Store avec un profil UI, un CODEX_HOME et des bases propres.
5. Confier chaque instance à un hôte persistant : détection après redémarrage du switcher,
   fermeture ciblée et refus de bascule tant que l'instance est ouverte.
6. Calculer recommandations et resets par compte autorisé, sans double consommation.
7. Archiver sans effacer les profils ; réutiliser la même installation Codex et éviter
   les téléchargements automatiques de dépendances bureautiques dans les nouveaux profils.
8. Tester migration, associations, séparation, refus, comptes partagés, lancement réel,
   fermeture ciblée et conservation de la session de travail existante.

## Après cette version

- Intégration au coffre natif Codex (`keyring`/`auto`) après validation dédiée.
- Bascule automatique et reprise de tâches, après validation des interruptions.
- Signature Authenticode et installateur.

## Sources techniques

- https://learn.chatgpt.com/docs/auth#credential-storage
- https://learn.chatgpt.com/docs/app-server
- Inspiration fonctionnelle : https://github.com/lordydord/Codex-Account-Switcher

L'implémentation Windows est indépendante : aucun code Swift n'est repris.
