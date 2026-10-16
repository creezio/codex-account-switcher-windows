# Connexion : navigateur, profil et lien — 0.12.4

Le choix du navigateur concerne uniquement l’étape de connexion du compte.
L’association permanente entre une instance et son compte Codex reste inchangée.

## Parcours

1. Créer une instance, donner son nom, sélectionner « Connecter un nouveau compte ».
2. Choisir le navigateur puis un profil existant, ou le mode « Copier le lien ».
3. Préparer et ouvrir : Codex génère le lien OAuth, visible en lecture seule avec
   un bouton de copie. En mode manuel, aucun navigateur n’est lancé.
4. Terminer la connexion dans le navigateur choisi, sur le même PC ; le Switcher
   conserve le serveur de retour local pendant la tentative.
5. Le compte confirmé est importé, associé à l’instance, puis l’intégration est
   préparée comme auparavant. Le lien est retiré à la fin de la tentative.

Le même composant sert dans Comptes → Ajouter un compte. Le bouton Annuler la
connexion reste disponible pendant l’attente. Une tentative est limitée à cinq
minutes. Le lien copié précédemment n’est pas réutilisé après annulation ou erreur.

## Fonctionnement

- Détection des exécutables dans les emplacements habituels et App Paths Windows.
- Lecture des noms dans `Local State` → `profile.info_cache`, avec vérification
  de l’existence des dossiers. Repli sur les dossiers Default/Profile existants
  si les métadonnées sont temporairement illisibles. Aucun cookie ou mot de passe
  n’est lu, copié ou modifié.
- Lancement direct de l’exécutable choisi avec `--user-data-dir` et
  `--profile-directory`, sans shell intermédiaire. Citation des arguments Windows
  pour conserver les espaces et le lien OAuth complet.
- Mode Windows par défaut explicite ; mode manuel sans lancement de processus.
- Préférences limitées à l’identifiant du navigateur et au dossier relatif du
  profil dans `settings.json`. Le lien OAuth reste seulement dans le formulaire
  vivant et, si l’utilisateur clique Copier, dans son presse-papiers.
- Si le navigateur ou le profil enregistré disparaît, choix explicite requis.
  Si le lancement échoue pendant la tentative, le lien reste utilisable et
  l’attente du retour OAuth continue. Aucun repli silencieux vers un autre profil.
- Les contrôles WPF sont lus sur leur dispatcher. Le lancement, l’affichage du
  lien et la copie partagent le même choix figé pendant la tentative.

## Portée

Chrome, Edge, Opera, Opera GX, Opera Air, Brave et Vivaldi sont pris en charge
dans leurs installations et dossiers habituels. Un profil de navigateur n’est
pas une preuve d’identité ChatGPT : le navigateur peut proposer plusieurs comptes
et l’utilisateur vérifie le compte avant de valider. Les profils personnalisés,
Firefox, les sessions privées et les conteneurs restent utilisables via le lien
copié, sans détection automatique de leurs sessions.

Documentation de référence :
[Chromium — dossiers de profils](https://chromium.googlesource.com/chromium/src/+/master/docs/user_data_dir.md),
[Opera — migration vers le sous-dossier Default](https://blogs.opera.com/desktop/2023/08/resolving-profile-loss-issues-in-opera-version-102-0-4880-16/).

## Validation

Les tests moteur couvrent la détection sur métadonnées synthétiques, un fichier
corrompu, les profils absents, les chemins invalides, l’URL autorisée, le mode
manuel, la relecture des arguments par Windows et la compatibilité des réglages.
Les tests WPF couvrent la sélection, les préférences, la copie exacte du lien,
l’annulation, l’échec du lancement, le profil supprimé, l’ajout de compte et le
bouton Copier visible dans l’assistant d’instance.

Les connexions de recette utilisent des comptes et des liens fictifs. Aucune
connexion utilisateur ni changement de compte réel n’est effectué par ces tests.

Résultats locaux du 8 octobre 2026 : 347 tests moteur réussis sous .NET Framework
4.8, 347 sous .NET 10, 140 vérifications WPF réussies sur la livraison portable
0.12.4, compatibilité des données entre runtimes confirmée. Les 10 tests ciblés
navigateur ont également été exécutés sur le code final. Le catalogue réel a
détecté Chrome, Edge et Opera et validé les arguments de leurs profils existants,
sans ouvrir de navigateur ni lire une session authentifiée.
