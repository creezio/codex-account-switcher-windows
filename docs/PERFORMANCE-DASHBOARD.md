# Consommation et processus par instance — 0.15

Ouvrez **Performances** dans la navigation du Switcher. Aucun réglage avancé n’est nécessaire.

- Les cartes montrent la mémoire physique du PC, la RAM résidente des instances Codex, leur CPU et le nombre de processus suivis.
- Le tableau associe chaque instance à son compte permanent et regroupe sa fenêtre, son moteur, ses navigateurs, plugins, terminaux et autres descendants Windows.
- Sélectionnez une instance pour voir ses PID, parents, rôles, heure de démarrage, RAM, mémoire privée engagée et CPU. Les colonnes de consommation se trient sur les valeurs numériques.
- Les commandes **Afficher la fenêtre**, **Fermer l’instance** et **Redémarrer** réutilisent la gestion ciblée des instances. La session principale et les processus d’origine inconnue ne peuvent pas être arrêtés depuis ce tableau.
- Le contrôle d’activité lit les chats visibles de l’instance sélectionnée, au plus une fois toutes les 30 secondes. **Vérifier l’activité** relit ce statut. Un chat actif ou en attente bloque les boutons de fermeture. Avant toute fermeture demandée et confirmée, le Switcher contrôle à nouveau tous les chats locaux ; une erreur ou un délai dépassé empêche l’arrêt.
- L’actualisation des mesures peut se régler sur 5, 10 ou 30 secondes, ou être mise en pause. **Actualiser** reste disponible pendant la pause. La collecte ne tourne pas lorsque cette page n’est pas sélectionnée ou lorsque le Switcher est masqué. Le suivi ne crée aucun serveur ni agent supplémentaire.

## Comprendre les chiffres

La **RAM** est le jeu de travail résident Windows. Des pages partagées peuvent être comptées dans plusieurs processus : le total n’est pas une estimation exacte de la mémoire qui sera libérée, ni nécessairement le chiffre « mémoire privée » du Gestionnaire des tâches.

La mémoire **engagée** correspond aux allocations privées garanties par Windows, qui peuvent être en RAM ou appuyées par le fichier d’échange. Une instance peut engager beaucoup plus de mémoire qu’elle n’en conserve en RAM.

Le **CPU** est calculé entre deux échantillons et normalisé sur tous les processeurs logiques du PC. La première mesure ou une reprise après plus de 30 secondes ne fournit pas de pourcentage. Le symbole `≥` indique une mesure partielle, notamment lorsqu’un nouveau processus n’a pas encore d’échantillon précédent. Une faible consommation CPU ne garantit pas qu’un agent a terminé.

Une origine inconnue, une fenêtre masquée ou un processus Node ne prouvent pas qu’un processus est abandonné. Le Switcher ne propose pas d’arrêt arbitraire de PID ni de nettoyage automatique de processus.

## Attribution et confidentialité

L’attribution utilise les PID **et leurs heures de création**, les racines déjà identifiées de chaque instance et les liens parent/enfant Windows. Le parent doit avoir été créé avant son enfant. Les cycles, PID réutilisés et identités ambiguës ne sont pas acceptés. Lorsqu’une instance est descendante d’une autre application, sa propre racine prend priorité pour éviter les doubles comptes.

Les lignes de commande ne sont utilisées qu’en mémoire pour reconnaître des rôles tels que rendu de fenêtre ou GPU. Elles ne sont ni affichées, ni persistées, ni envoyées à un service. Les noms et titres de chats restent locaux. Les caches de mesure et de rôles sont bornés par les processus actuellement présents et les 60 derniers points de la courbe.

La collecte réutilise les API Windows Toolhelp, les temps CPU et les compteurs de mémoire des processus. Elle n’installe aucune dépendance. Les contrôles de lancement protègent les actions contre un redémarrage intervenu après l’affichage du tableau. Le contrôle d’activité constitue une vérification préalable, pas un verrou empêchant l’utilisateur de démarrer lui-même un nouveau travail pendant la confirmation.
