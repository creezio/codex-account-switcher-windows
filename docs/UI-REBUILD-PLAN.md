# Refonte de l'interface — suivi de réalisation

Demande : implémenter le plan de refonte et vérifier chaque étape.

1. [x] Corriger navigation, brouillons, recherche et pagination ; tests de parcours.
2. [x] Définir des composants communs et qualifier Accueil, Comptes et Tâches.
3. [x] Nouvelle interface WPF, navigation et chargements indépendants.
4. [x] Migrer Comptes, Instances et Limites ; vérifier les actions et erreurs.
5. [x] Migrer Agents, Projets et Tâches ; vérifier configuration et délégation.
6. [x] Recette, documentation et préparation de la livraison autonome.

Les preuves, les commandes de reproduction et les limites de qualification sont
détaillées dans [UI-VALIDATION.md](UI-VALIDATION.md). Le démarrage sur les données
de l'utilisateur exige la fermeture de l'ancien switcher par son menu Quitter ;
les instances Codex restent ouvertes.

Contraintes : réutiliser le checkout et les dépendances ; préserver les exécutables
chargés, les données et conversations. Pas de consommation de réinitialisation
réelle dans les tests. Pas de validation automatique des approbations Codex.

État initial : arbre propre, 9,55 Gio libres ; runtimes .NET 7/8 présents,
aucun SDK. Le SDK 10.0.401 a été installé une seule fois, après autorisation
explicite de l'utilisateur malgré le seuil d'espace disque. Le compilateur
.NET Framework existant reste utilisé pour le moteur et les corrections initiales.
