# Surveillance native des chats et compatibilité Codex

Le Switcher contrôle les conversations configurées toutes les 60 secondes. Le moteur `CreezioRelay.exe --chat-watch-worker` lit les métadonnées du chat dans son instance Codex existante. Il ne crée aucune tâche planifiée Codex, aucun chat, aucun prompt et aucun tour de modèle.

Dans **Tâches → Surveillances**, chaque fiche affiche le compte permanent, le chat exact, le dernier contrôle, son ancienneté, les alertes et leur chronologie. L'indicateur de la barre latérale reste visible tant qu'une alerte nécessite une intervention. Une notification Windows est demandée si les notifications du Switcher sont activées ; Windows peut en masquer l'affichage. Garder le Switcher ouvert ou réduit près de l'horloge pour recevoir ces notifications. Le moteur conserve ses observations sur disque si l'interface est quittée ; les alertes seront visibles à sa réouverture.

## États et fin de mission

- Une approbation native ou une réponse utilisateur attendue produit une alerte au prochain contrôle.
- Un tour en échec, une interruption ou un arrêt sans achèvement confirmé produit une alerte. Une réponse finale ou un site publié ne prouve pas la fin de l'ensemble d'un projet.
- Deux échecs consécutifs de lecture signalent une perte de suivi. Dès le premier échec, la vue indique que le travail n'est plus vérifiable.
- Après 15 minutes sans changement des métadonnées visibles, un avertissement invite à vérifier l'activité. Un test long peut expliquer ce délai : ce n'est pas une preuve de blocage. Le délai est configurable, ou désactivable.
- La fin de mission est confirmée explicitement par l'utilisateur, ou par une valeur JSON précise dans un fichier du dossier de travail, lorsque le chat n'est plus actif.

Chaque épisode est enregistré sous DPAPI avec son état de notification et d'acquittement. Un même épisode ne produit pas une nouvelle notification à chaque minute ni au redémarrage. Une reprise réelle du travail efface l'alerte active ; un nouveau blocage peut alors produire une nouvelle alerte. Acquitter une alerte ne déclare pas la mission terminée et ne relance pas l'agent.

L'identité est liée à l'instance, au compte, au profil local et à l'identifiant du chat. Le moteur vérifie le compte avant et après la lecture. Il n'ouvre pas une instance fermée, ne bascule pas vers un moteur sans fenêtre et ne donne aucune autorisation.

## Changements de version Codex

Au démarrage puis chaque minute, le Switcher détecte la version du paquet Codex installé, les instances actives et l'identité du CLI configuré. Un changement déclenche une vérification des connexions existantes ; une défaillance est réessayée après cinq minutes. **Paramètres → Compatibilité Codex** affiche le rapport et permet de relancer le contrôle.

Les contrôles vérifient le propriétaire Windows du canal, le compte associé, le catalogue des commandes natives et la lecture d'un chat existant si un canal enregistré le permet. Les instances fermées restent indiquées comme non testées. La version installée et la version encore exécutée par chaque instance sont affichées séparément. La commande locale `codex --version` n'exécute aucun agent.

Cette vérification ne publie pas un site, ne modifie pas une ressource, ne crée pas un environnement Cloud et n'envoie pas de mission. Elle ne garantit donc pas le fonctionnement de toutes les écritures ou de tous les plugins après une mise à jour. Une incompatibilité détectée produit un avertissement indiquant qu'une mise à jour du Switcher peut être nécessaire ; le Switcher ne modifie pas lui-même son code et n'installe pas automatiquement une autre version.

La livraison 0.22.4 accepte les noms de canaux `codex-browser-use-<UUID>` et `codex-app-tools-<UUID>`, en contrôlant le PID et l'heure de démarrage du processus propriétaire avant tout appel. Ce changement rétablit la découverte du canal avec Codex 26.1013.

## Recette

`scripts/test.ps1 -ChatWatchOnly` exécute les tests du moteur et de compatibilité sans RPC réel. L'option `--monitoring-ui-test <dossier>` de l'application vérifie les actions et états de la vue dans un coffre fictif, sans compte réel ni réseau. Les lectures réelles de Yaadz restent séparées de ces fixtures.
