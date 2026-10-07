# Consultation des Pages par le tunnel — 0.12

Objectif : consulter et modifier la Page originale depuis une instance cliente,
sans carte native inaccessible, copie de Page ni second tour de modèle.

1. Ajouter un service de lecture et d'édition de blocs réutilisant strictement
   les autorisations, les contrats, les reçus et l'idempotence du tunnel.
   Vérifier les conflits, les révocations, les erreurs et les résultats incertains.
2. Ajouter une visionneuse dans le Switcher, accessible depuis l'instance cliente :
   liste des Pages reçues, lecture mise en forme, édition explicite d'un bloc,
   ajout de texte, actualisation et ouverture chez le propriétaire.
3. Exposer cette même expérience comme MCP App, avec ressources HTML embarquées,
   outils de lecture/sauvegarde et instructions pour éviter les cartes Pages natives.
   Aucun cookie, jeton de compte ou contenu privé dans une URL.
4. Vérifier les deux runtimes, l'UI Windows, le protocole MCP et l'interface web.
   Faire une lecture réelle d'une Page déjà autorisée et une recette d'édition
   sur une ressource de test identifiée, jamais sur la Page de travail de l'utilisateur.
5. Compiler et livrer le paquet portable, installer l'intégration une fois le
   précédent Switcher quitté, et documenter les preuves et limites restantes.

Périmètre : Pages Markdown, titres, listes, code et blocs textuels. Les instructions
d'agent restent consultables sans édition dans cette interface. Les médias privés,
documents, tableurs et présentations nécessitent l'éditeur propriétaire. La nouvelle
vue ne modifie pas les permissions du lecteur natif et ne réécrit pas les anciens chats.
