# Évolution produit 0.6 — réalisation et recette

Demande du 3 octobre 2026 : réaliser les recommandations de l'audit, étape par
étape, avec tests avant livraison. Les instances existantes restent ouvertes.
Les données et historiques sont conservés. Aucun compte ni usage privé n'est
une valeur par défaut du produit.

## Ordre et critères

1. **Prévisibilité** — commandes d'arrêt exactes, états/action de résolution,
   CLI unique, pourcentages précis, visibilité des composants et fenêtre unique.
   Validation : tests de décisions, compilation, rendu des écrans.
2. **Configuration et parcours** — navigation intégrée, vue d'ensemble,
   assistant, éditeurs guidés, modèles facultatifs, simulation de routage,
   validation des références, sauvegarde/restauration et échanges sans secrets.
   Validation : configuration neuve, migration, références erronées et simulation
   sans exécution ; test visuel et clavier.
3. **Moteur** — routage explicable, concurrence par compte, réaffectation avant
   envoi, arrêt progressif, politique réévaluée, retours groupés, parents/enfants.
   Validation : droits conservés, aucun renvoi incertain, reprise et concurrence.
4. **Quotas** — politique par compte, cache partagé, superviseur de fond,
   historique des resets et rafraîchissements non bloquants.
   Validation : seuil exact, idempotence, coexistence avec l'ancienne version,
   absence de consommation dans les tests automatiques.
5. **Fichiers et exploitation** — espaces Git explicites, artefacts vérifiés,
   index d'historique, diagnostics expurgés, compatibilité, stockage, mise à jour
   et chaîne de signature. Validation : confinement, historique volumineux,
   installation portable, processus réels et qualification Codex.
6. **Livraison** — tests de non-régression, rendus, documentation, archive et
   empreinte. Une signature publique exige un certificat de signature valide ;
   une capacité de transport interne n'est annoncée qu'après qualification.

## Suivi

- État initial : arbre propre, commit 4e916c5 ; environ 10,3 Gio libres.
- Compilation légère avec le compilateur Windows existant, aucune installation
  de dépendances. Réutiliser work/test-bin pour les tests ; ne pas écraser les
  exécutables actuellement chargés par les profils.
- [x] Étape 1
- [x] Étape 2
- [x] Étape 3
- [x] Étape 4
- [x] Étape 5 (signature publique et qualification exhaustive distinctes)
- [x] Recette locale et archive ; validation GitHub consignée dans la PR

Les résultats et limites constatés seront consignés ici au fur et à mesure.

État détaillé, preuves, timings et réserves : [PRODUCT-VALIDATION.md](PRODUCT-VALIDATION.md).
