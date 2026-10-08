# Instances nommées — 0.9.0-beta.2

## Utilisation

1. Dans **Instances → Créer une instance**, donnez un nom et choisissez un compte
   enregistré ou **Connecter un nouveau compte**. Terminez la connexion officielle
   dans le navigateur, puis utilisez **Préparer et ouvrir**.
2. Le switcher associe définitivement ce compte, installe son plugin et ses skills,
   puis ouvre son espace Codex indépendant. Une instance entièrement neuve doit
   recevoir un premier message dans Codex pour disposer d'une conversation native.
3. Dans un nouveau chat de votre instance de travail, demandez :
   **« Analyse ces fichiers et délègue la relecture à Léa. Rapporte-moi son résultat. »**
   Utilisez le nom exact de votre instance. Le skill connecte le chat, choisit
   l'instance, crée une mission durable et restitue la réponse dans le chat source.

Les missions restent visibles dans **Tâches** et dans les fenêtres Codex. Les
fichiers d'un même dossier Windows sont accessibles aux deux instances selon leurs
permissions. Aucun fichier n'est dupliqué pour déléguer. Évitez de modifier les
mêmes fichiers pendant que l'autre agent travaille dessus.

## Ce qui est automatique

- Installation du plugin et de ses skills avant la première ouverture guidée.
- Vérification au démarrage, puis toutes les 5 minutes par défaut ; intervalle
  de 1 à 120 minutes dans **Paramètres → Collaboration**.
- Contrôle de la déclaration Codex, de la version, du serveur MCP et des fichiers
  sources et installés. Une installation manquante ou endommagée est réparée dans
  une nouvelle version de cache, sans modifier les fichiers d'un chat ouvert.
- Connexion du chat émetteur par le skill ; découverte du canal Windows natif et
  d'une conversation réelle de l'instance destinataire au moment de la délégation.
- Retour du résultat au chat source par défaut. Une tâche en attente n'est pas
  présentée comme terminée ; une réponse perdue n'entraîne pas un renvoi aveugle.

Cette supervision fonctionne tant que le switcher reste ouvert, même réduit près
de l'horloge. **Quitter** arrête cette vérification périodique. **Vérifier et
réparer** lance un contrôle immédiat. Une désactivation volontaire du plugin dans
Codex est signalée ; ce bouton permet de le réactiver explicitement.

## Comptes et migration

Une instance conserve son compte permanent après fermeture, renommage, archivage
et restauration. Impossible de changer son compte, de supprimer son association
ou de retirer du coffre un compte encore associé. Un même compte peut être utilisé
dans plusieurs instances ; leurs limites d'utilisation restent partagées.

Au premier démarrage 0.9, une ancienne instance est liée au compte **réellement
connecté** dans son profil, même si son ancien enregistrement était obsolète. La
migration ne réécrit pas ses fichiers d'authentification ni ses conversations. Une
fois le lien permanent enregistré, une connexion manuelle à un compte différent
est signalée et bloque la délégation ; elle ne provoque pas une nouvelle liaison.

## Réglages avancés et limites

**Réglages avancés** révèle les anciennes pages Agents et Projets. Les configurations
existantes sont conservées. Un dossier possédant une politique avancée ou un profil
d'agent restrictif continue à utiliser cette politique via `list_agents` et
`submit_job` ; la délégation par nom ne crée pas de raccourci autour de ses droits.
Sans politique avancée, la mission par nom exige une demande explicite de l'utilisateur.
L'application ne décide pas seule quels travaux déléguer.

Les permissions natives de chaque chat, les confirmations et les droits sur les
plugins privés restent ceux de Codex. La création d'un nouveau chat passe par une
vérification de ses permissions effectives. Une intervention peut être nécessaire
si Codex ne reprend pas le mode attendu. Le switcher ne l'approuve pas à votre place.

Un premier chat reste nécessaire dans un profil entièrement neuf. Une instance
destinataire doit être ouverte. Une session habituelle relancée se reconnecte en
utilisant le skill depuis l'un de ses chats ; les instances gérées retrouvent leur
canal depuis leur hôte. Après mise à jour du plugin, les anciens chats peuvent garder
les anciens outils : ouvrez un nouveau chat. La disponibilité d'une ressource privée
n'est jamais déduite du seul nom d'une instance ou de l'installation d'un plugin.

Le parcours par nom concerne les instances **locales Windows**. Le partage entre PC
reste le parcours séparé de la version 0.8, avec invitations et droits explicites.
Il ne synchronise pas automatiquement les fichiers. Pas de prise en charge de Cursor
IDE, Linux ou macOS dans cette livraison.

## Plan réalisé et validation

1. Compte permanent : garde-fous du moteur, adoption des anciennes connexions,
   détection des changements externes et tests de conservation des fichiers.
2. Installation : création guidée, contrôle périodique configurable, diagnostic
   daté et réparation idempotente des fichiers du plugin.
3. Délégation : `list_instances`, `delegate_to_instance`, connexion automatique du
   chat, résolution exacte des noms et identifiant stable, file et retour existants.
4. Interface : formulaire nom/compte/action unique, fiche de diagnostic, navigation
   simple et accès explicite aux anciens réglages.
5. Recette : 230 tests du moteur sous .NET Framework, 230 sous .NET 10 ; 43 contrôles
   WPF et compatibilité DPAPI/JSON dans les deux sens. Le chargeur Codex réel confirme
   les skills et les nouveaux outils MCP. Un cache de skill endommagé volontairement
   est réparé ; un plugin volontairement désactivé reste désactivé jusqu'à réparation.
6. Échange natif vérifié le 7 octobre 2026 entre deux comptes distincts : mission
   envoyée à l'instance **Nouvel espace**, résultat `SWITCHER_NAMED_OK` récupéré et
   retour livré au chat source. Aucun outil de fichier ni publication dans ce test.

Preuve locale : mission `fb44a9ed28d445d2a2a89c1a45de6c1d`, source
`01a11519-ae17-7851-96ca-2a73ec19245a`, destinataire
`01a11519-b90a-7652-977a-70b86ceeaf20`. Le test n'établit pas l'accès à tous les
plugins privés ni une compatibilité future avec toutes les versions de Codex :
l'adaptateur utilise le protocole local de l'application, sensible aux mises à jour.

La connexion OAuth interactive d'un nouveau compte réutilise le parcours officiel
existant ; cette recette a utilisé les comptes déjà connectés, sans nouvelle connexion.
