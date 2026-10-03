# Console Windows : réalisation 0.8

Périmètre approuvé : conserver WPF, Codex desktop ouvert, Windows uniquement.
Ne pas changer les comptes, permissions ou conversations existantes pendant les tests.

## Étapes et critères de passage

1. **Messages depuis le switcher** : origine utilisateur, nouveau chat ou sélection
   d'un chat de l'instance, réponse dans Tâches, attente si occupé. Vérifier identité,
   compte, périmètre du dossier, idempotence, interruption et retour de résultat.
2. **Adaptateurs** : conserver Codex desktop et préparer le transport distant.
   Cursor suspendu : l'utilisateur exige une conversation visible dans l'éditeur,
   avec exécution identique à un prompt saisi par lui. ACP/CLI ne satisfait pas ce
   besoin ; aucun adaptateur CLI ni intégration par clics n'est livré.
3. **PC distants** : connexion TLS avec empreinte épinglée, association révocable,
   droits par canal (lecture/envoi), absence d'export des connexions de comptes.
   Valider deux extrémités locales, refus, coupure et absence de renvoi aveugle.
   La recette sur deux PC requiert un second PC fourni par l'utilisateur.
4. **Assistance** : tickets persistants, règles configurables, demande depuis un
   agent, réponse humaine ou déléguée, retour au chat d'origine. Les hooks installés
   explicitement peuvent bloquer un prompt selon une règle ; aucun changement
   implicite des permissions natives. Tester chaque transition et les doublons.
5. **Livraison** : tests du moteur Framework/.NET, tests WPF, compilation portable,
   documentation et bilan distinguant tests simulés, réels et non réalisés.

## Choix d'architecture

- Étendre le moteur et les écrans existants ; pas de nouvelle interface graphique.
- Chaque destination conserve son identité et annonce ses capacités.
- Une origine humaine n'a pas besoin d'un chat source ; les délégations d'agents
  gardent une session liée et les politiques de projet existantes.
- Le transport distant joint une adresse explicitement configurée (LAN/VPN ou
  routage administré). Aucun tunnel public ni ouverture de pare-feu automatique.
- Fichiers et secrets ne sont pas synchronisés par la messagerie. Chaque canal
  distant reste limité au dossier autorisé sur sa machine.
- Les envois incertains sont rapprochés de l'historique ; jamais rejoués aveuglément.
- Les anciens profils, tâches et connexions restent conservés.

## État

- Messages directs, transport distant, assistance et livraison portable implémentés.
- Cursor CLI abandonné à la demande de l'utilisateur ; aucun adaptateur IDE validé.
- Recettes locales et Codex réel réussies, consignées dans WINDOWS-CONSOLE-VALIDATION.md.
- Restent à qualifier : deux PC distincts et activation native du hook expérimental.
