# Validation de la version 0.2.0

Vérifications effectuées le 1er octobre 2026 sur l'hôte Windows de développement.

## Effectué

- Compilation C# avec le compilateur Windows .NET Framework, avertissements
  traités comme erreurs ; aucun téléchargement de dépendances.
- **44 tests automatisés hors ligne**, dont les 20 vérifications initiales : identité par utilisateur et espace,
  refus des fichiers invalides, quotas inconnus, limites multiples, valeurs
  anciennes, chiffrement DPAPI, coffre corrompu préservé, collisions de fichiers
  temporaires, client ouvert, modes keyring/auto/ephemeral, paramètres TOML,
  bascule nominale, sauvegarde, restauration octet pour octet, échec de sauvegarde,
  absence initiale de connexion, écritures concurrentes, confinement des
  suppressions, restauration d'un ancien mode API key et conservation de l'historique.
- **24 vérifications du reset automatique** : métadonnées absentes ou malformées,
  compte local uniquement, seuil exact à 1 % et 0 %, fenêtre hebdomadaire, identité
  du quota dans le format historique,
  fraîcheur, compte changé juste avant l'envoi, crédits expirés/incompatibles,
  nombre seul, priorité d'expiration, intention durable avant envoi, reprise après
  timeout/redémarrage avec la même clé, maximum de trois tentatives, lecture après
  acceptation, délai de cinq minutes, résultats négatifs/inconnus, concurrence,
  annulation et migration des paramètres.
- Essai du protocole avec **Codex CLI 0.158.0-alpha.2.1** : serveur isolé,
  initialisation JSON-RPC et lecture d'un état non connecté.
- **Lecture réelle des quotas du compte local**, via le mode externe
  `chatgptAuthTokens` du serveur Codex ; une catégorie de quotas reçue. Le fichier
  de connexion actif est resté identique avant et après. Ni jetons, ni identité,
  ni valeurs personnelles publiés dans les journaux de test.
- Lecture réelle de `rateLimitResetCredits` : métadonnées présentes et interprétées
  par le nouveau modèle. **Aucun appel de consommation effectué pendant cet essai.**
- Arrêt des serveurs auxiliaires créés et suppression de leurs dossiers de travail
  à la fin des essais.
- Rendu et inspection visuelle de la fenêtre avec deux comptes fictifs.

## À valider en interaction

- Connexion OAuth complète d'un second compte dans le navigateur.
- Consommation d'un crédit réel lorsqu'un compte atteint le seuil : les tests ne
  dépensent pas de crédit pour simuler artificiellement une situation de limite.
- Bascule réelle entre deux comptes, après fermeture volontaire de Codex, puis
  contrôle du profil affiché après sa réouverture.
- Recette sur plusieurs versions Windows, thèmes système et densités d'affichage.

Le badge « Compte local » et la vérification du fichier ne prouvent pas à eux seuls
que l'application de bureau a chargé ce compte. La bêta ne prétend pas une reprise
automatique des conversations, et n'interrompt aucun client pour faire ce test.
