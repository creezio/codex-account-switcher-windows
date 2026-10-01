# Validation de la version 0.1.0

Vérifications effectuées le 1er octobre 2026 sur l'hôte Windows de développement.

## Effectué

- Compilation C# avec le compilateur Windows .NET Framework, avertissements
  traités comme erreurs ; aucun téléchargement de dépendances.
- **20 tests automatisés hors ligne** : identité par utilisateur et espace,
  refus des fichiers invalides, quotas inconnus, limites multiples, valeurs
  anciennes, chiffrement DPAPI, coffre corrompu préservé, collisions de fichiers
  temporaires, client ouvert, modes keyring/auto/ephemeral, paramètres TOML,
  bascule nominale, sauvegarde, restauration octet pour octet, échec de sauvegarde,
  absence initiale de connexion, écritures concurrentes, confinement des
  suppressions, restauration d'un ancien mode API key et conservation de l'historique.
- Essai du protocole avec **Codex CLI 0.158.0-alpha.2.1** : serveur isolé,
  initialisation JSON-RPC et lecture d'un état non connecté.
- **Lecture réelle des quotas du compte local**, via le mode externe
  `chatgptAuthTokens` du serveur Codex ; une catégorie de quotas reçue. Le fichier
  de connexion actif est resté identique avant et après. Ni jetons, ni identité,
  ni valeurs personnelles publiés dans les journaux de test.
- Arrêt des serveurs auxiliaires créés et suppression de leurs dossiers de travail
  à la fin des essais.
- Rendu et inspection visuelle de la fenêtre avec deux comptes fictifs.

## À valider en interaction

- Connexion OAuth complète d'un second compte dans le navigateur.
- Bascule réelle entre deux comptes, après fermeture volontaire de Codex, puis
  contrôle du profil affiché après sa réouverture.
- Recette sur plusieurs versions Windows, thèmes système et densités d'affichage.

Le badge « Compte local » et la vérification du fichier ne prouvent pas à eux seuls
que l'application de bureau a chargé ce compte. La bêta ne prétend pas une reprise
automatique des conversations, et n'interrompt aucun client pour faire ce test.
