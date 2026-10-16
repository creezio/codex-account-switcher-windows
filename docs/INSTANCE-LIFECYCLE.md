# Cycle des instances — 0.12.1-beta.1

## Commandes

Dans **Instances**, sélectionner l’instance. Les commandes restent accessibles
au-dessus des onglets Compte, Ressources & accès et Activité.

| État | Action principale | Autres actions |
|---|---|---|
| Fermée | Ouvrir Codex | Archiver pour une instance gérée |
| Ouverte | Afficher la fenêtre | Fermer, Redémarrer |
| En arrière-plan | Rouvrir la fenêtre | Fermer, Redémarrer |
| Démarrage en cours | Attente de la fenêtre | Fermer, Redémarrer |
| Fermeture en cours / état inconnu | Attente / diagnostic | Actions bloquées |

Sur cette version de Codex Windows, fermer la dernière fenêtre peut laisser
le processus actif. Ce n’est donc pas la même chose que **Fermer** dans le Switcher.
L’état affiché est vérifié auprès du processus et de sa fenêtre, y compris avec
un ancien hôte qui inscrit encore « starting » après la fermeture manuelle.

La réouverture utilise l’activation Electron du même profil isolé. Son identité
de lancement, son hôte et son processus sont contrôlés avant l’activation.
Aucun compte n’est remplacé et aucun prompt n’est envoyé.

Fermer et Redémarrer demandent confirmation, car les tâches en cours sont
interrompues. Un redémarrage attend la fermeture effective avant de relancer.
Le compte permanent, les dossiers et les conversations sont conservés. Pendant
l’opération, les commandes de cette instance sont désactivées ; l’actualisation
périodique ne permet pas un double lancement.

Pour une instance gérée, la fermeture passe par son hôte et son job Windows.
Pour la session habituelle, le Switcher identifie un seul processus Codex non
géré, ferme sa fenêtre puis termine ce processus précis s’il reste en arrière-plan.
Il ne termine jamais son arbre de processus : il pourrait contenir le Switcher
ou un hôte indépendant. Une identité ambiguë ou remplacée entraîne un refus.

## Validation du 7 octobre 2026

- 322 tests du moteur sur .NET Framework 4.8 et .NET 10 : états périmés,
  fenêtre fermée, phases de lancement et arrêt, identités de lancement, fermeture
  ciblée et refus de relancer tant que l’arrêt n’est pas terminé.
- 109 tests WPF : affichage des commandes, état arrière-plan, annulation,
  confirmation nommant l’instance, verrouillage pendant l’opération, transitions
  fermer/ouvrir et conservation des commandes sur la session habituelle.
- Recette réelle sur une instance gérée : réactivation de la fenêtre avec le
  même PID ; fermeture manuelle et détection arrière-plan ; nouvelle réactivation
  avec le même PID ; redémarrage avec une nouvelle identité ; fermeture complète
  puis réouverture. Le compte et l’autre instance active sont restés identiques.
- La fermeture réelle de la session habituelle n’a pas été exécutée : elle
  hébergeait la conversation de développement. Sa détection et ses commandes
  sont couvertes par les tests ; la recette destructive concerne l’instance gérée.

Les preuves locales restent dans `work/lifecycle-*.log`. Aucun contenu de
conversation ni identifiant d’accès n’est enregistré dans ces rapports.
