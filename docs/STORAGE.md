# Stockage et temporaires — 0.19

La page **Stockage** inventorie les profils des instances, les dossiers des
projets déjà référencés dans Codex ou le relais, les répertoires du Switcher et
les temporaires Windows. Elle ne démarre pas Codex et ne charge pas le contenu
des conversations. Une analyse se lance au clic, s’annule et se filtre localement.
Le rafraîchissement général de l’application ne relance pas cette analyse.

Les tailles représentent les données logiques des fichiers. Les liens physiques
NTFS sont identifiés par volume et identifiant de fichier, et comptés une fois
par analyse. Cela ne mesure pas l’allocation exacte des fichiers compressés,
sparse ou des métadonnées NTFS. Les erreurs, limites de parcours et volumes non
mesurés sont signalés ; une taille partielle n’est pas présentée comme complète.
Le fichier d’échange Windows est mesuré sans être modifié.

## Nettoyage

Une source, un historique, une base, une identité ou un dossier projet n’est pas
supprimé. Une dépendance ou compilation est **à examiner**, jamais nettoyable sur
le seul critère de son nom, de son âge ou de sa taille. Les anciens dossiers de
recette sans preuve de propriété ne sont pas réétiquetés pour les supprimer.

Le nettoyage proposé exige un dossier GUID directement dans `runtime` ou
`temporary` du coffre du Switcher, un marqueur reconnu qui correspond au chemin
et au propriétaire exact, puis un contenu entièrement identifié comme jetable.
Un ancien scratch qui contient une identité, une base ou un historique est
conservé. Un processus vivant, inaccessible ou dont le PID a été réutilisé est
conservé. Une prévisualisation liste les chemins avant confirmation.

Le moteur refuse les liens/jonctions avant descente, ouvre les éléments vérifiés,
revalide le propriétaire et les références actives, puis supprime par handles
Windows. Il conserve les sources, identités et contenus inconnus même si un
marqueur de temporaire est présent. Un refus lors du préflight conserve tout le
lot ; un échec après le début indique explicitement un résultat partiel. Les
codes Windows 5, 32 et 33 sont distincts d’un refus de politique. Aucun changement
d’ACL, arrêt d’application, élévation ou autre interpréteur n’est essayé.

Les répertoires sélectionnés et leur chaîne d’ancêtres sont tenus ouverts sans
partage d’écriture ni de suppression pendant la préparation et le nettoyage.
Une ouverture incompatible fait refuser l’opération. Les anciens dialogues de
maintenance utilisent aussi ce moteur et affichent ses refus détaillés ; aucun
ancien nettoyage récursif ne subsiste dans ces points d’entrée.

Les petits services de connexion et d’import du Switcher reçoivent leurs propres
`TEMP`/`TMP`, avec marqueur, PID, date de lancement et lease exclusive. À leur
fin, leur arbre de processus est arrêté avant de libérer ce temporaire. Les
chemins `CODEX_HOME` et `CODEX_SQLITE_HOME` du service de connexion sont tous les
deux explicites : une variable SQLite héritée ne doit pas rediriger ses écritures
dans le profil principal.

Le nettoyage automatique, désactivable dans Stockage, examine au démarrage et
toutes les dix minutes **uniquement les nouveaux temporaires marqués du
Switcher**. Il traite des lots bornés et conserve les cas ambigus. Son dernier
rapport expose les chemins, résultats et refus. L’application doit rester ouverte
pour cette maintenance ; aucun service Windows ni tâche planifiée n’est ajouté.

## Prévention

Les copies indépendantes de projets, créations de worktrees et imports de chats
vérifient le volume de destination, l’estimation des écritures simultanées et une
marge avant mutation. Une opération estimée à plus de 50 Mio exige au moins
20 Gio libres. Un coût ou un espace inconnu ne vaut pas zéro. Les petits travaux
restent possibles si leur coût et leur marge sont couverts. Un dossier partagé
évite une copie de projet supplémentaire ; aucune dépendance demandée dans une
copie indépendante n’est omise silencieusement.

Les historiques qui grossissent pendant leur export sont revérifiés avant de
franchir un palier d’écriture. Les reprises de migration passent aussi par le
contrôle de capacité. Les hooks, filtres Git et installations lancés directement
par un agent peuvent néanmoins écrire plus que l’estimation connue.

Cette version ne change pas la sandbox des chats et ne contrôle pas tous les
fichiers créés par des commandes arbitraires d’agents. Le `TEMP` natif des agents
Codex qui exécutent les missions reste inchangé pour éviter de casser les droits
des utilisateurs Windows du bac à sable. Les historiques natifs et dépendances
des projets restent persistants. Une politique refusant une suppression doit
être traitée à sa propre couche, pas contournée.

## Compilation sans accumulation

Le pipeline portable réutilise les packs déjà installés et les runtimes immuables
par liens physiques. `-RefreshApplication` permet de corriger les seuls fichiers
applicatifs d’une livraison **inactive**, avec contrôle des processus, chemins,
fichiers liés et signatures des runtimes. Un verrou exclusif protège le filtre
de publication et l’inventaire partagés. Le compilateur Framework ne remplace pas
l’apphost WPF lors d’une actualisation portable. Aucun nouveau checkout, SDK ni
répertoire de dépendances n’est créé par cette procédure.
