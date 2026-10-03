# Interface 0.7.0-beta.1 — réalisation et recette

## Ce qui change

La fenêtre principale utilise WPF sur .NET 10. Les sept rubriques sont Accueil,
Comptes, Instances, Agents, Projets, Tâches et Paramètres. La sélection de la barre
latérale représente la page affichée et revient à la page précédente si un
abandon de brouillon est refusé. Les chargements sont suivis par page ; une
connexion ou une opération en attente ne désactive pas la navigation.

Les composants communs définissent les espacements, champs, boutons, cartes,
sélections et erreurs. Les listes et fiches remplacent les grands écrans de
boutons. Les actions secondaires, journaux et options avancées restent accessibles
sans occuper la vue principale. Les thèmes clair, sombre et système sont inclus.
Les couleurs de contraste élevé suivent Windows ; ce mode demande encore une
recette manuelle avec un lecteur d'écran.

Les limites et réinitialisations se règlent depuis la fiche du compte. L'action
manuelle de réinitialisation ne sauvegarde pas les champs modifiés dans le
formulaire. Les brouillons de configuration sont protégés. Les éditeurs de règles
et de ressources distinguent suppression préparée et enregistrement. L'import
et les modèles montrent les changements avant application. Les modèles restent
facultatifs, sans compte, fournisseur ni usage privé prédéfini.

Les tâches proposent recherche, quatre filtres, pagination de 50 lignes, résultat,
prochaine action, reprise et contrôle du relais. La page suivante dépend du
résultat filtré. Une nouvelle recherche revient à la première page. L'accueil
inclut les échecs terminés qui demandent une intervention.

Les réglages regroupent supervision, capacité, intégration, diagnostic, versions,
restauration, espaces Git et examen des temporaires appartenant au switcher.
Le nettoyage exige une sélection et vérifie à nouveau les propriétaires des
processus ; il ne propose pas les profils Codex ni leurs conversations.

## Architecture et migration

- Interface et services partagés : `desktop/`, WPF et .NET 10.
- Relais indépendant : `CreezioRelay.exe`, .NET Framework 4.8. Son protocole et
  celui du plugin restent en version 0.6 ; le numéro du moteur n'est pas celui
  de la nouvelle interface.
- Le coffre DPAPI, les instances et les fichiers du relais restent à leur
  emplacement existant. Pas de recopie de profil ni de conversion destructive.
- Les objets JSON non typés conservent leurs dictionnaires et tableaux. Les
  reprises comparent aussi le contrat persistant lorsque les deux runtimes
  produisent des échappements JSON différents.
- Le lanceur utilise l'apphost `.exe` de .NET 10 pour démarrer une instance,
  et non la DLL d'assemblage.
- La livraison Windows x64 inclut le runtime .NET 10. Le SDK sert uniquement
  à compiler ; il n'est pas requis sur le PC de l'utilisateur.

Fermer d'abord l'ancien switcher par son menu Quitter puis lancer l'exécutable
dans le nouveau dossier. Les instances Codex peuvent rester ouvertes. Ne pas
supprimer une ancienne livraison utilisée par un hôte d'instance ou un serveur
MCP. Une intégration déjà connectée garde son fonctionnement ; pour déplacer
son chemin, la réinstaller sur le profil puis ouvrir un nouveau chat.

## Vérifications exécutées le 3 octobre 2026

| Vérification | Résultat |
| --- | --- |
| Sécurité, stockage, quotas, instances, routage et permissions sur Framework | 183 tests réussis |
| Même suite contre les services .NET 10 | 183 tests réussis |
| Parcours de la fenêtre WPF avec comptes fictifs | 33 assertions réussies |
| Interface → Framework → interface | Coffre de 4 comptes, configuration et 57 tâches relus ; modifications relues dans le sens inverse |
| Moteur détaché | Démarrage, singleton, pause, redémarrage et arrêt coopératif validés |
| Supervision indépendante | Démarrage, singleton et arrêt validés sans compte réel |
| Instance Codex réelle isolée | Ouverte, retrouvée par un autre gestionnaire et fermée précisément ; processus, authentification et configuration d'origine inchangés |
| Échange réel entre deux comptes | Soumission .NET 10, exécution et retour par le moteur existant ; `ACK-UI-V07`, résultat `succeeded`, retour `delivered`, réception confirmée dans le chat source |

Les tests WPF déclenchent les actions de contrôles réels : navigation, pagination,
enregistrement d'un projet, erreur de sauvegarde, refus puis acceptation
d'abandon, navigation pendant une opération en attente, recherche rapide,
sélection conservée, modèle explicite et affichage d'un échec. Une erreur de test
renvoie un code non nul et le script contrôle également le rapport écrit.

Les captures des sept pages, des thèmes et d'une fenêtre de 800 × 550 unités
logiques sont produites avec des données fictives. Des rendus à 125, 150 et 200 %
sont générés. Ces rendus ne remplacent pas une vérification physique de changement
de moniteur ni une qualification au lecteur d'écran. Aucune réinitialisation
réelle et aucun achat de crédit n'ont été utilisés pour cette recette.

## Reproduire

```powershell
.\scripts\build-desktop.ps1 -Portable -OutputDirectory outputs\v0.7.0
.\scripts\test.ps1 -TestDirectory work\framework-validation
dotnet run --project tests\Core.Net10.csproj -c Release -- work\net10-validation
.\scripts\test-desktop.ps1 -BinaryDirectory outputs\v0.7.0
.\scripts\worker-smoke.ps1 -BinaryDirectory outputs\v0.7.0
.\scripts\package-desktop.ps1 -SkipBuild
```

Les tests hors ligne n'utilisent pas les comptes de l'utilisateur. Les tests
réels d'instance et d'échange sont facultatifs et exigent un Codex installé et
des participants connectés et autorisés. La CI effectue les tests hors ligne,
l'interopérabilité et le contrôle de contenu du ZIP à chaque push.

La bêta reste non signée, en français, pour Windows x64. Les intégrations Codex
dépendent des interfaces locales disponibles ; une mise à jour de Codex peut
nécessiter une adaptation. Les permissions restent propres à chaque chat et le
relais ne valide aucune approbation à la place de l'utilisateur.
