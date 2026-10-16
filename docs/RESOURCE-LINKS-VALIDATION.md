# Validation 0.12.2 — 7 octobre 2026

- 337 tests moteur sous .NET Framework 4.8 : succès.
- 337 tests moteur sous .NET 10 : succès.
- 110 tests WPF et échange des données entre les deux runtimes : succès.
- Un premier passage WPF a échoué sur la taille de fenêtre ; la recette force
  désormais le retour à l’état Normal avant de vérifier une dimension exacte.
- Installation réelle du plugin dans un profil de recette et chargement par le
  Codex app-server : quatre skills et les 21 outils annoncés, dont
  `get_shared_resource_link`. L’éditeur n’est plus proposé dans la liste.
- Application 0.12.2 démarrée ; intégration saine dans les deux instances réelles.
  Aucun processus Codex ni hôte de l’instance secondaire n’a été arrêté.
- Lien réel créé à partir d’un partage Page existant. Navigation depuis le
  navigateur intégré Codex : confirmation HTTP de la demande d’ouverture par
  `open_in_codex` dans le compte propriétaire, puis navigation du chat existant.
  Aucun prompt, aucune modification de contenu et aucun changement de partage.
- Aucun Site n’étant actuellement partagé entre les instances, la navigation
  Sites est vérifiée par tests automatisés, sans qualification réelle complète.

La confirmation de navigation ne constitue pas une inspection visuelle du
rendu de la Page native. Le test n’a pas produit une réponse d’agent dans un
nouveau chat : l’adhésion aux instructions de présentation reste à vérifier
dans l’usage. Les anciennes cartes restent inchangées.

Le profil de test et les compilations réutilisent les dossiers existants. Les
fichiers du runtime de la livraison locale sont liés aux fichiers existants
par le mécanisme de liens physiques de MSBuild ; les fichiers applicatifs
mutables ont été détachés avant lancement. Aucun téléchargement de SDK.
