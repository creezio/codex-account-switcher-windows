# Version 0.17.1-beta.1 — Windows x64

## Configurer une mission progressivement

Le formulaire unique est remplacé par quatre étapes : **Compte**, **Environnement**,
**Mission**, puis **Vérifier**. Choisir le compte lance automatiquement le
chargement de ses environnements publiés. La progression indique le chargement,
une liste vide ou une erreur ; une reprise reste possible sans changer de compte.

L’étape suivante n’est accessible qu’après un choix valide. Le titre et le mandat
sont conservés lors des retours entre étapes. Le récapitulatif montre le compte,
l’environnement et le texte exact avant **Envoyer la mission**. Une fois l’intention
d’envoi enregistrée, une reprise garde son identifiant afin de ne pas dupliquer
la mission.

Passer au récapitulatif demande une nouvelle action avant l’envoi. Un double clic
sur **Continuer** ne peut pas valider puis envoyer la mission sans laisser lire
ce récapitulatif.

Une réponse tardive d’un ancien compte ne remplace pas la liste du compte actuel.
Fermer l’assistant annule le chargement en cours ; quitter avec un mandat saisi
demande confirmation. Aucune mission n’est envoyée pendant le choix des étapes.

## Lire l’instance et ses environnements

La fiche d’instance donne une place stable au nom, au compte, à l’état et à
l’action principale. Les commandes moins fréquentes sont regroupées dans des
menus. Les onglets séparent le compte, les accès, l’activité et le Cloud.

Le Cloud présente un catalogue d’environnements et le détail de la sélection.
Le nom, l’état et le dépôt guident le choix ; les opérations de préparation et de
publication apparaissent selon l’état de l’environnement. Les commandes de
renommage et de configuration ne forment plus une rangée permanente de boutons.

## Périmètre conservé

Cette version change la présentation et l’enchaînement des choix. Elle conserve
les comptes permanents, les permissions, les données, les protections d’envoi et
les limites de concurrence de la 0.17.0-beta.2. Les environnements restent propres
au compte choisi et le conteneur Cloud garde son checkout séparé.

La modification d’un environnement déjà publié peut toujours recevoir HTTP 403.
La première connexion GitHub depuis le Switcher et le retour automatique dans un
vrai chat orchestrateur restent les limites documentées de la recette précédente.
Voir [CLOUD-NATIVE-PILOT.md](CLOUD-NATIVE-PILOT.md) et
[RELEASE-0.17.md](RELEASE-0.17.md) pour les preuves et limites du transport Cloud.

## Validation

- Compilation WPF sans avertissement ni erreur et **345 vérifications UI**.
- Chargement automatique, changement de compte pendant l’attente, erreurs,
  liste vide, retours entre étapes et préservation du mandat vérifiés avec des
  transports simulés, sans mission réelle envoyée.
- Récapitulatif vérifié en **680 × 580**, catalogue Cloud en **980 × 620** et
  **1280 × 860** ; les actions de navigation restent visibles.
- Compatibilité des données Framework ↔ .NET 10 vérifiée dans les deux sens.

La direction visuelle a été explorée avec une
[projection générée par le modèle d’image](../assets/design/cloud-workspace-projection.png)
et son [prompt](../assets/design/cloud-workspace-projection-prompt.txt).
Cette projection explique la direction retenue ; elle ne remplace pas une capture
de l’application. Le README montre une capture réelle de l’assistant avec des
comptes fictifs.

## Livraison

Décompresser tout le ZIP, puis lancer **CodexAccountSwitcher.exe**. Windows 10/11
x64 est requis. Le runtime .NET 10 est inclus ; aucun SDK n’est nécessaire.
Conserver les DLL, exécutables, fichiers JSON et le dossier `plugins` ensemble.

Quitter seulement l’ancienne application Switcher avant de lancer cette version.
Les fenêtres Codex peuvent rester ouvertes. Ne pas supprimer un ancien dossier
encore utilisé par un hôte d’instance ou un serveur MCP. Les données et profils
existants sont relus sur place.

La préparation locale réutilise les fichiers de runtime identiques de la livraison
existante après comparaison SHA-256 avec les packs officiels en cache. Le ZIP
final contient des fichiers autonomes et ne dépend pas du dossier précédent.
