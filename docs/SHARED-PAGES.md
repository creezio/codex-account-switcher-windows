Document historique 0.12 : parcours retiré en 0.12.2. Voir [les liens vers le propriétaire](RESOURCE-LINKS.md).

# Pages partagées par le tunnel

La version 0.12 ajoute une vue de la Page originale dans le compte propriétaire.
Lire ou modifier par le tunnel ne donne pas d'accès au lecteur natif de Pages
avec le compte destinataire. Les anciennes cartes « Page indisponible » restent
donc inchangées.

## Utilisation

1. Dans l'instance propriétaire → Ressources & accès, sélectionner l'instance
   destinataire, cocher la Page et choisir Lecture ou Lecture et modification.
2. Dans l'instance destinataire → Compte ou Ressources & accès, cliquer sur
   **Consulter les Pages reçues**, puis sélectionner la Page par son nom.
3. Lire le contenu, modifier un bloc ou ajouter du texte en fin de Page.
   **Enregistrer sur la Page** écrit dans le document original.
4. **Ouvrir chez le propriétaire** ouvre la Page dans un chat existant de cette
   instance et affiche sa fenêtre. Cela ne crée pas de chat et n'envoie aucun prompt.

Les deux instances Codex doivent rester ouvertes, connectées aux comptes associés,
et leur intégration doit avoir été connectée au moins une fois depuis un chat.
Aucun partage supplémentaire n'est nécessaire si la Page est déjà cochée.

Dans un nouveau chat ayant chargé le plugin mis à jour :

> Affiche la Page DGD de l'instance Creezio avec le lecteur du tunnel.

Le skill utilise `list_shared_pages` et `open_shared_page`. Les noms sont ceux
configurés par chaque utilisateur. Après une modification vérifiée, il ouvre
cette vue et cite le titre en texte simple, sans fabriquer de carte Page native.

## Enregistrement et conflits

Chaque sauvegarde utilise une lecture complète appartenant à l'instance et au
chat appelants, les identifiants des blocs et leurs empreintes, ainsi que le
numéro de séquence lorsque le fournisseur le renvoie. Le Switcher possède une
identité d'audit distincte : il n'emprunte pas un chat pour modifier la Page.

Le serveur recontrôle le partage, les comptes et le contrat de l'outil avant
l'envoi. Une modification concurrente est refusée par le fournisseur ; le
brouillon reste visible. Une réponse perdue ne déclenche aucun renvoi. Un reçu
confirmé suivi d'un échec de relecture est distingué d'un enregistrement incertain.
Annuler un brouillon demande confirmation ; actualiser est bloqué pendant sa saisie.

## Périmètre

- Pages textuelles : lecture des blocs, titres, listes, texte mis en forme et code.
- Édition explicite d'un bloc Markdown ou ajout de texte ; 80 Ko par sauvegarde.
- Instructions d'agent consultables en lecture seule dans cette interface.
- Le panneau MCP utilise le SDK MCP Apps, Marked et DOMPurify embarqués. Aucun
  script, image ou lien actif provenant du document n'est exécuté ou chargé.
- Aucun jeton de compte, cookie ou contenu privé dans une URL. La session locale
  de la vue passe par le pont MCP et les résultats restent chiffrés comme les
  autres appels du tunnel.
- Médias privés, pièces jointes, documents, tableurs et présentations : utiliser
  l'éditeur de l'instance propriétaire. Le lecteur Windows applique une mise en
  forme textuelle simplifiée ; il ne reproduit pas tous les blocs natifs.
- Le panneau MCP nécessite un hôte compatible. Si celui-ci ne l'affiche pas,
  la visionneuse du Switcher reste disponible. Une installation ne modifie pas
  les outils déjà chargés par les anciens chats.

## Développement et validation

`npm ci --prefix viewer`, puis `node viewer/build.mjs`, reconstruisent la ressource
HTML autonome livrée dans `plugins/creezio-relay/ui`. Node n'est pas requis chez
l'utilisateur final. Les versions sont verrouillées et les licences embarquées.
`node viewer/test.mjs` vérifie la vue avec un hôte MCP simulé et Edge headless.
`PLAYWRIGHT_MODULE` permet de réutiliser une installation existante de Playwright.
Les scripts de compilation .NET consomment directement l'HTML versionné.

Voir [SHARED-PAGES-VALIDATION.md](SHARED-PAGES-VALIDATION.md) pour les preuves réelles
et la distinction entre découverte du panneau par Codex et rendu dans une conversation.
