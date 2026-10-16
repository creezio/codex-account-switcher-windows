# Liens vers les ressources — 0.12.2

Le tunnel utilise le compte propriétaire pour les outils. Une carte native Page
ou Site utilise, elle, le compte connecté dans le Codex qui affiche la réponse.
Le succès d’une modification via le tunnel n’accorde donc pas d’accès natif.

## Parcours

1. Garder le partage existant dans Instances → Ressources & accès.
2. Charger la nouvelle intégration dans un nouveau chat Codex.
3. Demander l’opération habituelle via le tunnel.
4. Après vérification, l’agent utilise `get_shared_resource_link` et insère le
   lien Markdown nommé dans sa réponse, sans carte ni citation native.
5. Le clic demande l’ouverture dans le chat d’ancrage existant de l’instance
   propriétaire, puis affiche cette fenêtre. Aucun prompt ni chat n’est créé.

Le Switcher ne présente plus d’éditeur de Pages. Une ancienne ressource MCP de
la version 0.12 reste lisible pour compatibilité, mais elle n’est plus annoncée
et ses outils ne sont plus proposés dans les nouveaux chats.

## Implémentation et limites

- Serveur de navigation lié uniquement à `127.0.0.1`, port stable enregistré
  avec DPAPI. Il s’arrête avec le Switcher. Les liens sont locaux à ce PC.
- Identifiant opaque aléatoire de 64 caractères, réutilisé pour la même ressource
  et le même couple de comptes. Aucun token de session, contenu ou URL privée
  n’est présent dans le lien. Maximum 1 000 liens enregistrés.
- GET/HEAD sont sans navigation. Une page visible envoie un POST de même origine.
  Host et Origin sont vérifiés ; les requêtes étrangères, corps et paramètres
  inattendus sont refusés. Pas d’accès depuis le réseau distant.
- Chaque clic revérifie le partage, la lecture autorisée, la ressource exacte,
  les comptes permanents et leurs connexions. Un changement de compte ou un
  retrait d’accès invalide les liens déjà créés.
- Une Page ouvre son identifiant natif chez le propriétaire. Un Site est relu
  par `sites.get_site` : sa Page attachée est préférée, sinon son URL HTTPS
  publiée ou de prévisualisation, sans identifiant utilisateur, query ou fragment.
  `expected_url` ne prouve pas une publication et n’est jamais utilisée.
- Aucun partage cloud, droit, compte, prompt, contenu ou publication n’est modifié.
- Les anciennes cartes « indisponible » ne sont pas réécrites. Afficher une
  ressource privée dans la fenêtre native d’un autre compte n’est pas résolu.
- Les skills guident la présentation des nouvelles réponses ; le Switcher ne
  réécrit pas les messages produits par Codex. Un agent ignorant ces instructions
  peut encore émettre une carte native invalide.

Tests : `ResourceLinksTests` couvre identité, révocation, exactitude de la cible,
absence d’écriture/prompt, réutilisation des liens, redémarrage du serveur,
GET/HEAD inertes, origine/Host, titres HTML, URLs de Sites et secrets persistés.
