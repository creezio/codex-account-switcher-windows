# Relais entre comptes — v0.4

Un compte prépare les fichiers dans un dossier local partagé. Le relais demande au
compte propriétaire de les publier dans une nouvelle conversation, puis remet sa
réponse dans la conversation qui a envoyé la demande. Une suite peut rejoindre la
même conversation destinataire. Chaque compte conserve ses propres connexions Sites.

## Connecter les comptes

1. Ouvrir les instances souhaitées dans le switcher. Garder les deux exécutables
   `CodexAccountSwitcher.exe` et `CreezioRelay.exe`, avec leurs `.config`, ensemble.
2. Dans chaque instance, ouvrir une conversation de connexion pour le projet et
   sélectionner **Accès complet dans cette conversation** si le relais doit travailler
   sans demande d'approbation de commandes.
3. Dans **Relais → Canaux connectés → Connecter**, choisir un nom
   distinct (par exemple `dev-creezio` ou `publication-creezio`) et le même chemin
   absolu du projet. Copier la consigne dans la conversation correspondante.
4. Vérifier le compte, le dossier et les permissions retournés. L'option **Exiger
   Accès complet** est cochée par défaut. Décocher permet de conserver un canal
   soumis aux approbations normales de Codex.
5. Reconnecter le même canal après un redémarrage de son instance. Un changement
   de compte exige un nouveau nom de canal ; une demande en attente reste liée au
   compte et au dossier d'origine.

**Accès complet affiché dans une autre fenêtre ne prouve pas les permissions d'un
chat déjà créé.** Le test réel a montré un ancien chat en `read-only/on-request`
alors que le sélecteur général affichait Accès complet. Le contrôle lit les
permissions enregistrées de la conversation ; il ne change aucun réglage Codex et
ne clique jamais sur Approve. Après changement, envoyer un message dans le chat de
connexion puis reconnecter le canal. Une configuration inconnue bloque l'envoi
exigeant Accès complet. La nouvelle tâche doit aussi vérifier son propre contexte
avant tout outil ; les approbations propres aux plugins restent celles de Codex.

## Envoyer depuis l'interface

**Nouvelle demande** choisit deux canaux, l'objet, la version prête et les instructions.
La source est la conversation utilisée pour connecter son canal. Pour recevoir la
réponse dans la conversation de développement réelle, utiliser la commande ci-dessous
depuis cette conversation. **Continuer l'échange** réutilise le chat destinataire.

Le switcher doit rester ouvert ou réduit près de l'horloge pour relever les réponses
toutes les quatre secondes. Quitter suspend le relais, sans arrêter Codex. Au retour,
les demandes reprennent ; les envois dont l'issue est inconnue ne sont pas répétés.

## Commandes pour un agent

Exécuter dans la conversation de l'instance source, avec le chemin réel de
`CreezioRelay.exe`. Les variables de contexte fournies par Codex identifient cette
conversation ; ne pas les fabriquer ni copier celles d'un autre compte.

```powershell
$relay = 'C:\Outils\Creezio\CreezioRelay.exe'
@{
  operation = 'send'
  from = 'dev-creezio'
  to = 'publication-creezio'
  title = 'Publier la page prête'
  revision = 'SHA du commit ou SHA256 du fichier'
  prompt = 'Vérifie la version préparée et publie les fichiers du dossier partagé. Réutilise .openai/hosting.json et son project_id. Conserve la visibilité du Site. Rapporte le résultat et l’URL.'
  returnToSource = $true
} | ConvertTo-Json -Compress | & $relay
```

Conserver l'`Id` retourné. `returnToSource = $true` autorise la relance de la
conversation source avec la réponse finale. Sans cette option, la réponse reste
consultable dans le switcher. Le message reçu commence par `[CREEZIO_RESULT:Id]`.

```powershell
@{operation='get'; id='ID_RETOURNE'} | ConvertTo-Json -Compress | & $relay
@{operation='wait'; id='ID_RETOURNE'; seconds=50} | ConvertTo-Json -Compress | & $relay
```

Pour poursuivre le même échange, envoyer une autre demande `send` avec les mêmes
canaux et `replyTo = 'ID_RETOURNE'`. Pour accuser réception sans boucle, utiliser
`returnToSource = $false`. Une clé `id` optionnelle (32 caractères hexadécimaux)
rend la soumission idempotente : même clé et même contenu ne recréent pas la demande.

`channels`, `list`, `get`, `pump`, `wait`, `cancel`, `recheck` et `close-reviewed`
complètent le protocole JSON sur entrée/sortie standard. `register` est normalement
généré par l'interface ; il accepte `channel`, `workspace`, `name` et
`requireFullAccess` (vrai par défaut). Ne transmettre aucun jeton dans les demandes.

## Fichiers et propriété

Le même chemin Windows désigne les mêmes fichiers physiques pour les deux instances.
Leurs conversations, connexions et permissions Sites restent séparées. Le relais
ne copie pas le projet : terminer les modifications, fournir une empreinte ou un
commit, puis laisser le propriétaire vérifier cette version avant publication.
Les demandes sont sérialisées par canal, mais le relais ne verrouille pas l'éditeur
ni les autres canaux. Éviter deux auteurs simultanés dans le même checkout.

## Reprise et limites

- **En attente** : compte, permissions, instance ou demande précédente à vérifier.
- **À vérifier** : envoi potentiellement effectué, réponse absente ou tronquée.
  Consulter Codex. **Relire le résultat** ne renvoie jamais le prompt.
- **Classer après vérification** conserve l'historique et libère le canal. Ne
  recréer une demande qu'après avoir vérifié qu'elle n'a pas déjà été exécutée.
- **Remis à Codex** confirme l'acceptation du message de retour par Codex, pas
  l'exécution d'une action ultérieure. Le résultat peut lui-même signaler un échec.
- Les demandes/réponses et canaux sont chiffrés avec DPAPI sous
  `%LOCALAPPDATA%\Creezio\CodexAccountSwitcher\relay` ; ils restent également dans
  les conversations Codex où ils ont été transmis.
- Le relais utilise un canal local interne de Codex, sensible aux versions. Il
  exige le même utilisateur Windows, une instance ouverte, un stockage de connexion
  fichier et des outils de conversation compatibles. Pas de service réseau.
- Les chats créés sont locaux et sans projet enregistré ; le prompt contient le
  dossier partagé explicite. Les plugins disponibles dépendent du destinataire.
  Aucun accès Sites n'est accordé au compte développeur par le relais.

Le test réel du 2 octobre 2026 a validé : Site privé créé par A, refus d'accès Sites
pour B, modification locale par B, publication v2 par A sur le même projet et URL,
retour à B puis accusé reçu par A. La correction des permissions a aussi été testée
dans une nouvelle conversation B, avec une commande réussie sans approbation.
