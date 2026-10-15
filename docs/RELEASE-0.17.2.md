# Version 0.17.2-beta.1 — Windows x64

## Créer un environnement progressivement

L’action **Nouvel environnement** ouvre un assistant. Le choix du compte charge
automatiquement son catalogue GitHub. Il n’est plus nécessaire de lancer
manuellement le chargement des dépôts avant de configurer l’environnement.

Les étapes sont **Compte → Dépôts → Réglages → Démarrage → Vérifier**. Plusieurs
dépôts peuvent être sélectionnés, chacun avec sa branche. Les champs de réglage
concernent l’environnement choisi et les scripts de démarrage restent à une étape
dédiée. Revenir en arrière
conserve la saisie ; changer de compte invalide les choix de dépôt de l’ancien
compte. Le récapitulatif permet de vérifier la cible et les paramètres avant
l’enregistrement explicite.

Après une soumission, une réponse inexploitable ou une erreur locale peut laisser
le résultat inconnu. L’assistant conserve alors l’intention et indique cette
incertitude ; il n’affirme pas que l’opération distante a été arrêtée et ne renvoie
pas automatiquement une seconde demande.

La création d’une configuration ne vaut pas préparation ni publication. Le suivi
et la publication initiale restent des actions distinctes du catalogue Cloud.

## Modifier dans le compte propriétaire

La modification suit également un assistant. Le compte propriétaire est affiché
et fixé : modifier un environnement ne le déplace pas vers un autre compte.
L’environnement et ses droits GitHub restent ceux de son propriétaire.

La configuration existante est chargée avant la saisie. Les valeurs et scripts
peuvent être relus dans le récapitulatif avant l’action finale. Une réponse tardive
d’un autre catalogue ne doit jamais remplacer les choix du compte courant.

Le nom existant est présenté en lecture seule. Son renommage reste une action
séparée dans le menu de l’environnement, afin de ne pas mélanger deux opérations
distantes sans garantie commune. À la création, le réseau est désactivé par
défaut. En modification, **Conserver** ne remplace pas la politique réseau
existante, y compris ses restrictions personnalisées.

## Limites conservées

L’assistant simplifie la configuration sans ajouter de droits au service Cloud.
La création d’un brouillon pour un environnement déjà publié peut toujours
recevoir **HTTP 403**. Ce refus reste visible et aucune publication réussie n’est
annoncée dans ce cas. La connexion OAuth initiale à GitHub n’est pas automatisée ;
le catalogue suppose le consentement officiel déjà établi sur le compte.

Les comptes permanents, l’isolation des instances, les protections d’envoi et les
limites de concurrence restent inchangés. Les fichiers locaux ne sont pas copiés
automatiquement dans un conteneur Cloud. Voir
[CLOUD-NATIVE-PILOT.md](CLOUD-NATIVE-PILOT.md) pour la recette native et
[RELEASE-0.17.1.md](RELEASE-0.17.1.md) pour le parcours des missions Cloud.

## Validation

- Compilation WPF sans avertissement ni erreur et **399 vérifications UI**.
- Parcours de création et de modification, chargements automatiques, réponses
  tardives, listes vides, erreurs et reprise vérifiés avec des transports simulés.
- Sélection d’un dépôt en une activation accessible, plusieurs dépôts et branches,
  conservation des valeurs existantes et récapitulatif avant envoi vérifiés.
- Étapes **Dépôts**, **Réglages** et **Démarrage** contrôlées en **680 × 580**,
  avec les actions de navigation visibles.
- Compatibilité des données Framework ↔ .NET 10 vérifiée dans les deux sens.
- Les 399 vérifications ont aussi réussi sur la livraison portable. Son démarrage
  de contrôle réussit avec `DOTNET_ROOT` et `DOTNET_ROOT_X64` invalides ; les 468
  fichiers de runtime partagés et leur source sont restés identiques par SHA-256.

Cette recette ne crée ni ne modifie d’environnement réel. Elle ne constitue pas
une nouvelle qualification du service HTTP 403 ou de la connexion OAuth GitHub.
La [capture du README](../assets/cloud-environment-wizard.png) provient de
l’application réelle avec un compte et des dépôts fictifs.

## Livraison

Décompresser tout le ZIP, puis lancer **CodexAccountSwitcher.exe**. Windows 10/11
x64 est requis. Le runtime .NET 10 est inclus ; aucun SDK n’est nécessaire.
Conserver les DLL, exécutables, fichiers JSON et le dossier `plugins` ensemble.

Quitter seulement l’ancienne application Switcher avant de lancer cette version.
Les fenêtres Codex peuvent rester ouvertes. Les données et profils existants sont
relus sur place. Conserver tout ancien dossier encore utilisé par un hôte
d’instance ou un serveur MCP.

La préparation locale réutilise les fichiers de runtime identiques de la livraison
existante après comparaison SHA-256 avec les packs officiels en cache. Le ZIP
final contient des fichiers autonomes et ne dépend pas du dossier précédent.
