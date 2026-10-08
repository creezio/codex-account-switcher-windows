# Instances, comptes, plugins et ressources — 0.11.3

## Parcours

1. Ouvrir **Instances**, créer un espace nommé et connecter son compte permanent.
2. Sélectionner l’instance. **Compte** réunit identité, limites, ouverture de Codex
   et vérification de l’intégration.
   **Renommer l’instance** est toujours accessible au-dessus des onglets. Un nom
   vide ou déjà utilisé laisse le formulaire ouvert avec son erreur. Le nouveau
   nom apparaît immédiatement, sans changer le compte, le dossier ou les accès.
   **Vérifier l’intégration** indique sa progression puis un succès daté ou une
   erreur explicite ; le bouton redevient disponible pour réessayer. La vérification
   contrôle et répare le plugin et les skills. Elle ne modifie pas les permissions
   natives des chats et ne recharge pas l’intégration dans un chat déjà ouvert.
3. **Ressources & accès** affiche les ressources du compte. La détection démarre
   à la sélection de l’instance si son inventaire a plus de dix minutes. Le bouton
   **Actualiser** permet de la relancer. Codex propriétaire doit rester ouvert.
4. Dans **Partager avec**, choisir l’instance qui utilisera ces ressources.
5. Rechercher par nom, filtrer par plugin, cocher les ressources et choisir les
   droits. **Enregistrer les accès** applique les changements ensemble. **Annuler**
   restaure les droits enregistrés. Les nouvelles ressources ne sont pas cochées.
6. Pour un autre plugin, cliquer sur **Configurer les actions** sur sa fiche.
   La fenêtre porte le nom du plugin et conserve le propriétaire et le destinataire.
   Ses actions se chargent automatiquement, avec leurs libellés fournisseur et une
   recherche. Cocher les lectures ou les autres actions utiles, puis enregistrer.
   Aucun nom de partage ni sélection Site/Page n’est demandé. Les actions utilisent
   les accès du compte propriétaire ; le switcher n’ajoute pas de filtre par élément
   pour un plugin dont il ne sait pas énumérer les ressources.
7. Dans un nouveau chat de l’instance cliente, demander par exemple :
   « Utilise les ressources de Principal pour lire la Feuille de route ».
   Le skill retrouve le nom et l’identifiant parmi les ressources autorisées.

Les filtres et le changement d’onglet conservent le brouillon. Quitter la page,
changer de destinataire ou d’instance demande de traiter les changements non
enregistrés. Le mode compact remplace la liste latérale d’instances par un
sélecteur. Le bouton d’enregistrement reste visible pendant le défilement.
Les anciens écrans Comptes, Agents, Projets et Outils partagés restent accessibles
dans **Réglages avancés**.

## Ce que la détection sait faire

| Fournisseur | Inventaire | Droit simple « modification » |
| --- | --- | --- |
| Pages / Space | Pages accessibles, noms et droits du compte | Modification textuelle de la Page sélectionnée |
| Sites | Sites du propriétaire et Sites éditables | Métadonnées du Site sélectionné |
| Tableurs, présentations, documents natifs | Signalés si renvoyés par le catalogue Pages | Non partageables avec le transport actuel |
| Autres plugins connectés | Nom du plugin et nombre d’actions | Cases par action, dans une fenêtre propre au plugin |

Il n’existe pas d’énumération universelle des ressources de tous les MCP.
Le catalogue signale explicitement les plugins sans adaptateur, les erreurs et
les résultats partiels. La détection lit les métadonnées, pas le contenu des
documents. Aucun modèle n’est lancé et aucun partage n’est créé à cette étape.

« Lecture et modification » n’autorise pas implicitement la suppression, le
changement de membres, les credentials ou le déploiement. La publication de
nouvelles versions Sites reste indisponible à cause du transfert natif d’archives.
Les appels directs restent limités aux instances locales Windows. Les widgets
et éditeurs natifs ne sont pas transférés. Voir [le contrat du tunnel](TOOL-TUNNEL.md).

## Architecture et migration

- `InstanceResources` sépare inventaire et autorisations. Il utilise le transport
  app-server existant, pagine les catalogues, élimine les doublons et borne le
  nombre de pages, ressources et octets. Les inventaires sont chiffrés par DPAPI
  et attachés à l’identité exacte de l’instance, du compte et du profil.
- `InstanceResourcesView` conserve les choix en brouillon. Une actualisation
  périodique ne remplace pas une sélection en cours. La découverte est annulable.
- `PluginAccess` et `PluginAccessEditor` gèrent les permissions par action dans
  le contexte exact propriétaire / destinataire / plugin. `CatalogPlugin` distingue
  ces règles des sélections de ressources : enregistrer l’une ne révoque pas l’autre.
  Le chargement ne coche rien par défaut et signale les anciennes règles distinctes.
  L’enregistrement vérifie à nouveau les outils disponibles et leur contrat, puis
  applique les choix sous verrou avec une révision contre les conflits concurrents.
  Un titre d’action fourni par le plugin sert à l’affichage ; en son absence le nom
  technique est conservé, sans inventer une interprétation métier.
- Enregistrement : vérification de l’identité, de la fraîcheur et des outils,
  préparation des règles, puis remplacement atomique sous verrou. Une révision
  empêche l’écrasement d’un changement concurrent. Chaque règle a une liste
  exacte d’IDs autorisés (`ResourceValues`) et des noms indicatifs (`ResourceLabels`).
- Les anciennes règles avancées et les autres destinataires sont conservés.
  Une ressource déjà couverte par une règle avancée le signale et renvoie vers
  cette règle. Une détection partielle ne révoque aucun partage. Si des ressources
  déjà partagées manquent au catalogue, actualiser avant de modifier, ou utiliser
  **Tout décocher** pour révoquer les accès gérés par cette page.
- Les appels restent soumis à l’identité du client, au schéma approuvé et à
  l’appartenance de l’ID à la liste autorisée. Un décochage enregistré bloque
  les prochains appels ; il ne peut pas annuler une opération déjà envoyée.
- Aucun compte, secret, chat, Site ou Page n’est déplacé. L’intégration 0.11 est
  installée par le mécanisme existant. Les anciens chats peuvent conserver leur
  ancien MCP : ouvrir un nouveau chat après mise à jour.

## Plan réalisé et recette du 7 octobre 2026

1. Regrouper compte, ressources et activité par instance ; alléger la navigation.
2. Ajouter un catalogue de métadonnées avec états prêts, partiels et indisponibles.
3. Ajouter les cases par ressource et destinataire, les droits et la sauvegarde atomique.
4. Adapter le skill pour résoudre les noms uniquement dans les ressources autorisées.
5. Tester le moteur, les interactions WPF, les petites fenêtres et le transport réel.
6. Livrer un dossier portable séparé, en conservant les processus Codex existants.

Les tests du moteur couvrent pagination, doublons, erreur fournisseur, dérive
d’identité, absence de partage implicite, révocation ciblée, droits minimaux,
noms de ressources, concurrence et préservation des autres destinataires.
La recette WPF couvre la sélection, les brouillons, les filtres, les modes clair
et sombre, le sélecteur compact et l’accès aux actions à 800 × 550.

Recette réelle : détection de 11 plugins connectés, 3 Sites et 1 Page sur un compte
secondaire ; création d’un accès limité à la Page privée de test existante,
lecture de ses métadonnées depuis l’autre compte, puis révocation. Les règles
présentes avant le test sont identiques après le test. Aucune publication ni
modification de contenu n’était nécessaire pour cette recette.

Résultats : **276 tests moteur** réussis sous .NET Framework et **276 sous .NET 10**,
**61 tests WPF**, compatibilité du coffre dans les deux sens, installation et
réparation du plugin, chargement de ses **4 skills et 20 outils**, cycle de vie
des workers et compilation portable sans avertissement.

Correction 0.11.1 : le parcours complet depuis la fiche d’un plugin est testé par
clic jusqu’à l’enregistrement, avec vérification du propriétaire, du destinataire,
de la liste exclusive des actions, des filtres et du maintien du brouillon des
ressources. Le formulaire avancé ne propose plus systématiquement Site/Page.
Recette réelle en lecture seule : un plugin métier expose 57 actions dont 25 lectures,
toutes avec un libellé fournisseur. Aucune action métier ni permission réelle n’a
été modifiée pendant cette vérification.

Recette 0.11.1 : **286 tests moteur par runtime** et **69 tests WPF**, dont un
enregistrement de permission par clic depuis la fiche plugin. Les règles de
ressources, de plugins et les anciennes règles avancées sont testées séparément.

Correction 0.11.2 : **89 tests WPF**. Les nouveaux parcours testent le renommage
par clic (validation, annulation, persistance, identité et compte conservés), son
accès depuis les trois onglets et la conservation des brouillons. Les contrôles
d’intégration testent une réponse lente, le double clic, une intégration déjà saine,
l’erreur persistée par la maintenance, une exception et le changement d’instance
pendant le contrôle. Le résultat reste attaché à l’instance vérifiée ; la fiche
conserve son défilement. Les tests isolés utilisent des comptes fictifs.

Correction 0.11.3 : un partage créé par l’interface .NET 10 pouvait produire une
empreinte différente de celle recalculée par le worker .NET Framework à cause de
l’échappement JSON. La lecture fonctionnait mais certaines écritures étaient
refusées avant tout appel fournisseur, même avec les mêmes schémas et les mêmes
droits. Le worker recalcule les deux empreintes depuis les définitions enregistrée
et courante dans le même runtime. Les vrais changements de schéma, description,
connecteur ou annotations restent bloqués. Aucun partage n’est élargi, réenregistré
ou rejoué automatiquement. Les noms retournés au chat suivent les renommages.

L’onglet Activité présente l’opération et son erreur, avec l’heure locale.
Le bouton d’état inerte devient **Afficher la fenêtre** pour toute instance ouverte,
y compris la principale. La sélection utilise le PID et sa date de démarrage ;
la fenêtre principale exclut les instances gérées et refuse un choix ambigu.
Une instance fermée conserve **Ouvrir Codex** ; la principale utilise l’activation
Windows normale. Si Windows refuse le premier plan, l’icône est signalée dans la
barre des tâches et un message l’explique. Les fenêtres Codex ne sont pas fermées.

Recette 0.11.3 : 93 tests WPF ; échange de contrats comportant accents, caractères
spéciaux et emoji entre les deux runtimes, avec reproduction des empreintes
différentes et vérification du refus d’une modification réelle. Résolution et mise
au premier plan réussies sur les deux fenêtres Codex existantes. Le diagnostic
réel compare uniquement les métadonnées : aucun texte de Page n’est modifié.
