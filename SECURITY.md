# Sécurité

Merci de ne pas publier de jetons, de fichier `auth.json`, de coffre DPAPI ou de
captures avec des identités réelles dans les issues. Fournissez la version de
Windows, la version Codex et les étapes reproduites avec des comptes fictifs.

Les connexions sont chiffrées au repos pour l'utilisateur Windows courant. Une
connexion OAuth officielle peut écrire temporairement un `auth.json` dans un
dossier privé pendant l'opération. Le format de connexion actif reste celui de Codex.

Les serveurs auxiliaires s'exécutent dans un Job Object Windows dédié. Aucun
processus préexistant n'est fermé. La fermeture explicite d'une instance gérée
cible son seul Job Object après confirmation. La session habituelle ne peut pas
être fermée par le switcher. La bascule refuse une instance cible ouverte et les
magasins d'identifiants non pris en charge.

Le format de stockage est versionné. Une erreur de déchiffrement ne déclenche
jamais la création d'un coffre vide par-dessus le fichier existant.

Les resets automatiques utilisent uniquement les crédits de réinitialisation
annoncés par Codex pour les comptes autorisés utilisés, au seuil choisi pour ce compte.
L'intention et sa clé d'idempotence sont conservées dans le cache partagé DPAPI avant l'envoi. Un verrou interprocessus
sérialise les opérations par compte ; une ancienne interface encore ouverte suspend
les consommations de la nouvelle version.
Un résultat réseau incertain ne crée pas une nouvelle demande ; les répétitions
réutilisent la même clé et sont bornées à trois envois. Après acceptation, les
quotas doivent être relus et rétablis avant de permettre un nouvel incident.

Les instances sont identifiées par un UUID et conservées dans des dossiers privés.
Le contrôleur persistant utilise un verrou par profil, une identité de lancement,
le PID et l'heure de démarrage : aucun arrêt n'est effectué par nom de processus.
Les sorties brutes de Codex sont abandonnées ; seuls des états et avertissements
prédéfinis sont conservés. Les variables d'authentification héritées ne sont pas
transmises à une nouvelle instance.

Les associations ne constituent pas une frontière de sécurité entre utilisateurs
Windows. Les fichiers actifs des profils restent lisibles par leur propriétaire.
Une instance archivée conserve volontairement ses connexions et conversations.

Le relais v0.4 transmet les prompts et réponses explicitement demandés entre les
comptes de l'utilisateur. Ces contenus deviennent visibles dans les conversations
des comptes correspondants. Ses fichiers locaux sont chiffrés avec DPAPI. Aucun
jeton d'authentification n'est transmis dans un message. Les canaux sont épinglés au
compte, au profil, au dossier, au PID et à l'heure de démarrage du processus Codex.
Un redémarrage exige une reconnexion. Il n'existe aucun serveur HTTP de relais.

L'option Exiger Accès complet vérifie le contexte de permissions enregistré avant
la connexion et les envois. Une lecture inconnue échoue de façon restrictive. Le
relais ne modifie ni config.toml, ni sélection de permissions, ni demandes
d'approbation. Les outils Sites s'exécutent dans l'instance propriétaire, sous les
contrôles natifs de ce compte. Les utilisateurs Windows et programmes capables de
modifier les fichiers de ce même utilisateur ne sont pas isolés entre eux par DPAPI.

Les intentions sont enregistrées avant envoi et verrouillées entre processus. Une
coupure après un envoi peut laisser un état incertain, conservé pour vérification
humaine sans renvoi automatique. La sérialisation par canal ne remplace pas un
verrou de dépôt. Les agents doivent vérifier le commit ou l'empreinte avant toute
publication. Le résultat textuel d'une tâche n'est pas une preuve indépendante de
déploiement ; le test réel utilise aussi le statut natif Sites.
