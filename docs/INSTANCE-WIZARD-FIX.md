# Création d’instance — correction 0.12.3

Le bouton « Préparer et ouvrir » échouait avant la connexion au compte avec
« Le thread appelant ne peut pas accéder à cet objet parce qu’un autre thread
en est propriétaire ». Le contrôle du nom utilisait `name.Text` dans la fonction
passée à `DesktopContext.Read`, qui exécute son travail sur le pool de threads.
Les contrôles WPF appartiennent au thread de l’interface.

Le formulaire capture maintenant le nom et le choix du compte avant le premier
`await`. La validation en arrière-plan et la création utilisent ces valeurs.
Le nom et le compte restent bloqués pendant la soumission, et redeviennent
éditables après un échec de connexion. Une association déjà enregistrée reste
permanente. L’annulation ou l’expiration du délai est expliquée dans le formulaire.

## Validation du 8 octobre 2026

- Test de régression ajouté avant correction : le clic avec un compte existant
  reproduit exactement l’exception WPF signalée.
- Après correction : 124 tests WPF réussis, dont 14 nouvelles vérifications du
  formulaire, plus les contrôles d’interopération des données avec Framework.
- Création avec compte existant ; import et association d’un nouveau compte via
  une réponse de connexion asynchrone simulée ; nom vide et nom déjà utilisé ;
  annulation puis essai avec un compte existant ; erreur puis seconde connexion
  réussie. Les tests passent par le bouton réel et le stockage de recette.
- La connexion OAuth interactive du compte personnel de l’utilisateur n’est pas
  effectuée par ces tests. Le flux OAuth de production n’a pas changé.
- Les tests n’ouvrent pas d’instance réelle, ne modifient aucun compte personnel
  et ne réinitialisent aucune limite. Les données fictives sont isolées.

Les contrôles précédents inspectaient le formulaire sans exécuter sa soumission.
La nouvelle régression couvre explicitement ce passage asynchrone.
