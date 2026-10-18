# Switcher 0.29.0-beta.2

Switcher Live et l’application Windows permettent de consulter et de gérer les
skills, les objectifs d’une discussion et les planifications Switcher. Les mêmes
opérations sont accessibles par l’API d’administration signée.

- **Skills** : catalogue des compétences préinstallées, des plugins et des
  personnalisations ; création, édition, import ZIP et copie vers une instance
  autorisée. La personnalisation d’un original protégé crée une copie distincte,
  sélectionnable dans le composeur, avec sa provenance visible.
- **Objectifs natifs** : lecture, définition, changement de statut ou de budget,
  et effacement avec contrôle de version. Un changement de budget conserve le
  texte existant et sa comptabilité native.
- **Planifications** : préparation en pause, intervalle, calendrier quotidien ou
  hebdomadaire, fuseau du PC et reprise durable. Une occurrence déjà admise n’est
  pas envoyée une seconde fois après une réponse perdue.
- **Comptes et plugins** : connexion officielle par navigateur, identité à
  confirmer avant création de l’instance, installation depuis le catalogue et
  lien vers la connexion officielle du service avec le compte concerné.
- **Fiabilité Windows** : les lectures du registre et du coffre tolèrent les
  brèves indisponibilités pendant un remplacement atomique de fichier. Une telle
  lecture ne devient plus silencieusement un registre ou un coffre vide. Une
  vraie révocation, une corruption ou un changement de compte reste refusé.

Le lanceur permanent conserve la même entrée dans le menu Démarrer. Les options
de mise à jour intégrée et de démarrage Windows restent disponibles dans les
paramètres. Les hôtes des conversations ouvertes ne sont pas fermés pour mettre
à jour Switcher.

La gestion des objectifs exige actuellement un profil isolé arrêté et disponible.
Les planifications nécessitent que le PC et Switcher fonctionnent. Les plugins
utilisent les services présents dans le catalogue et leurs autorisations
officielles ; installer un plugin ne prouve pas que son service est connecté.

Voir [le contrat Web/API](PORTAL-MANAGEMENT.md), [les limites des skills](SKILLS.md)
et [la mise à jour intégrée](APPLICATION-UPDATES.md).
