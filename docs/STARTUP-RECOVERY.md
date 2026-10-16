# Fiabilité des lancements — 0.16.1-beta.1

## Problème corrigé

Un hôte interrompu pouvait laisser `state.json.switcher-tmp`. L’écriture suivante
utilisait le même nom avec `CreateNew` : Windows refusait cette création avec
`IOException / 0x80070050`. L’hôte quittait avant de démarrer Codex, mais son erreur
était masquée ; après 45 secondes, le Switcher affichait encore un démarrage.

Le lancement distingue maintenant une demande envoyée, un hôte effectivement
actif, une fenêtre prête et un échec. Une demande seule ne prouve pas qu’un hôte
s’est déjà lancé ou arrêté. Les diagnostics d’une tentative précédente ne sont
pas réutilisés pour une nouvelle tentative.

## Récupération automatique

Les écritures atomiques utilisent un temporaire court dans le même répertoire,
identifié par le fichier cible, le PID, la date de création du processus et un
identifiant aléatoire. Le nom reste court pour les chemins Windows utilisés par
le moteur .NET Framework. Les octets sont vidés sur disque avant le remplacement
atomique du fichier final.

Avant chaque écriture, les temporaires de cette cible sont récupérés si leur
processus propriétaire a disparu ou si Windows a réutilisé son PID. Un processus
inaccessible est considéré comme potentiellement actif. Un fichier encore ouvert
ou un nom inconnu est conservé. Aucun critère de taille ou d’âge n’est utilisé.
Les fragments incomplets ne remplacent jamais les données validées.

Pour cohabiter avec les anciens moteurs, le nom historique `.switcher-tmp` sert
encore de réservation exclusive pendant le remplacement. Il contient désormais
l’identité du propriétaire. Une réservation abandonnée identifiable est récupérée
automatiquement ; une écriture d’une ancienne version conserve son exclusion.

Les anciens temporaires sans identité sont récupérés uniquement pour les quatre
fichiers de contrôle `state.json`, `launch.json`, `stop.json` et `startup.json`,
dans le dossier de l’instance à lancer, après acquisition de son verrou exclusif.
Un fichier ouvert empêche cette récupération et son erreur Windows est conservée.
Les autres temporaires historiques sans propriétaire vérifiable sont conservés.
Les liens et jonctions sont refusés. Il n’y a pas de nettoyage global des profils,
comptes, bases, historiques de conversations ou fichiers du projet.

Cette récupération a lieu au moment des écritures et des lancements. Aucun
nettoyage périodique supplémentaire n’est installé.

## Diagnostic visible

La fiche de l’instance affiche **Échec du lancement**, l’étape concernée et le
code d’erreur. **Ouvrir Codex** reste disponible pour réessayer. Le diagnostic
reste visible après actualisation et retour sur cette instance.

Chaque hôte enregistre un résultat d’échec indépendant de `state.json`, dans :

```
%LOCALAPPDATA%\Creezio\CodexAccountSwitcher\instances\<identifiant>\startup.json
```

Un seul résultat est conservé par instance. Il contient l’identifiant de tentative,
l’heure, l’étape, le type d’erreur, le HRESULT, le code Windows natif lorsqu’il
existe et le code de sortie disponible. Les erreurs d’activation du paquet Windows
sont également remontées et enregistrées pour la tentative correspondante.
Les messages libres d’exception, arguments de commande, sorties brutes de Codex,
adresses de compte et jetons ne sont pas enregistrés.

Si Codex quitte avant sa première fenêtre, son code de sortie est affiché.
Le marqueur `Desktop bootstrap failed` est conservé comme motif reconnu.
Le message d’attente est réservé à un hôte encore actif pour la tentative courante.

La mise à jour de l’interface peut laisser les anciens hôtes ouverts : leur verrou
reste respecté. Ils adoptent le nouveau code de lancement lors d’une prochaine
ouverture normale de leur instance ; aucun redémarrage collectif n’est effectué.

## Vérification

Les tests couvrent les propriétaires vivants/disparus, PID réutilisé, fichiers
ouverts, écriture concurrente, interruption entre processus, fichiers historiques,
cible protégée en écriture, diagnostic indépendant, codes Windows et erreurs
anciennes. La même suite s’exécute sur .NET Framework 4.8 et .NET 10.

Les tests WPF vérifient le diagnostic visible, sa persistance après sélection et
actualisation, l’isolation entre instances, le verrouillage pendant une tentative
et le retour aux commandes normales après réussite.

La recette native utilise le vrai paquet Codex sur un profil de test séparé :
échec contrôlé du lanceur, récupération de quatre temporaires historiques puis
ouverture et fermeture de la seule instance de test. Les PID et dates de création
des processus préexistants, ainsi que les empreintes de connexion et configuration
principales, sont vérifiés avant et après. Aucun prompt ni compte utilisateur
n’est nécessaire à cette recette.

Commandes reproductibles :

```powershell
.\scripts\test.ps1
dotnet run --project tests\Core.Net10.csproj -c Release -- work\net10-validation
.\scripts\test-desktop.ps1 -BinaryDirectory outputs\v0.16.1-beta.1
# Utiliser exclusivement un répertoire de données de test, jamais le coffre réel.
outputs\v0.16.1-beta.1\CodexAccountSwitcher.exe --instance-test <dossier-de-test>
```

Validation du 13 octobre 2026 : **454 tests** réussis sur chacun des deux moteurs,
**267 tests WPF** et compatibilité croisée réussis. Le vrai hôte sous le paquet
Windows a remonté `FileNotFoundException / 0x80070002`. Une nouvelle tentative a
récupéré les quatre temporaires historiques et ouvert Codex ; la fermeture de la
seule instance de recette a conservé tous les processus préexistants et les
empreintes de connexion/configuration principales.

Les preuves de cette livraison sont conservées localement dans
`work/startup-recovery-*.log`. Les processus du profil de recette sont arrêtés.
Le contrôle automatique a refusé son nettoyage, y compris sans suppression
récursive ; ses fichiers générés (environ 785 Mio) sont donc conservés dans
`work/startup-live`. Le lien présent dans son cache n’a pas été parcouru et son
dossier cible reste intact. Cette limitation du nettoyage de recette ne concerne
pas la récupération des temporaires testée dans le moteur de l’application.
