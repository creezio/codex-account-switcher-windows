# Reprise de la file Cloud et fiabilité des états

## Anciennes demandes et missions préparées

Une correction peut posséder le chat de son parent avant d’avoir envoyé son
propre message. Ce chat historique ne prouve pas qu’un nouveau tour travaille.
Les demandes non commencées suivent un arbitrage commun avec la file préparée.
Une exécution réelle ou un envoi incertain conserve ses réservations ; une
simple attente ne doit pas provoquer un cycle entre les deux générations.

Pour raccorder une demande ancienne à une équipe, la commande locale
`cloud-queue-adopt` exige l’identifiant existant, l’équipe, une liste explicite
de fichiers et l’autorisation de l’opérateur, ou la session du chat source exact.
Elle conserve le compte, le mandat, la branche, les réglages et les identifiants
natifs. Elle ne crée ni chat ni message. Le périmètre est une décision explicite
du responsable ; il n’est pas déduit du titre de la mission.

L’adoption possède une preuve DPAPI séparée pour résister à la réécriture du
registre par un ancien client MCP encore chargé dans une fenêtre Codex ouverte.
Une divergence d’identité, de mandat ou de réservation bloque l’admission.

## Envoi incertain

La commande locale `cloud-job-reconcile` relit le chat exact et cherche le
message utilisateur correspondant. Le rapprochement utilise les identifiants
et l’empreinte du contenu, jamais une réponse de l’agent ou une PR isolée.
Une preuve positive rattache le suivi au tour existant sans nouvel envoi.

Les nouveaux envois journalisent séparément un échec avant l’entrée dans le RPC
d’envoi et une réception incertaine après cette entrée. Une reprise explicitement
demandée n’est permise qu’avec une preuve durable d’absence d’envoi. Un ancien
journal ne disposant pas de cette preuve reste incertain : deux lectures d’un
chat vide ne constituent pas une garantie de non-réception.

## Lecture des résultats et affichage

Les pages de résultats contenant les sorties des outils peuvent dépasser la
taille maximale d’une réponse. Le lecteur emploie des pages plus petites et une
reprise de lecture bornée sur le même curseur. Il conserve la limite de taille et
ne présente jamais un fragment de réponse comme un résultat complet.

Le tableau de bord distingue la dernière tentative de lecture de la dernière
lecture réussie. Une erreur persistante ou une preuve trop ancienne affiche le
dernier état connu et son âge, sans compter l’agent comme certainement actif.
Cette information ne libère pas ses réservations et n’autorise aucun renvoi.
Une notification reçue n’équivaut toujours pas à une revue ou à une fusion.

## Validation avant publication

Une validation doit désigner le commit applicatif exact, ses commandes et ses
fichiers de preuve. Quand le produit est strictement en lecture seule dans le
checkout du développeur Cloud, les réservations portent sur les preuves
autorisées ; elles ne bloquent pas sans raison les autres dossiers de preuve.
Le responsable vérifie les résultats terminaux et la concordance des sources
avant l’intégration et la publication. Un échec de validation reste un échec.
