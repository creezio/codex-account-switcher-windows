# Relais entre instances — plan de la version 0.4

Demande du 2 octobre 2026 : préparer des fichiers avec un compte, transmettre une
demande de publication au compte propriétaire du Site, récupérer sa réponse puis
continuer l'échange. Conserver les identifiants Sites et les sessions en cours.

1. Adaptateur du canal local Codex : protocole encadré, contrôle du processus,
   connexion explicite depuis une véritable conversation, compte et espace épinglés.
2. Canaux persistants, demandes et réponses chiffrées pour l'utilisateur Windows.
   Enregistrement avant envoi ; résultat ambigu jamais renvoyé automatiquement.
3. Création d'une conversation destinataire ou suivi d'une conversation existante,
   lecture de son résultat et remise à la conversation émettrice.
4. Interface Relais et commandes utilisables par les agents, sans service réseau,
   mot de passe, transfert de jetons ou remplacement des connexions actives.
5. Tests hors ligne (identité, concurrence, reprise, réponses, doublons), compilation,
   puis deux comptes réels et un Site de test privé. Vérifier le même project_id,
   la mise à jour par le propriétaire et le retour au demandeur.

Le canal interne Codex est sensible aux versions : toute réponse incompatible
arrête l'envoi. Une tâche doit reconnecter son canal après redémarrage de Codex.
La disponibilité des outils Sites reste déterminée par le compte destinataire.
Les écritures concurrentes dans un même checkout doivent être évitées ; les demandes
de publication identifient la version exacte préparée.

Réalisation du 2 octobre : étapes 1 à 5 terminées. 100 tests hors ligne passent.
Le test réel v1 → v2 conserve le Site et son URL : réponse reçue par le développeur,
accusé renvoyé au propriétaire et confirmé. La différence entre permissions générales
et permissions du chat a été reproduite, contrôlée et retestée sans approbation.
Voir RELAY.md et VALIDATION.md pour la procédure et les limites.
