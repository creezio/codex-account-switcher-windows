# Sécurité

Merci de ne pas publier de jetons, de fichier `auth.json`, de coffre DPAPI ou de
captures avec des identités réelles dans les issues. Fournissez la version de
Windows, la version Codex et les étapes reproduites avec des comptes fictifs.

Les connexions sont chiffrées au repos pour l'utilisateur Windows courant. Une
connexion OAuth officielle peut écrire temporairement un `auth.json` dans un
dossier privé pendant l'opération. Le format de connexion actif reste celui de Codex.

Les serveurs auxiliaires s'exécutent dans un Job Object Windows dédié. Aucun
processus utilisateur n'est fermé. La bascule refuse les clients connus encore
ouverts et les magasins d'identifiants non pris en charge.

Le format de stockage est versionné. Une erreur de déchiffrement ne déclenche
jamais la création d'un coffre vide par-dessus le fichier existant.
