# Console Windows 0.8 — messages, partage et assistance

Cette version conserve l'interface WPF. Codex desktop doit être ouvert sur chaque
PC concerné. Elle ne lance pas un agent Codex autonome à la place du chat visible.
Cursor IDE, Linux et macOS ne sont pas pris en charge.

## Envoyer un prompt depuis le switcher

1. Connecter le canal de l'instance dans **Agents**.
2. Ouvrir **Tâches → Envoyer un message**.
3. Choisir l'instance, une nouvelle conversation ou un chat existant. La liste
   propose les conversations récentes ; un identifiant peut aussi être collé.
4. Renseigner l'objet, le message et l'action autorisée : lecture, modification
   de fichiers ou action externe. Le projet configuré est facultatif.
5. Envoyer, puis suivre la réponse dans **Tâches** et dans la fenêtre Codex.

Un chat occupé attend. Un nouveau chat reçoit d'abord un message de préparation ;
le travail part après contrôle de ses permissions effectives. Une approbation
native reste à traiter dans Codex. Une livraison incertaine n'est pas rejouée
automatiquement. Ces appels à l'application desktop dépendent de son protocole
local et devront être requalifiés lors de ses évolutions.

## Associer deux PC Windows

Sur le PC propriétaire :

1. Dans **PC distants → Partager ce PC**, activer l'écoute sur une adresse du
   réseau privé ou du VPN. `127.0.0.1` accepte seulement une connexion locale ou
   un tunnel configuré séparément. Port initial : `17381`.
2. Créer une invitation pour un canal. Le chat de connexion est partagé ; ajouter
   explicitement les autres identifiants autorisés. Choisir les droits de lecture,
   d'envoi, de création et/ou de réponse aux demandes d'assistance.
3. Transmettre l'invitation uniquement à la personne autorisée. Elle contient un
   secret d'association valable de 1 à 30 jours. Aucun identifiant de connexion
   au compte Codex n'est exporté.

Sur le PC du responsable :

1. **Associer un PC**, puis coller l'invitation reçue du propriétaire.
2. **Connecter un agent partagé** pour l'utiliser depuis Tâches ou Assistance.
3. **Consulter les conversations** pour lire les messages et réponses des chats
   partagés. L'écran affiche les derniers huit tours et s'actualise sur demande.
   Les sorties d'outils ne sont pas exposées.

TLS 1.2 chiffre la connexion ; l'empreinte SHA-256 du certificat est comparée à
celle de l'invitation. Les données locales sont protégées avec DPAPI pour le compte
Windows. La révocation bloque les prochaines requêtes, sans annuler une tâche
déjà démarrée. Désactiver le partage arrête l'écouteur, qui peut sinon rester actif
après fermeture de la fenêtre du switcher. Aucun port de pare-feu n'est ouvert
automatiquement ; le routage réseau/VPN reste à configurer par l'administrateur.

Les fichiers ne sont **pas synchronisés**. Un dossier de même nom sur deux PC
ne contient pas nécessairement les mêmes fichiers. Préparer la même révision de
chaque côté ; les manifestes SHA-256 fournis sont vérifiés sur les deux PC. Sans
dossier local associé, le switcher crée uniquement un dossier de correspondance
vide. Le transport ne fournit ni terminal distant ni exécution arbitraire d'outils.

## Demander et fournir de l'assistance

- **Assistance → Configurer les règles** : sélectionner un canal et rédiger les
  critères propres au projet. Aucun métier, compte ou fournisseur n'est imposé.
- Installer l'intégration actualisée depuis **Agents** ; utiliser un nouveau chat
  pour charger les trois skills et quatorze outils MCP. Le skill `request-assistance`
  lit les règles, lie la session au chat et enregistre une question persistante.
- Le responsable consulte la demande dans **Assistance**, peut demander une
  analyse à son propre agent, puis choisit explicitement la réponse à transmettre.
  L'analyse ouvre un chat visible en lecture seule ; elle n'est pas renvoyée
  automatiquement au client.
- La réponse reprend le chat source lorsqu'il est disponible. L'envoi est suivi
  dans **Tâches**, avec les contrôles de permissions et d'identité habituels.
- Les demandes des canaux distants associés sont consultées toutes les minutes
  tant que le switcher est ouvert. Les notifications utilisent le réglage global
  du switcher. Une machine inaccessible ne vaut pas absence de demandes.

Le filtre de prompts est **expérimental et désactivé par défaut**. Il peut détecter
des expressions configurées ou un seuil de longueur dans le dossier exact du canal,
puis notifier ou suspendre le prompt. Il ne mesure pas automatiquement la difficulté
d'un travail. Son installation conserve les autres hooks du profil ; sa confiance
doit être validée dans Codex. Aucune approbation n'est accordée par le switcher.
Le contrat de hook est testé, mais son activation native dans cette installation
Codex reste à qualifier. Voir les [hooks officiels](https://learn.chatgpt.com/docs/hooks).

## Mise à jour et limites de cette bêta

Quitter uniquement l'ancien switcher avant de lancer le nouvel exécutable. Garder
Codex ouvert. Les conversations, comptes, profils et historiques sont conservés.
Les nouvelles tâches utilisent un format séparé des moteurs 0.6/0.7 ; ne pas faire
fonctionner deux moteurs concurrents. Pour les nouveaux outils d'assistance,
réinstaller l'intégration depuis Agents et démarrer un nouveau chat. Les anciennes
conversations peuvent conserver leur processus MCP ; conserver leur ancien dossier
de programme tant qu'elles l'utilisent.

- Recette réelle : messages directs dans un chat nouveau et existant, puis aller
  et retour via TLS local vers Codex ouvert, y compris réponse d'assistance.
- Pas encore de recette entre deux PC distincts : l'accès SSH au serveur proposé
  n'a pas été établi. Le fonctionnement RDP n'établit pas l'accès SSH.
- Retour distant par texte final borné. Les résultats MCP longs, sous-tâches et
  `await_children` d'un job distant ne sont pas synchronisés entre les deux moteurs.
- Pas de reconnexion réseau magique, de copie de fichiers, de contournement des
  permissions, ni d'exécution dans Cursor IDE. Aucun adaptateur Cursor CLI livré.
- Binaire portable non signé ; runtime .NET 10 inclus.

[Détail des tests](WINDOWS-CONSOLE-VALIDATION.md) · [Plan](WINDOWS-CONSOLE-PLAN.md)
