[CmdletBinding()]
param([switch]$SkipBuild,[string]$BinaryDirectory)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$bin=if($BinaryDirectory){[IO.Path]::GetFullPath($BinaryDirectory)}else{Join-Path $repo 'outputs\v0.18.0-beta.1'}
if(-not $SkipBuild){& (Join-Path $PSScriptRoot 'build-desktop.ps1') -OutputDirectory $bin -Portable}
foreach($required in @('CodexAccountSwitcher.exe','CodexAccountSwitcher.dll','CodexAccountSwitcher.runtimeconfig.json','coreclr.dll','PresentationFramework.dll','CreezioRelay.exe','plugins')){if(-not(Test-Path -LiteralPath (Join-Path $bin $required))){throw "Livraison autonome incomplète : $required"}}
$instructions=@'
CODEX ACCOUNT SWITCHER — 0.18.0-beta.1 — WINDOWS X64

Extraire TOUT le ZIP et lancer CodexAccountSwitcher.exe.
Garder les DLL, fichiers JSON, exécutables et dossier plugins ensemble.
Le runtime .NET 10 de cette interface WPF est inclus. Aucun SDK à installer.
Le moteur de relais utilise .NET Framework 4.8, présent sur Windows 10/11.

0.18 : Cloud, ressources et missions locales sans fenêtre Codex.
Instances > Compte > Fonctionnement sans fenêtre : activation et permissions.
Tâches > Interventions : répondre aux questions et confirmations explicites.
Performances : les moteurs temporaires sont attribués à leur compte.
Connexions relues et renouvelées à la demande, sans changer de compte.
Une session révoquée ou un consentement expiré exige une reconnexion officielle.
Un profil occupé par un moteur ne démarre pas un second moteur concurrent.
Les moteurs se libèrent après la mission, y compris leurs serveurs enfants.
Les fenêtres ouvertes conservent leur fonctionnement et ne sont pas fermées.
Voir README.md pour les limites de renouvellement et les permissions.

0.17.2 : création et modification guidées des environnements Cloud.
À la création, choisir le compte charge automatiquement ses dépôts GitHub.
Choisir les dépôts et branches puis les réglages, le démarrage et vérifier le résumé.
En modification, le compte propriétaire reste fixé : aucun transfert de droits.
Le nom existant se renomme séparément depuis le menu de l'environnement.
Le réseau d'une création est désactivé par défaut ; Conserver préserve en
modification la politique existante, y compris les restrictions personnalisées.
Les textes saisis sont conservés lors des retours entre étapes.
La préparation et la publication restent des opérations explicites séparées.

0.17.1 : configuration guidée des missions Cloud, une étape à la fois.
Choisir le compte charge automatiquement ses environnements publiés.
Choisir ensuite l'environnement, décrire la mission et vérifier avant l'envoi.
Le mandat reste présent lors des retours entre les étapes.
La fiche d'instance et le catalogue Cloud donnent la priorité au contenu ;
les actions secondaires sont réunies dans des menus contextuels.
Cette refonte ne change ni les comptes, ni les droits, ni les limites Cloud.

0.17 : pilotage de Codex Cloud depuis le compte permanent d'une instance.
Instances > Cloud : catalogue GitHub du compte, création privée, préparation,
suivi et publication explicitement séparés. Aucun passage par ChatGPT web.
Tâches > Missions Cloud : compte exécutant et environnement publié choisis,
mandat et branche distincte, état et résultat suivis dans une fenêtre dédiée.
Dans Codex : demander explicitement une mission Cloud à l'instance nommée.
Une réponse du modèle n'est ni un push, ni une PR, ni une fusion réussie.
Les changements locaux ne sont pas synchronisés dans le conteneur Cloud.
Les règles réseau, GitHub et les droits du compte cible restent applicables.
La connexion initiale GitHub et les demandes interactives ne sont pas automatisées.
La modification d'un environnement publié peut recevoir un refus HTTP 403 HTML ;
aucun contournement ni élargissement automatique des droits n'est effectué.
Le protocole Cloud est interne à Codex et peut évoluer : voir CLOUD-NATIVE-PILOT.md.
Les tâches, intentions et références de reprise sont conservées sans renvoi aveugle.
Les statuts de mission sont en français ; l'actualisation conserve la lecture.
Voir RELEASE-0.17.2.md pour les assistants d'environnement,
RELEASE-0.17.1.md pour les missions et RELEASE-0.17.md pour la recette Cloud.

0.16.1 : temporaires d'instance abandonnés récupérés automatiquement avant lancement.
Les écritures actives et les données validées sont préservées.
Échec du lancement affiche l'étape et le code Windows ; Ouvrir Codex permet de réessayer.
Le résultat est conservé dans instances/<identifiant>/startup.json, sans jetons.
Une nouvelle tentative ne reprend pas l'erreur de la précédente.
Voir STARTUP-RECOVERY.md pour les garanties et les limites de la récupération.

Instances : donner un nom, connecter un compte permanent, préparer et ouvrir.
Instances > Conversations et projets : cinq usages séparés.
Copier une conversation : choisir le chat source et une autre instance ouverte.
L’original reste intact ; la copie utilise le compte et les permissions cibles.
Les fichiers du projet restent communs. Les droits cloud ne sont pas transférés.
Copies récentes permet de rouvrir le résultat ou de vérifier une réponse perdue.
Transférer une conversation : copie vérifiée puis archivage réversible de la source.
Projet local : ouvrir ses fichiers sans historique, cloner, ou transférer ses chats actifs.
Dossier commun ou copie indépendante des fichiers, avec résumé avant exécution.
Le projet source reste en place. La propriété des ressources cloud reste inchangée.
Actualiser Codex et reprendre demande confirmation avant le seul redémarrage ciblé.
Connexion : choisir navigateur et profil, ou Copier le lien sans ouverture.
Le lien de connexion est affiché avec un bouton Copier pendant la tentative.
Coller le lien dans le navigateur souhaité sur ce même PC.
Le plugin et les skills sont installés et vérifiés automatiquement.
Dans Codex : « Délègue cette mission à Léa ». Le résultat revient dans ce chat.
Instances > Ressources & accès : choisir le destinataire, cocher les ressources
par leur nom, choisir Lecture ou Lecture et modification, enregistrer.
Pour les autres plugins : Configurer les actions, cocher et enregistrer.
Le plugin, son compte et l'instance destinataire sont conservés automatiquement.
Dans Codex : « Utilise les outils Pages de Principal pour modifier cette page ».
Aucun prompt envoyé au propriétaire. Codex doit rester ouvert sur les deux côtés.
Les fichiers joints ne sont pas transférés : publication de nouvelles versions
Sites indisponible dans le tunnel. Lire TOOL-TUNNEL.md pour les possibilités.
Instances > Compte : limites, réinitialisations et état de l'intégration.
Renommer l'instance reste accessible depuis ses actions, quel que soit l'onglet.
Vérifier l'intégration affiche la progression puis un résultat daté ou une erreur.
Afficher la fenêtre amène le Codex de cette instance au premier plan.
Rouvrir la fenêtre réactive une instance restée en arrière-plan après le clic X.
Fermer et Redémarrer sont accessibles dans les actions de l'instance, avec confirmation.
Ces deux actions interrompent les tâches de la cible et conservent ses données.
Activité affiche l'outil appelé et le motif exact de son éventuel blocage.
0.12.2 : liens nommés vers la Page ou le Site dans le Codex propriétaire.
Les anciennes cartes Pages natives restent soumises aux droits du compte.
Voir RESOURCE-LINKS.md pour le fonctionnement et les limites des liens locaux.
Ouvrir un nouveau chat pour charger le relais corrigé ; les droits sont conservés.
Réglages avancés : anciens canaux, projets, ressources et règles facultatives.
Tâches : envoyer un prompt vers un chat nouveau ou ciblé et lire sa réponse.
Performances : choisir une instance à gauche, ou via le sélecteur compact.
Vue d'ensemble : consommation par famille et processus les plus gourmands.
Processus : recherche, filtres et tri RAM/CPU ; détails dans le pied de liste.
Le résumé global reste visible, sans défilement horizontal ni page défilante.
Afficher/Ouvrir Codex cible le compte sélectionné. Vérifier l'activité lit les chats.
Le menu ... propose fermeture et redémarrage avec contrôle de tous les chats.
L'instance principale, les origines inconnues et les chats actifs restent protégés.
PC distants : partager des canaux avec invitations et droits révocables.
Assistance : recevoir une question, faire analyser et répondre au chat source.
Paramètres : thème, supervision, capacités, diagnostic et outils avancés.

La délégation est explicite par défaut. Aucun compte ni service n'est imposé.
Les réinitialisations concernent les limites d'utilisation, pas les achats de
crédits. L'automatisme s'active par compte et ne contourne aucune permission.

MISE À JOUR
Quitter l'ancien switcher via son icône près de l'horloge > Quitter.
Conserver les fenêtres Codex ouvertes. Lancer cette version depuis son dossier.
Les comptes, instances, configuration et tâches existants sont relus sur place.
Ne pas écraser ni supprimer un dossier utilisé par un hôte d'instance ou un MCP.
Les nouvelles tâches utilisent un format séparé des moteurs 0.6/0.7.
Ne pas faire tourner deux moteurs concurrents. L’intégration est mise à jour
automatiquement ; ouvrir un nouveau chat pour charger les nouveaux outils.
Une destination locale fermée peut recevoir une mission sans premier chat visible.
Les copies et transferts de conversations conservent leurs exigences de fenêtre.
Les permissions natives des chats ouverts restent inchangées. Vérification périodique tant que le
switcher reste ouvert, même près de l’horloge (5 minutes, intervalle réglable).
Les anciens chats peuvent conserver leur MCP et leur ancien dossier de programme.

Fermer la fenêtre réduit le switcher dans la zone de notification.
Quitter le switcher ne ferme pas les instances Codex.
Codex Microsoft Store est nécessaire aux instances, Codex CLI aux comptes.
Le binaire n'est pas signé. Lire WINDOWS-CONSOLE.md et
WINDOWS-CONSOLE-VALIDATION.md pour les tests et limites.

https://github.com/creezio/codex-account-switcher-windows
'@
[IO.File]::WriteAllText((Join-Path $bin 'LIRE-MOI.txt'),$instructions,[Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination (Join-Path $bin 'LICENSE.txt') -Force
Copy-Item -LiteralPath (Join-Path $repo 'README.md') -Destination $bin -Force
Copy-Item -LiteralPath (Join-Path $repo 'docs\PERFORMANCE-DASHBOARD.md') -Destination $bin -Force
foreach($name in @('CLOUD-NATIVE-PILOT.md','RELEASE-0.17.2.md','RELEASE-0.17.1.md','RELEASE-0.17.md','STARTUP-RECOVERY.md','TRANSFERS.md','THREAD-COPY.md','LOGIN-BROWSER.md','INSTANCE-WIZARD-FIX.md','RESOURCE-LINKS.md','RESOURCE-LINKS-VALIDATION.md','INSTANCE-LIFECYCLE.md','SHARED-PAGES.md','SHARED-PAGES-VALIDATION.md','INSTANCE-RESOURCES.md','TOOL-TUNNEL.md','SIMPLE-INSTANCES.md','UI-VALIDATION.md','UI-REBUILD-PLAN.md','RELAY.md','WINDOWS-CONSOLE.md','WINDOWS-CONSOLE-VALIDATION.md')){Copy-Item -LiteralPath (Join-Path $repo ('docs\'+$name)) -Destination $bin -Force}
$zip=Join-Path $repo 'outputs\CodexAccountSwitcher-0.18.0-beta.1-windows-x64.zip'
# All published runtime assemblies are required. Test helpers and debug symbols are not shipped.
$items=Get-ChildItem -LiteralPath $bin | Where-Object {$_.Name -notmatch '(Smoke|Tests|\.pdb$)' -and $_.Name -ne 'RUNTIME-SHARED.json' } | ForEach-Object FullName
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
# Windows PowerShell Compress-Archive requests write access to DLLs and fails
# when the delivered app is open. Read immutable release files with shared access.
$temporary=$zip+'.tmp'
$stream=[IO.File]::Open($temporary,[IO.FileMode]::Create,[IO.FileAccess]::Write,[IO.FileShare]::None)
$archive=[IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create,$false)
try {
  foreach($item in $items){
    $files=if(Test-Path -LiteralPath $item -PathType Container){Get-ChildItem -LiteralPath $item -Recurse -File}else{Get-Item -LiteralPath $item}
    foreach($file in $files){
      $entry=$archive.CreateEntry($file.FullName.Substring($bin.TrimEnd('\').Length+1).Replace('\','/'),[IO.Compression.CompressionLevel]::Optimal)
      $inputStream=[IO.File]::Open($file.FullName,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
      try{$outputStream=$entry.Open();try{$inputStream.CopyTo($outputStream)}finally{$outputStream.Dispose()}}finally{$inputStream.Dispose()}
    }
  }
}finally{$archive.Dispose();$stream.Dispose()}
$archive=[IO.Compression.ZipFile]::OpenRead($temporary)
try{
  foreach($required in @('CodexAccountSwitcher.exe','CodexAccountSwitcher.dll','coreclr.dll','CreezioRelay.exe','plugins/creezio-relay/.codex-plugin/plugin.json','plugins/creezio-relay/ui/viewer.html')){
    if(-not($archive.Entries | Where-Object {$_.FullName.Replace('\','/') -eq $required})){throw "Fichier absent du ZIP : $required"}
  }
}finally{$archive.Dispose()}
Move-Item -LiteralPath $temporary -Destination $zip -Force
$hash=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $repo 'outputs\SHA256SUMS-0.18.0-beta.1.txt'),$hash+'  '+[IO.Path]::GetFileName($zip)+"`n")
Get-Item -LiteralPath $zip | Select-Object Name,Length
