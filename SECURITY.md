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
annoncés par Codex pour les comptes autorisés utilisés, à 1 % restant ou moins.
L'intention et sa clé d'idempotence sont conservées dans le coffre avant l'envoi.
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
