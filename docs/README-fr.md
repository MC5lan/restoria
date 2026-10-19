# Restoria

Transforme des pages d'archives numérisées en documents A4 propres — y compris sur des machines aussi anciennes que Windows 7.

Détection d'orientation, redressement, débruitage, suppression des taches, niveaux de noir et blanc et tonalité, puis mise en page A4. Un clic remplace une demi-journée de travail d'opérateur de scanner : tourner la page, la rogner, nettoyer les points, la redresser, la mettre en page.

**Un seul fichier. Double-clic pour lancer. Rien ne quitte la machine.**

- **Fonctionne sous :** Windows 7 SP1 à Windows 11 (.NET Framework 4.8, AnyCPU)
- **Interface :** entièrement dessinée avec GDI+ et mise en double tampon, pour rester fluide sur du matériel ancien ; thèmes clair et sombre
- **Code :** environ 26 000 lignes de C#, aucun paquet NuGet tiers, compilation avec le compilateur fourni avec Windows

---

## Téléchargement

Les versions publiées se trouvent sur la [page Releases](https://github.com/MC5lan/restoria/releases).

| Fichier | Description |
|---|---|
| `Restoria-setup.exe` | Installateur en un seul fichier. Double-clic, accepter la licence, terminé |
| `Restoria-portable.zip` | Sans installation. Décompressez, lancez `setup-boot.exe` |

Les deux paquets incluent les ressources IA facultatives et une couche de compatibilité Win7. L'installateur vérifie la présence du runtime .NET et s'assure avant de finir que les composants IA se chargent correctement.

> **Pas encore signé.** Les binaires ne portent pas de certificat de signature de code, donc Windows affiche un avertissement d'éditeur. Choisissez *Informations complémentaires → Exécuter quand même*.

**Depuis les sources :** `build.bat` produit `dist\Restoria.exe`. Les fonctions IA nécessitent en plus de copier le dossier `ai\` à côté (voir [Activer les fonctions IA](#activer-les-fonctions-ia)).

---

## Autres langues

[English](../README.md) · [中文](README-zh.md) · [日本語](README-ja.md) · [Deutsch](README-de.md) · Español · [Русский](README-ru.md)

---

## Ce que fait l'outil

### Le pipeline en un clic

Orientation → détection de miroir → débruitage → redressement → niveaux et tonalité automatiques → sortie A4.

- **Standard** — équilibre entre rapidité et propreté. L'orientation ne retient que la preuve physique des trous de reliure.
- **Exhaustif** — ne supprime que les poussières les plus certaines et annule son propre travail s'il commence à entamer les caractères. **Préfère laisser un point plutôt que perdre un trait d'écriture manuscrite.** Les pages sans trous de reliure basculent sur une orientation par OCR. Convient aux écritures pâles et aux fonds où le contenu prime sur la vitesse.

L'archivage ne fait que **créer de nouveaux fichiers** ; l'image d'origine n'est jamais modifiée, à l'octet près. Une exécution interrompue reprend où elle s'était arrêtée. Chaque lot produit un rapport indiquant, par page, les dimensions, l'angle de rotation et sa justification, ainsi que les comptes et les durées.

### Édition d'une page unique

| Onglet | Fonction |
|---|---|
| Supprimer | Pinceau ou sélection rectangulaire sur les dégâts ; cinq modes de remplissage : simple, renforcé, mixte, reconstruction du papier, IA |
| Rogner | Rognage manuel ou calage sur un plan A4 |
| Orientation | Rotation manuelle ou redressement en un clic (méthode par projection, ±20°) |
| Réglages | Luminosité, contraste, saturation, température, niveaux, gamma, accentuation (chaque valeur se saisit au clic) |
| Sceau | Raviver les cachets rouges délavés |
| Lot | Appliquer des paramètres fixes à un dossier entier |
| Numérisation | Dialogue direct avec un scanner WIA ; format de sortie au choix (JPG/PNG/TIF) |
| Fichiers | Renommage en masse, gestion des dossiers |
| Texte | Apposer des annotations sur la page |

### Reconstruction IA facultative (MI-GAN)

- ONNX Runtime 1.14.1 pilotant le modèle MI-GAN ; prolonge les longues structures droites comme les filets de tableau par-dessus une zone endommagée
- **Chargé dans un processus fils, jamais dans le processus principal.** Si le runtime ne se charge pas, les fonctions IA se coupent et rien d'autre n'est affecté
- Prise en charge de Win7 par renommage des tables d'importation (détails dans les commentaires de `src/Ai/AiFill.cs`)

L'ensemble du paquet `ai/` représente 30 fichiers, environ 59 Mo, et n'est nécessaire que pour la reconstruction IA et la lecture des cachets.

### Qualité de realization

- **134 tests sans fenêtre** (`--selftest`) : seuils des algorithmes, budget d'annulation, emplacement de la reprise, conflits de raccourcis
- **Régression sur la référence d'interface** (`--layout`) : preuve objective qu'une refonte de l'interface n'a rien changé
- **Quatre verrous** (`tools/gate.py`) : auto-tests, référence d'interface, artefact IA, test de fumée des performances
- **Intégration continue** : GitHub Actions compile et exécute les verrous à chaque push
- Régression de parallélisme pour le pipeline d'archivage : le mode exhaustif donne un résultat identique à l'octet en parallel=1/2/4

---

## Limites connues

À lire avant de supposer que l'outil traitera votre fonds.

- **L'orientation exige une preuve physique.** La décision à 180° repose sur les trous de reliure. Une page sans trou — un booklet collé, une feuille isolée, un document perforé sur deux côtés — **ne sera pas tournée automatiquement**. C'est un choix délibéré : faire pivoter une page de tableau déjà droite est plus grave que de laisser une page inversée que quelqu'un remarquera. Utilisez la rotation manuelle, ou le mode exhaustif (qui retombe sur l'OCR) pour les pages qui en ont réellement besoin.
- **Conçu pour la sortie de scanner à plat.** Les photos prises au téléphone présentent de la distorsion perspective et ne sont pas traitées.
- **Les seuils d'orientation sont calibrés pour environ 300 DPI.** À 600 DPI, la preuve des trous sort de la fenêtre calibrée et l'outil revient à « pas de rotation ».
- **Le runtime IA est figé sur ONNX Runtime 1.14.1**, dernière version qui se charge sous Windows 7. Avant de changer de modèle, vérifiez la compatibilité des opérateurs avec `python tools\onnx_ops.py --diff ai\migan.onnx candidate.onnx`. (LaMa a été écarté car son opérateur FFT se comporte mal dans les noyaux CPU de cette version.)
- **L'archivage refuse de travailler sur** le Bureau, la racine de C:, les dossiers système et le dossier de profil utilisateur.
- **Pas encore de certificat de signature de code**, donc Windows affiche un avertissement d'éditeur au premier lancement.

---

## Compiler depuis les sources

**Prérequis :** Windows avec .NET Framework 4.x. Rien d'autre — ni Visual Studio, ni restauration NuGet.

```bat
build.bat
```

Le résultat est `dist\Restoria.exe`. Le script de compilation :

1. Détecte `vendor\onnxruntime_c_api.h`. S'il existe, il génère la couche d'interopérabilité (`src\Ai\OrtApi.Generated.cs`) et compile avec le support IA ; sinon il compile sans IA, tout le reste fonctionne à l'identique.
2. Incruste l'icône (`assets\app.ico`, tracée comme géométrie de tracés GDI+ par `tools\IconGen.cs` — le code source est l'unique source de la conception) et le manifeste (`assets\app.manifest`, déclarant la prise en charge Win7–11 et `asInvoker` pour ne jamais demander d'élévation de privilèges).
3. Écrit un `.config` déclarant le runtime .NET 4.x.
4. Produit un PDB à côté de l'exécutable et le renomme en accord avec le nom du produit, afin qu'un rapport de plantage issu d'une machine sur place puisse être résolu avec des symboles correspondants.

> **À propos du nom du produit :** `build.bat` compile sous le nom interne `dist\PicTool.exe`, puis le copie sous le nom publié `dist\Restoria.exe` (lu dans `appname.txt`). Les deux fichiers sont identiques à l'octet près. `PicTool` est l'identifiant interne et l'espace de noms de l'assembly ; partout où un utilisateur voit le nom, c'est Restoria.

### Activer les fonctions IA

Copiez le dossier `ai\` à côté de l'exécutable :

```bat
xcopy /e /y ai dist\ai
```

Il contient `onnxruntime.dll`, `migan.onnx`, les modèles de `ocr\` et les shims de `win7\` — 30 fichiers, environ 59 Mo. Si vous n'en avez pas besoin, supprimez `ai\` avant de compiler.

### Lancer les vérifications

```bat
build.bat                                  :: compiler
dist\Restoria.exe --selftest               :: 1. auto-tests des algorithmes (code 0 = réussite)
dist\Restoria.exe --layout                 :: 2. instantané d'interface (comparer à la référence)
python tools\check_resources.py --strict     :: 3. verrou ressources (appariement LockBits)
python tools\gate.py                       :: 4. tous les verrous, test de fumée des performances inclus
```

La référence d'interface se trouve dans `tools\golden-layout.txt`. Après une modification volontaire de l'interface, régénérez-la avec `--regen-layout`.

Les verrous « artefact IA » et « performances » ont besoin de la page de test fixe `_probe\doc_test.png`, produite de façon déterministe par `Restoria.exe --make-fixture`. Elle est versionnée, donc un clone neuf la contient ; si elle disparaît, le verrou la régénère et n'échoue que si la génération elle-même échoue.

La référence de performance mémorise une étiquette de machine. Une référence provenant d'une autre machine est signalée plutôt que comparée silencieusement — reconstrivez-la en local avec `--regen-perf`.

---

## Diagnostic en ligne de commande

Modes sans fenêtre, pour les scripts et l'analyse des incidents. Les rapports atterrissent dans `%TEMP%\PicTool-*.txt`, les codes de sortie sont propres et aucune boîte de dialogue n'apparaît.

| Option | Rôle |
|---|---|
| `--selftest` | 134 auto-tests d'algorithmes et d'interactions ; code 0 = tout passe |
| `--layout` | Instantané d'interface (doit être identique ligne à ligne avant/après refonte) |
| `--archive <entrée> <sortie> [ppi] [options]` | Exécute le pipeline sans fenêtre |
| `--skew-test <fichier ou dossier> [n]` | Détection de redressement avec contrôle comparatif sur bordure noire |
| `--ai-check` / `--ai-check-silent` | Vérification du runtime IA et des modèles (silent pour installation sans surveillance) |
| `--ai-fill <entrée> <sortie> [x y w h]` | Reconstruit une zone par IA et l'écrit sur disque, pour comparaison hors ligne |
| `--paper-fill <entrée> <sortie> [x y w h]` | Reconstruit une zone de papier et l'écrit sur disque |
| `--make-fixture [chemin]` | Exporte la page de test déterministe des verrous |
| `--bench [golden] [--regen]` | Chronométrage par opérateur et empreintes des résultats |
| `--wheeltest[=dossier]` | Routage de la molette : surface, rail gauche, rail droit |
| `--uityping` | Vérification de la règle de focus, ancien et nouveau prédicat côte à côte |
| `--undotest` | Budget d'annulation et retour arrière à l'image près |
| `--cursortest` | Vérification de la forme du curseur personnalisé, laisse une capture |
| `--droptest` | Vérification de l'arbre de réception du glisser-déposer |

Commutateurs d'environnement : `PICTOOL_AI_TRACE=1`, `PICTOOL_OCR_TRACE=1`, `PICTOOL_SKEW_NOGUARD=1`, ainsi que `PICTOOL_RECOVERY_ROOT` (redirige la zone de reprise ; utilisé par les modes de test pour qu'ils ne touchent jamais les données réelles de l'utilisateur).

---

## Organisation

```
├── src/                  Tout le code source (68 fichiers .cs, environ 26 000 lignes)
│   ├── Program.cs        Point d'entrée, capture globale des exceptions, répartition des diagnostics
│   ├── Core/             Auto-tests, banc d'essai, version, reprise, passerelle Bitmap
│   ├── Platform/         Sondage système, numérisation WIA, utilitaires de fichiers
│   ├── Imaging/          Algorithmes d'image purs (sans dépendance UI) : nettoyage, amélioration, réparation, archivage
│   ├── Ai/               Couche IA facultative : interopérabilité ONNX, MI-GAN, juge d'orientation par OCR
│   └── Ui/               Interface et interactions (le seul endroit qui référence WinForms)
├── assets/               Icônes et manifestes. L'icône est tracée comme géométrie GDI+
│                         par tools\IconGen.cs, le code source étant l'unique source de conception
├── _probe/               Page de test fixe des verrous (régénérable avec --make-fixture)
├── tools/                Chaîne d'outils de compilation et de vérification
│   ├── gate.py           Les quatre verrous : auto-tests + interface + artefact IA + performances
│   ├── check_resources.py Verrou ressources (appariement LockBits)
│   ├── onnx_ops.py       Analyse de compatibilité des opérateurs ONNX (à lancer avant tout changement de modèle)
│   ├── archive_demo.py   Génération d'images de comparaison avant/après
│   ├── make_package.ps1  Chaîne d'empaquetage (compilation → assemblage → signature → auto-vérification)
│   └── install/          Code source de l'installeur et du désinstalleur
├── ai/                   Paquet IA facultatif : runtime ONNX + MI-GAN + modèles OCR + shims Win7
├── vendor/               onnxruntime_c_api.h (génère la couche d'interopérabilité)
├── docs/                 Traductions et notes d'architecture
├── build.bat             Compilation en une commande
├── CHANGELOG.md          Historique des versions
└── THIRD_PARTY_NOTICES   Licences de tous les composants tiers embarqués
```

Les dépendances s'écoulent dans un seul sens ; les références inverses sont interdites :

```
Ui ──→ Imaging / Ai / Platform / Core
Ai ──→ Platform (SysInfo)
Imaging ──→ Platform (quelques)
Platform ──→ (rien)
```

`Imaging` et `Ai` ne doivent pas mentionner `System.Windows.Forms` ; les algorithmes prennent des paramètres et renvoient de nouvelles images bitmap au lieu de modifier un état global.

---

## Composants tiers et licences

Le code de Restoria lui-même est sous licence MIT (voir [LICENSE](../LICENSE)). Les composants ci-dessous sont inclus dans la version publiée et restent sous leurs propres licences :

| Composant | Version | Copyright | Licence |
|---|---|---|---|
| ONNX Runtime (`ai\onnxruntime.dll`, `ai\win7\onnxruntime.dll`) | 1.14.1 | © Microsoft Corporation | MIT |
| Modèles de détection et de reconnaissance PP-OCRv3 et dictionnaire (`ai\ocr\`) | PP-OCRv3 | © PaddlePaddle Authors | Apache-2.0 |
| Modèle de reconstruction MI-GAN (`ai\migan.onnx`) | 512, ICCV 2023 | © 2024 Picsart AI Research | MIT |
| Shims de compatibilité API-set Win7 (`ai\win7\*.dll`) | — | Redirigeurs Microsoft | MIT |

Les textes complets des licences figurent dans **[THIRD_PARTY_NOTICES](../THIRD_PARTY_NOTICES)**.

Ces composants servent **uniquement** à la reconstruction IA facultative et à la lecture des cachets. Le pipeline central — orientation, redressement, débruitage, suppression des taches, niveaux, tonalité, sortie A4 — est du C# pur sans dépendance native et fonctionne entièrement sans le dossier `ai\`. Le programme sonde, dans un processus fils, si le runtime IA peut se charger, et désactive proprement ces fonctions sinon ; aucun chemin de code IA ne s'exécute dans le processus principal.

## Remerciements

- [ONNX Runtime](https://github.com/microsoft/onnxruntime) — moteur d'inférence multiplateforme
- [MI-GAN](https://github.com/Picsart-AI-Research/MI-GAN) — modèle de reconstruction d'image, ICCV 2023
- [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR) — modèles de détection et de reconnaissance PP-OCRv3