# Validation de la visionneuse 0.12

Recette Windows du 7 octobre 2026, Codex 26.930.7945.0.

## Tests vérifiés

- 315 tests moteur réussis sous .NET Framework et .NET 10, dont 22 nouveaux :
  droits, identité, périmètre, révocation pendant la lecture, contrôle de version,
  instructions non modifiables, édition idempotente, conflit, perte de réponse,
  reçu absent et distinction entre écriture confirmée et relecture échouée.
- 102 tests WPF, dont sélection de Page, édition, conservation du brouillon,
  blocage des doublons, lecture seule, navigation propriétaire et fenêtre compacte.
- Interface web testée dans Edge headless avec un hôte MCP simulé : handshake
  du SDK, affichage, sauvegarde, conflits, coupure de réponse, assainissement HTML,
  absence de requêtes réseau externes, mode compact et thème sombre.
- Plugin installé dans un profil de test, vérification idempotente, réparation,
  respect d'une désactivation explicite. L'app-server Codex charge les quatre
  skills, les 25 outils et l'HTML du panneau avec ses métadonnées fullscreen.
- Lecture réelle d'une Page déjà partagée depuis l'instance destinataire :
  11 blocs affichés dans la fenêtre WPF, sans changer la configuration des accès.
- Écriture réelle via le service de la visionneuse, depuis un autre compte,
  sur la Page de validation préexistante : remplacement d'un bloc, reçu confirmé,
  relecture, restauration du texte initial avec les nouvelles empreintes et
  nouvelle relecture. Le partage temporaire de recette est retiré ; les partages
  préexistants restent identiques. Aucun nouveau tour de modèle.
- Ouverture de la Page chez son propriétaire via `open_in_codex`, puis navigation
  dans son chat existant et activation de la fenêtre. Aucun prompt envoyé.

## Limites de ces preuves

La récupération de l'HTML par Codex prouve la compatibilité du transport MCP,
pas le rendu dans le panneau d'un ancien chat. La vue web est validée dans un
navigateur avec le pont simulé ; l'affichage dans une vraie conversation chargée
avec le nouveau plugin reste à vérifier. La visionneuse Windows, elle, a affiché
le contenu réel. Les anciens chats conservent leurs outils jusqu'à rechargement.

Les médias privés et les documents natifs ne sont pas rendus dans la visionneuse.
La mise en forme du lecteur WPF est simplifiée. Le lecteur natif de Pages reste
soumis aux permissions du compte connecté ; aucune carte historique n'est réécrite.

## Preuves locales non publiées

Les journaux et captures sont dans `work/shared-page-*.log`,
`work/shared-pages-*.log`, `work/desktop-validation` et `work/viewer-validation`.
Ils restent hors Git ; aucune capture ou donnée privée de la Page réelle n'est
incluse dans le dépôt ou dans la livraison.
