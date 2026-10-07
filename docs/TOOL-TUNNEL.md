# Appels d’outils entre instances — plan et contrat

Depuis la version 0.12.2, les réponses utilisent des liens nommés qui ouvrent
la ressource dans le Codex propriétaire, sans éditeur supplémentaire ni prompt.
Voir [les liens interinstances](RESOURCE-LINKS.md). Les cartes natives restent
liées au compte connecté et les anciennes cartes ne sont pas réécrites.

Depuis la version 0.11, le parcours principal est **Instances → Ressources & accès** :
détection des Sites et Pages, choix du destinataire et cases à cocher par nom.
Les instructions de sélection manuelle d’outils ci-dessous restent valables pour
les réglages avancés. Voir [le guide des ressources](INSTANCE-RESOURCES.md).

## Étapes

1. Vérifier `mcpServer/tool/call` sans tour de modèle. Lecture Sites et Pages
   réussie sur les deux profils le 7 octobre 2026 avec Codex Desktop 0.160.1.
2. Ajouter un transport borné : profil propriétaire, contexte éphémère, aucun
   `turn/start`, aucune copie d’identifiants, arrêt des seuls processus créés.
3. Configurer les outils partagés depuis le switcher : instance propriétaire,
   instances clientes, opérations sélectionnées, restriction de ressource.
4. Exposer découverte, appel et relecture du résultat dans le plugin installé.
5. Tester refus, révocation, changements de compte, erreurs, appels concurrents,
   résultat incertain et non-répétition ; puis lecture et écriture réelles de test.
6. Compiler, vérifier l’interface, préparer la livraison Windows et documenter
   les limites effectivement observées.

## Contrat

Le transport utilise le serveur officiel livré avec Codex, sous le profil de
l’instance propriétaire. Codex doit rester ouvert. Un contexte technique
éphémère permet l’appel MCP ; il ne reçoit aucun prompt et ne lance aucun modèle.
Les conversations existantes ne sont ni reprises ni modifiées.

Aucun outil n’est partagé par défaut. Un partage contient une liste exacte
d’outils et des comptes clients autorisés. Les schémas et descriptions proviennent
de l’inventaire réel et sont revérifiés avant l’exécution. Une restriction de
ressource impose un argument racine exact (par exemple `project_id` ou `page_id`).
Les outils sans cet argument ne sont alors pas autorisés. Le partage est local à
ce PC ; les invitations de PC distants ne donnent aucun droit sur ces outils.

Chaque appel porte un identifiant stable et une empreinte de son contenu. Le
résultat est conservé chiffré avec DPAPI et relu uniquement par le chat émetteur.
Les champs d'identification connus (`token`, `access_token`, `password`, etc.)
sont masqués, y compris dans les copies textuelles JSON. L'original sensible
est fourni une seule fois avec `ephemeralResult`, uniquement en mémoire dans
le processus appelant. Il ne faut pas le journaliser. Les autres réponses
privées restent chiffrées ; l'interface ne lit que leurs métadonnées.
Une coupure après envoi produit un résultat incertain, jamais un nouvel envoi
automatique. Les demandes interactives du connecteur sont refusées par le
transport et signalées : aucune approbation Codex n’est accordée implicitement.

La configuration du partage et les paramètres du connecteur restent deux
contrôles distincts. L’accès complet aux commandes locales n’accorde pas l’accès
à une Page ou un Site. Le transport vérifie l'état effectif installé, activé et
appelable des apps, et ne modifie pas les permissions des plugins. Les comptes
et les profils sont revérifiés avant l'appel et avant de retourner la réponse.
Les appels d'un même compte sont sérialisés ; les changements de contrat
demandent de recharger le partage. DPAPI protège le stockage sous l'utilisateur
Windows courant, pas contre un programme exécuté avec les mêmes droits Windows.

## Parcours utilisateur

1. Ouvrir les deux instances nommées, chacune avec son compte permanent.
2. Dans **Outils partagés**, cliquer **Partager des outils**, choisir le compte
   propriétaire puis **Charger les plugins**.
3. Cocher les opérations autorisées et les instances clientes. Si le partage
   concerne une ressource précise, choisir Site/Page et saisir son identifiant
   exact. Le périmètre retire les outils qui ne possèdent pas cet argument.
4. Enregistrer. Dans un nouveau chat du client, demander par exemple :
   « Utilise les outils Pages de Principal pour modifier cette page ».
5. Consulter **Historique des appels** pour voir réponse reçue, erreur,
   blocage avant envoi ou résultat incertain. Désactiver le partage arrête les
   prochains départs ; une opération déjà transmise n'est pas annulée chez le
   fournisseur.

Les sélections sont conservées en changeant de filtre ou de plugin, avec un
compteur total visible. Aucun outil n'est coché par défaut. La mise à jour du
plugin suit l'installation et la vérification périodique existantes ; les chats
déjà ouverts peuvent conserver leur ancien plugin et nécessiter un nouveau chat.

## Limites vérifiées de 0.10.0-beta.1

- Le transport direct de cette version Codex ne transforme pas un chemin local
  en pièce jointe native. Un test réel Sites a retourné `INVALID_ARGUMENT` pour
  `archive` : le service attendait une référence de fichier structurée. Le test
  de publication sur envoi des sources a retourné
  `publish_on_push_accepted: false` sur le compte utilisé. **Aucune publication
  de nouvelle version Sites n'est donc validée ni annoncée comme disponible.**
  Les deux outils de sauvegarde sont grisés et bloqués avant envoi. Le tunnel
  n'omet pas silencieusement l'archive et ne fabrique pas de référence de fichier.
- Les opérations Sites sans fichier, Pages textuelles et autres outils MCP
  compatibles restent accessibles selon les droits sélectionnés. Cela ne
  garantit pas tous les workflows de tous les plugins. Les widgets, éditeurs
  d'artefacts et téléchargements natifs ne sont pas transportés.
- Une interaction native est signalée et refusée, jamais approuvée implicitement.
  Effectuer l'opération dans Codex si cette interaction est nécessaire.
- Délai de réponse de 50 secondes par RPC ; arguments limités à 200 Ko, résultats
  à 3 Mo et pages de lecture à 24 000 caractères. Un délai dépassé après envoi
  exige de vérifier la ressource ; aucune répétition automatique d'écriture.
- Partage local Windows uniquement. Le relais de missions et les invitations
  distantes conservent leur fonctionnement distinct. Aucun prompt de secours
  n'est envoyé sans demande de l'utilisateur.

## Recette du 7 octobre 2026

- Inventaire réel via Codex Desktop 0.160.1 : 1 122 outils sur le profil source,
  204 sur le profil propriétaire utilisé pour les essais.
- Création d'une Page privée sur le compte propriétaire, modification depuis
  le chat source, réponse structurée et relecture exacte du texte accentué.
  Le même identifiant d'appel rejoué n'a pas déclenché de deuxième modification.
- Création puis lecture d'un Site privé du propriétaire. Préparation et envoi
  des sources avec le script Sites officiel réussis. Publication non aboutie
  pour la limitation d'archive décrite ci-dessus ; aucun déploiement revendiqué.
- Aucun `turn/start`, aucun prompt et aucune conversation utilisateur créée
  dans l'instance propriétaire par ces essais. Le contexte app-server est
  éphémère. Les partages de recette sont désactivés après les vérifications.
- Tests automatisés : 260 tests moteur sous .NET Framework, les mêmes 260 sous
  .NET 10, et 49 tests WPF ; compatibilité DPAPI/JSON et contrôles de compte,
  ressource, révocation, schéma, sérialisation, coupure, anti-répétition,
  masquage des identifiants, refus des fichiers et navigation.
- Chargement réel des quatre skills et vingt outils MCP par Codex dans un profil
  de test isolé ; installation idempotente, réparation d'un skill altéré et
  respect d'un plugin désactivé par l'utilisateur.

Les ressources privées de recette et leurs identifiants sont conservés
localement, jamais ajoutés à ce dépôt public.

### Reproduire

```powershell
./scripts/test.ps1 -TestDirectory work/tunnel-tests
dotnet run --project tests/Core.Net10.csproj -c Release -- work/net10-validation
./scripts/build-desktop.ps1 -OutputDirectory work/desktop-bin
./scripts/test-desktop.ps1 -BinaryDirectory work/desktop-bin
./scripts/integration-smoke.ps1 -BinaryDirectory work/desktop-bin
```

`scripts/tool-tunnel-smoke.ps1 -Instance <ID>` inspecte le catalogue réel.
Ses phases `pages-create`, `pages-edit`, `site-create`, `site-read` et `finish`
sont des essais explicites sur les comptes connectés, hors CI. `finish`
désactive les partages de recette et masque les identifiants des résultats de
recette antérieurs ; elle préserve les ressources et les reçus.

Référence : https://learn.chatgpt.com/docs/app-server
