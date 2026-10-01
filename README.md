# Codex Account Switcher pour Windows

Une application native et légère pour retrouver les quotas de vos comptes Codex
et choisir celui que vous souhaitez utiliser. Développée par **Creezio**.

**Version 0.1.0 — première version expérimentale.** Windows 10/11, interface en
français, exécutable portable sans droits administrateur ni dépendances NuGet.

[Télécharger la version Windows](https://github.com/creezio/codex-account-switcher-windows/releases/tag/v0.1.0-beta.1)
· [Versions et téléchargements](https://github.com/creezio/codex-account-switcher-windows/releases)
· [Plan de réalisation](docs/PLAN.md)
· [Validation](docs/VALIDATION.md)

![Interface avec deux comptes fictifs](assets/screenshot.png)

## Fonctionnalités

- Ajout de comptes avec la connexion officielle Codex dans votre navigateur.
- Import du compte présent dans le fichier local `auth.json`.
- Coffre chiffré avec Windows DPAPI, lié à votre utilisateur Windows.
- Quotas par compte et par catégorie : pourcentage restant, durée et date de
  réinitialisation. Une donnée inconnue reste inconnue.
- Suggestion du compte disposant de la meilleure marge parmi les valeurs récentes.
- Bascule manuelle avec vérification serveur préalable, sauvegarde chiffrée,
  remplacement atomique et restauration en cas d'échec local.
- Icône près de l'horloge, actualisation automatique facultative toutes les
  cinq minutes et notifications lorsque les quotas deviennent faibles.
- Noms personnalisés et retrait des comptes du coffre.

## Installation

1. Téléchargez le ZIP depuis [Releases](https://github.com/creezio/codex-account-switcher-windows/releases).
2. Décompressez-le dans un dossier de votre choix.
3. Lancez **CodexAccountSwitcher.exe**. Conservez le fichier `.exe.config` à côté.

Windows 10/11 avec **.NET Framework 4.8** est requis. Le programme utilise
**Codex CLI** : la version fournie par l'application Codex est recherchée dans le
`PATH` et `%LOCALAPPDATA%\OpenAI\Codex\bin`. Sinon, sélectionnez votre `codex.exe`
dans **Paramètres**. Les lanceurs npm `.cmd` ne sont pas acceptés ; sélectionnez
le véritable exécutable fourni par votre installation.

Le binaire de cette première version n'est pas signé avec Authenticode. Le code
source, les tests et les sommes SHA-256 sont publiés pour inspection.

## Utilisation

### Enregistrer les comptes

**Importer le compte local** copie la connexion du dossier Codex configuré vers
le coffre chiffré. Le fichier actif n'est pas modifié.

**Ajouter un compte** ouvre le parcours officiel de connexion dans votre
navigateur. Choisissez le compte souhaité. Pour reconnecter un compte expiré,
ajoutez de nouveau ce même compte : son entrée et son nom sont conservés.

Cliquez sur **Actualiser** pour obtenir les quotas. Les anciennes valeurs sont
conservées avec un avertissement si une requête échoue. Les recommandations
excluent les valeurs datant de plus de dix minutes et les comptes en erreur.

### Changer le compte de Codex

1. Terminez les tâches en cours et quittez complètement Codex/ChatGPT ainsi que
   les terminaux exécutant Codex. Fermez aussi les sessions Codex dans votre IDE.
2. Dans le switcher, cliquez sur **Utiliser ce compte**.
3. Rouvrez Codex et vérifiez le compte affiché dans son profil.

L'application refuse la bascule lorsqu'elle détecte un processus `codex`,
`codex-*` ou `ChatGPT`. Elle ne ferme jamais vos applications. Le badge
**Compte local** désigne le compte enregistré dans le fichier du dossier choisi,
pas une preuve du compte chargé en mémoire par une application déjà ouverte.

**Paramètres → Restaurer la connexion précédente** rétablit la dernière
sauvegarde. Une seule sauvegarde est conservée, chiffrée dans le coffre.

Fermer la fenêtre conserve l'icône de notification. Pour arrêter le programme,
faites un clic droit sur cette icône puis **Quitter**.

## Compatibilité et limites

- La bascule cible le stockage **fichier** de Codex : `CODEX_HOME/auth.json`, ou
  `%USERPROFILE%\.codex\auth.json` par défaut. `CODEX_HOME` est respecté et le
  dossier peut être choisi dans les paramètres.
- Les modes `keyring`, `auto` et `ephemeral` sont refusés pour la bascule et
  l'import local. Le switcher ne réécrit pas `config.toml`. Les installations
  gérées qui imposent un autre magasin de connexion ne sont pas prises en charge.
- Les quotas passent par `codex app-server` et son mode expérimental
  `chatgptAuthTokens`. Une version incompatible produit un état d'erreur, sans
  inventer des quotas. Version testée : voir [VALIDATION.md](docs/VALIDATION.md).
- La consultation ne renouvelle pas le refresh token d'un compte copié : cela
  évite d'interférer avec une session ouverte. Le compte actif récupère les
  nouveaux jetons du fichier local ; un compte inactif expiré doit être reconnecté.
- La bascule entre deux comptes réels dans l'application de bureau et le parcours
  OAuth complet demandent encore une recette interactive. Les tests automatisés
  couvrent les opérations locales avec des comptes fictifs.
- Pas de bascule automatique, de reprise automatique des conversations ni de
  consommation de crédits de réinitialisation dans cette première version.

## Données et protection

Le coffre `accounts.dpapi` et les paramètres se trouvent dans
`%LOCALAPPDATA%\Creezio\CodexAccountSwitcher`. Ce dossier n'est accessible qu'à
l'utilisateur Windows courant et au compte système. Les noms, adresses et jetons
des comptes sont tous inclus dans le coffre chiffré. Il n'est pas portable vers un
autre utilisateur Windows.

Pour la connexion officielle, le serveur Codex écrit temporairement sa connexion
dans un sous-dossier privé `runtime`. Ce serveur est isolé dans un Windows Job
Object : ses processus auxiliaires sont arrêtés à la fermeture. Le dossier de
travail est supprimé à la fin de l'opération ; un verrou Windows ou un arrêt
brutal peut nécessiter le nettoyage manuel des restes, après vérification que
l'application est arrêtée. Aucune collecte de télémétrie n'est ajoutée ; celle
des serveurs Codex temporaires est désactivée.

La protection DPAPI ne protège pas contre un logiciel malveillant exécuté sous
votre propre utilisateur Windows. Le fichier actif `auth.json` demeure au format
requis par Codex. Les conversations, projets, bases et réglages de Codex ne sont
pas déplacés ni supprimés.

## Compiler et tester

Depuis PowerShell à la racine du dépôt :

```powershell
.\scripts\build.ps1
.\scripts\test.ps1
```

Le compilateur C# fourni avec .NET Framework est utilisé directement. Aucun SDK
supplémentaire ni téléchargement de dépendances n'est nécessaire. La sortie est
réutilisée dans `build/`, les tests dans `work/`.

Avec Codex CLI installé, vérifiez aussi le protocole isolé :

```powershell
.\scripts\test.ps1 -ProtocolSmoke
```

Le contrôle réel facultatif suivant lit les quotas du compte local, sans changer
de compte ni renouveler ses jetons ; il ne publie aucune identité ni valeur de
quota dans les journaux :

```powershell
.\scripts\test.ps1 -LiveReadOnly
```

Pour produire le ZIP portable :

```powershell
.\scripts\package.ps1
```

GitHub Actions compile l'application et exécute les tests hors ligne à chaque
push et pull request. Aucune connexion réelle n'est nécessaire dans la CI.

## Origine et licence

Le projet [lordydord/Codex-Account-Switcher](https://github.com/lordydord/Codex-Account-Switcher)
a inspiré les fonctions de cette application. Il s'agit d'une implémentation
Windows indépendante en C# ; aucun code Swift ni ressource graphique n'a été copié.

Documentation technique : [authentification Codex](https://learn.chatgpt.com/docs/auth)
et [Codex App Server](https://learn.chatgpt.com/docs/app-server).

Licence [MIT](LICENSE), © 2026 Creezio. Projet indépendant, non affilié à OpenAI.
