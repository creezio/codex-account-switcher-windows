# Copier une conversation vers une autre instance

## Parcours

1. Garder les deux instances Codex ouvertes. Terminer ou arrêter le tour source.
2. Dans **Instances**, sélectionner la source puis **Conversations et projets… → Copier une conversation**.
3. Rechercher et choisir la conversation, puis l’instance destinataire.
4. Adapter éventuellement le titre et cliquer **Copier et ouvrir dans Codex**.
5. Continuer dans la copie avec le compte destinataire.

L’original reste à sa place. Le nouvel identifiant appartient au profil
destinataire. Le dossier de travail est le même : modifier un fichier depuis
la copie modifie aussi celui que voit l’original. Aucun checkout n’est dupliqué.

**Copies récentes** permet de rouvrir le résultat. Si une réponse a été perdue,
**Vérifier cette copie** recherche l’opération existante ; il ne relance pas
aveuglément une deuxième copie. Un échec d’ouverture conserve la copie créée.

Les autorisations Pages, Sites, plugins et autres services restent attachées
à chaque compte. Copier un historique n’accorde aucun droit sur ces ressources.
Les partages du tunnel se configurent séparément dans **Ressources & accès**.

## Implémentation et limites

- Conversations locales Codex de ce PC, avec journal JSONL dans `sessions` ou
  `archived_sessions` du profil source. La sélection affiche les conversations
  non archivées. Réactiver une archive dans Codex pour la sélectionner.
- Les chats ChatGPT/cloud, les chemins de projet absents et les formats inconnus
  sont refusés. Il ne s’agit pas d’une synchronisation entre conversations.
- Les formats `legacy` et `paginated` sont lus. Le second est rejoué à partir
  de son journal complet en mode `legacy`, dans un instantané temporaire privé.
  Les bases SQLite et fichiers d’authentification ne sont jamais copiés.
- Les messages paginés récents sont adaptés au journal de reprise pour
  conserver leurs textes visibles, images locales et images intégrées en données PNG,
  JPEG, WebP ou GIF. Les images distantes et les niveaux de détail non représentables
  restent refusés. Le processus d’import est ensuite fermé,
  et la commande native `migrate-rollouts --apply --thread <nouvel-id>` prépare
  le format paginé attendu par Codex. Elle ne vise que la copie. Un verrou,
  un outil absent ou une conversion refusée laisse l’opération à vérifier.
- La liste utilise l’index natif (`useStateDbOnly`) : le parcours de réparation
  des anciens journaux peut omettre les chats créés par les outils Codex.
- Le Switcher appelle `thread/fork` via un app-server éphémère du profil
  destinataire. L’option de chemin de journal est une API Codex **instable**.
  Une mise à jour de Codex peut demander une adaptation du Switcher.
- Les noms de fichiers et comptes sont vérifiés ; une cible située hors du
  profil, un lien/jonction, une identité changée, un journal incomplet ou un
  tour en cours bloquent l’import.
- Les permissions ne sont pas héritées de la source. Une session éphémère
  sans prompt résout les valeurs natives de destination. Elles sont appliquées
  au fork, puis vérifiées après reprise. Aucun choix d’Accès complet n’est fait
  par le Switcher et aucune demande d’approbation n’est acceptée à sa place.
- L’objectif autonome hérité est suspendu puis retiré de la copie. L’import
  ne lance aucun tour de modèle et n’exécute aucun ancien appel d’outil.
- Les contenus de modèle, compactages et messages visibles sont comparés
  par empreinte SHA-256, dans l’ordre, avant conversion. Après conversion,
  le contexte de modèle est comparé de nouveau et chaque message de conversation
  est recherché dans toutes les pages de `thread/items/list`, l’API de
  l’interface native. Les cartes techniques et de raisonnement peuvent être
  présentées différemment par le convertisseur Codex. Seuls les identifiants de messages
  régénérés et les champs de pièces jointes vides ajoutés par Codex sont
  normalisés. Les identifiants d’appels d’outils restent vérifiés. Les images
  locales sont comparées par chemin et SHA-256 du fichier, dans l’ordre.
  Les images intégrées sont comparées par format et SHA-256 des octets décodés.
  Les pages de lecture contiennent dix éléments pour éviter que les anciennes
  sorties d’images dépassent la taille de réponse du service natif.
- Les anciennes annotations `executed_tool_calls` retirées par Codex sont adaptées
  dans le seul instantané privé. Chaque annotation doit correspondre exactement
  à un appel réel conservé : identifiant de l’appel, nom (avec son espace de noms)
  et arguments. Les annotations incomplètes ou sans correspondance bloquent
  l’import. Les appels, résultats, textes et autres métadonnées restent conservés.
  Les valeurs vides par défaut ajoutées par Codex aux anciens compactages sont
  préparées explicitement ; aucun contenu de compactage existant n’est remplacé.
- Certains anciens forks combinent un affichage partiel dans le journal et
  l’affichage complet dans SQLite. L’instantané privé remplace alors uniquement
  ses événements d’affichage par les messages de l’API native, avec leur ordre,
  images et regroupement par tour. Le contexte modèle doit rester identique et
  les messages source sont relus avant le fork. Aucun journal source n’est réparé
  ni réécrit. Une collision avec un fichier temporaire préexistant le préserve.
- À la reprise d’un tour interrompu, Codex peut ajouter son avertissement
  `turn_aborted`. L’import accepte uniquement son texte et sa balise native
  exacts, en dernière position du contexte, suivis de l’événement d’interruption.
  Le reste de l’historique doit correspondre intégralement. Cet avertissement
  est conservé et inclus dans la vérification après migration ; aucun autre
  message développeur n’est exclu de la comparaison.
- Une copie dont le contenu diffère est conservée pour examen et signalée
  comme non vérifiée ; elle n’est pas ouverte automatiquement.
- Limites actuelles : journal de 512 Mio et élément JSON de 32 Mio maximum.
  Cette limite JSON plus grande est réservée à la lecture des journaux d’import ;
  les réglages et requêtes du relais conservent leur limite habituelle.
  Le convertisseur Codex qualifié omet les lignes de 16 Mio ou plus. Dans
  l’instantané privé, les PNG encodés de ces lignes sont recompressés sans perte :
  données raster décompressées, filtrage et tous les autres blocs PNG doivent
  rester identiques. Aucun pixel, texte ou métadonnée n’est retiré et les fichiers
  d’image d’origine restent inchangés. L’historique encodé ainsi préparé est
  ensuite comparé intégralement au résultat natif par SHA-256. Si une ligne
  reste trop grande, l’import est refusé avant création de la conversation.
  Les messages paginés multimodaux dont l’affichage n’est pas pris en charge
  sont refusés avant création ; ils ne sont pas silencieusement amputés.
  Les images locales, anciennes ou paginées, doivent encore exister. Les fichiers restent
  référencés sur ce PC, sans transport ni réparation automatique de pièces
  jointes. Des pièces jointes cloud peuvent rester inaccessibles à la cible.
- Le journal d’opération est chiffré avec Windows DPAPI. L’instantané de
  préparation est supprimé à la fin. Un arrêt brutal peut laisser cet
  instantané dans `relay/thread-copies` ; il est remplacé lors d’une nouvelle
  préparation du même identifiant, jamais réutilisé comme source fiable.
- Une réponse perdue est retrouvée grâce à son marqueur dans les métadonnées
  du premier enregistrement du journal cible. La recherche est bornée à
  2 000 conversations récentes. Un résultat ambigu reste à vérifier.

## Plan réalisé et recette

1. Vérification du format et du fork natif dans deux profils sans compte.
2. Service de copie, identité de compte, validation d’historique et reprise
   idempotente après perte de réponse.
3. Formulaire dans la fiche d’instance, recherche, pagination, destinataire,
   titre, progression et copies récentes.
4. Tests moteur sur Framework et .NET 10, tests WPF et recette native.

La recette native reproductible est disponible dans
`scripts/test-thread-copy-native.ps1 -CodexExecutable <chemin-codex.exe>`.
Elle utilise des échanges **fictifs**, ne se connecte à aucun compte et ne
lance aucun modèle. Elle vérifie les deux formats, le nouvel identifiant,
les messages relus par Codex, le contenu complet, la récupération et les
permissions propres à la destination, y compris avec une source plus permissive.

Codex qualifié : `0.162.0-alpha.17.2`, Windows, le 12 octobre 2026.
Validation locale : **372 tests moteur** sur chacun des runtimes Framework et
.NET 10, **153 tests WPF**, compatibilité des données entre runtimes et deux
recettes natives (ancien journal et messages paginés récents). Compilation sans
avertissement. Les captures WPF contrôlent aussi le formulaire en largeur réduite.
La recette réelle autorisée entre deux comptes a également réussi : un nouveau
chat contenant uniquement un code fictif a été copié vers l’autre instance.
La copie s’ouvre par l’outil natif, sa réponse initiale est relue dans l’historique
de l’interface, puis elle restitue le code lors d’un nouveau tour sans qu’il soit
fourni de nouveau. Aucun historique existant de l’utilisateur n’a été transféré.
