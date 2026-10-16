# Consommation et processus par instance — 0.16

Ouvrez **Performances** dans la navigation du Switcher. Aucun réglage avancé n’est nécessaire.

- Les cartes montrent la mémoire physique du PC, la RAM résidente des instances Codex, leur CPU et le nombre de processus suivis.
- Le sélecteur de gauche associe chaque instance à son compte permanent et montre sa RAM, son CPU et son état. Il reste visible pendant la consultation des processus. **Système** distingue le Switcher et les origines non vérifiées. La recherche accepte les noms et les adresses des comptes. En petite fenêtre, la même recherche reste accessible à côté d’un sélecteur déroulant.
- **Vue d’ensemble** montre la répartition de RAM entre fenêtres, moteur, outils et commandes, ainsi que les trois processus les plus gourmands. **Processus** affiche une liste unique, avec recherche par nom, rôle ou PID, filtres de famille et tri numérique. Sélectionnez une ligne pour retrouver parent, heure de démarrage, mémoire engagée et attribution dans le pied de liste. En très petite fenêtre, ces détails restent accessibles dans son infobulle.
- Le résumé global et les actions restent dans le cadre de la fenêtre, sans défilement de la page. Les colonnes s’ajustent à l’espace disponible ; le rôle passe dans les détails lorsque la largeur manque. Le sélecteur et la liste peuvent défiler indépendamment lorsqu’ils contiennent beaucoup d’éléments. La sélection, les filtres, le tri et la position de la liste survivent aux mesures. La dernière instance choisie et la fréquence sont enregistrées localement.
- **Afficher Codex** amène la fenêtre au premier plan ; **Ouvrir Codex** relance une instance fermée. **⋯** regroupe **Fermer l’instance** et **Redémarrer**, avec confirmation et contrôle complet. La session principale et les processus d’origine inconnue restent protégés.
- Le contrôle d’activité lit les chats visibles de l’instance sélectionnée, au plus une fois toutes les 30 secondes. Les titres des chats actifs sont consultables dans l’infobulle du statut. **Vérifier l’activité** relit ce statut. Les lectures par instance n’empêchent pas de passer à un autre compte. Un chat actif ou en attente bloque les actions de fermeture. Avant toute fermeture demandée et confirmée, le Switcher contrôle à nouveau tous les chats locaux ; une erreur ou un délai dépassé empêche l’arrêt.
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

## Validation de la refonte du 13 octobre 2026

- Compilation portable .NET 10 sans avertissement ni erreur, avec le SDK existant.
- 436 tests du moteur sous Framework et 436 sous .NET 10 ; compatibilité des données et contrats entre les deux runtimes vérifiée.
- 261 tests WPF : navigation, sélection synchronisée, recherche de comptes et de PID, familles, tri numérique, conservation de la sélection et du défilement, changement de compte pendant une requête lente, identité de lancement, pause et reprise, refus d’arrêt des instances protégées, contrôle complet et réouverture ciblée.
- Contrôles de position et de débordement dans les fenêtres 800 × 500, 800 × 560, 1024 × 720, 1280 × 860 et 1440 × 900. Agrandissement de l’interface simulé à 125 % et 150 %, plus inspection des captures en thèmes clair et sombre. Ce test de mise à l’échelle ne remplace pas une recette sur plusieurs écrans physiques.
- Les captures publiques utilisent exclusivement des comptes fictifs. La maquette générée à partir de la capture personnelle reste locale. Les répertoires de compilation et les dépendances sont réutilisés.
- Recette réelle après activation de la 0.16 : attribution des processus de deux comptes ouverts sans doublon de PID, collecte de 18 ms, activité native relue. Les PID et heures de lancement des fenêtres Codex et de l’hôte géré sont restés identiques. Seule l’interface Switcher a été remplacée.
