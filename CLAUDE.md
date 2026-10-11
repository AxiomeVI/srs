# srs — Contexte & architecture

Mod Everest **Speedrun Sheet** (`srs`), dépendant de SpeedrunTool : importe les temps de référence d'une practice sheet communautaire (Google Sheets → CSV local) et colore le temps final d'un segment (temps de chapitre, affiché au-dessus du room timer de SpeedrunTool) selon les paliers de la sheet.
Le `README.md` sert de **notes d'utilisation** et `CHANGELOG.md` de **changelog** : mettre ce dernier à jour à chaque feature.

## Build

- .NET 8, projet unique `srs.csproj`. Sur cette machine : `~/.dotnet/dotnet build -p:CelestePrefix="$HOME/.steam/steam/steamapps/common/Celeste"`.
- `SpeedrunTool.dll` extraite du `SpeedrunTool.zip` installé (cible `ExtractSpeedrunToolDll`), version épinglée dans `everest.yaml` ; sans zip, une `SpeedrunTool.dll` déjà extraite dans `$(CelestePrefix)` sert de référence. Pas de Publicizer : tout passe par le ModInterop et les classes publiques.
- Cible `OutputAsModStructure` : génère `build/` (`bin/` DLL+PDB, `everest.yaml`, `Dialog/`) → **à zipper** dans `<Celeste>/Mods/`, et copie la DLL dans `bin/` à la racine du repo. **Le sous-dossier `bin/` est celui qu'`everest.yaml` désigne** (`DLL: bin/srs.dll`) : le déplacer sans toucher au yaml fait échouer le chargement du module, sans rien dire.
- `<Compile Remove="Tests/**" />` dans le csproj : **ne pas retirer** (le SDK globbe `**/*.cs`, sans ça les sources de test partiraient dans `srs.dll`).

## Tests

`cd Tests && ~/.dotnet/dotnet test` (xUnit). Le projet de test `<Compile Include>` directement les sources game-free au lieu de référencer `srs.dll` ⇒ aucune dépendance à Celeste. **La liste fait foi dans `Tests/srs.Tests.csproj`** ; ne pas la recopier ici, ni son compte. Un fichier y entre le jour où il n'a plus de `using` du jeu, écran ou non. **Corollaire : aucun de ces fichiers ne prend de `using` du jeu**, sinon le projet de test ne compile plus.

`SheetConsistencyTests` croise les tables hardcodées entre elles et avec les CSV réels de `Tests/Fixtures/` (snapshots des onglets, dont les tests ne lisent que la **structure** : les seuils bougent tout le temps). **Rafraîchir les fixtures est le mécanisme de détection** d'un renommage côté sheet. En jeu, `SheetData.MissingRows` fait le même contrôle et nomme les lignes manquantes dans `log.txt`, **jamais dans le menu** : c'est l'affaire du mainteneur.

## Architecture

Le code porte ses justifications en commentaires ; ici, seules celles qui dépassent un fichier.

- `SrsModule.cs` — **L'ordre des `Load()` fixe l'ordre des hooks `Level.Update`** : chaque Load enveloppe les précédents, donc après `orig` la frame déroule `Hotkeys`, `RunWatcher`, `TierComparison`, puis `ExportMenu`. **Aucun `Load()` ne peut passer devant `Hotkeys`** : les deux derniers lisent un `Pressed` de la frame courante. **Toute sauvegarde des settings passe par `SrsModule.TrySaveSettings`** : `SaveSettings` d'Everest lève, et un appel nu depuis un menu ou un hook fait planter le jeu.
- **Master switch** : `Settings.Enabled` est un gate runtime, jamais un `Unload` — les hooks restent posés et chaque handler sort en tête (`TierComparison` Update *et* Render ; `ExportMenu` ferme en plus l'écran). Le premier frame nourri après un rallumage **lâche** ce qui était ouvert. Éteint, `SheetImporter.Load()` ne rafraîchit pas ; rallumer le relance.
- `Source/UI/` — les hotkeys viennent de **CelesteHotkeys**, **copié** dans `CelesteHotkeys/src/` par son `tools/vendor.sh` (commit `chore: CelesteHotkeys <sha>`, jamais un submodule : le dépôt source est privé). **Ne pas modifier la copie ici** : corriger dans CelesteHotkeys puis re-vendoriser. Le module lit un `ButtonBinding` comme un **combo** et fournit l'écran. **Règle propre à srs** (`Hotkeys.Pressed`) : niveau en pause, seule la hotkey d'export répond, et seulement derrière la pause que l'écran d'export tient lui-même. `ModMenu` construit **toute** la section, le bouton des raccourcis en dernier à la racine ; tout est `[SettingIgnore]`, pour que le master switch masque chaque entrée.
- **`Keys.None` ne doit jamais entrer dans un binding** : FNA le renvoie pour toute touche absente de sa table SDL→XNA (le `)` en AZERTY) puis le rapporte tenu ⇒ `Bindable.Sanitize` dans `Hotkeys.Load()` nettoie le disque, et l'écran du module le refuse à la capture, avec F1/F2/F3/F5.
- `SheetUrls.cs` — **game-free** : `Migrate` substitue **l'id seul** du classeur gelé dans une URL stockée (copie Drive : les gids restent valables). Appelé depuis `SrsModule.LoadSettings()` après `base.LoadSettings()` : repointer les constantes seules ne déplace aucun joueur ayant déjà sauvegardé (sérialisées bien que `[SettingIgnore]`). Les défauts de `SrsSettings` sont construits sur `EditUrlPrefix`, pour ne pas diverger de la cible de la migration.
- `SheetData.cs` — **game-free** : CSV RFC 4180, temps `[hh:][mm:]ss[.fff]` en culture invariante, modèle bloc/segment. Ne lève jamais d'exception sur contenu malformé.
- `SheetImporter.cs` — six onglets (`StandardsTabs`) + caches `Saves/srs/*.csv` (écriture atomique, tout-ou-rien), rechargés au `Load()` ⇒ hors-ligne OK ; des CSV déposés à la main y sont relus. Le `Load()` rafraîchit en fond (`BeginUpdate`), et *Update Standards* rejoint ce téléchargement au lieu d'en lancer un second. **`Data` est réassignée depuis un worker** : les lecteurs la relisent à chaque frame en `?.` et adressent les lignes par nom (`block.Find`).
- `SheetRows.cs` — **une seule table** pour les deux documents : chaque ligne importée, sa ligne `Standards` (`Tab`, `SheetChapter`, `Label`), son adresse srs (`Scope`, `Name`), son `Anchor` (checkpoint jeu, en anglais) et, **seulement là où l'onglet de saisie l'orthographie autrement**, un `Target`.
- `SegmentAutoDetect.cs` (+ `.Names.cs`, game-free) — `ScopeOf` et les tables hardcodées, clés en noms de checkpoints jeu. Ces noms sont des clés dialog traduites ⇒ `Dialog.Clean` **forcé en anglais** partout.
- **Checkpoints virtuels** : ce que la sheet coupe dans un checkpoint (8A `HotM Horizontal`, les trois drafts du 3000m de 7A) se déclare dans `SheetRows`, `StartRoomOverrides`, `SplitCheckpoints` et `EntryRooms`. Le dernier d'une chaîne doit terminer le chapitre.
- **Le jeu change de room sans transition** : chaque réveil (2A `end_0`, 5A et 5B `c-00`, la chute de 6A) pose `Session.Level` et rappelle `LoadLevel` sans `OnTransitionTo` ⇒ jamais de hook de transition : `RunWatcher` classe chaque `LoadLevel` par son intro.
- `Source/Engine/` — **le moteur, game-free et testé**. `SegmentRules` dérive une `SegmentRule` par ligne des marqueurs du libellé et de `RowTraits` (ce qu'il ne dit pas), en noms de checkpoints jeu, jamais en rooms (elles viennent d'`AreaData`, que seul le thread de jeu lit). `RunTracker` suit **tous les segments qui peuvent tourner à la fois** ; un temps est la différence de deux lectures de `Session.Time` (la fin de chapitre compte la frame d'arrêt). Invariants : `CurrentRoom` (les `Start`, les lignes d'après un réveil) s'ouvre au départ ; `NextRoom` (le reste) **et les lignes d'après un réveil** s'ouvrent à l'entrée de leur 1re room **depuis leur room d'entrée** (`EntryRooms`), cinématique comprise, et le segment qui finit là ne s'y ferme avec un temps que sur cette même entrée ; `MapSpawn` (`RowTraits`) ne s'ouvre que sur le spawn de son checkpoint ; `Restart` lâche tout ; les fermetures passent avant les ouvertures ; **rien à 0 ou moins n'est enregistré**. `Specificity.MostSpecific` choisit, parmi ce qui se ferme ensemble, la règle qui exige tout ce qu'exigent les autres, à part pour les segments et pour les runs de chapitre (`ChapterRun`, `RTM` d'IL compris) : la ligne de tier montre le chapitre, le segment un cran avant. `RoomMap` (**hors** d'`Engine/`, couplé au jeu) résout les rooms **sur le thread de jeu** ; la room de fin d'un segment est la room de départ du suivant.
- `RunWatcher.cs` — l'adaptateur, en hook `Level.Update`. **Les segments se chronomètrent au temps de chapitre (`Session.Time`), jamais au chrono de SpeedrunTool.** Les pièges :
  - `Saved` est enregistré auprès du SaveLoad de SpeedrunTool : **un load remet un `Stamp` plus ancien, lu avant `orig`**. Chaque load est une nouvelle tentative, plus une frame si « freeze after load » est Off et qu'il a son fondu.
  - **Seule une entrée à pied nourrit `RoomEntered`.** Une mort est le `Respawn` de `Level.Reload` ; **tout autre `Respawn` est un téléport de SpeedrunTool**, même dans sa room : un `Restart`.
  - `Saved.From` **reste `start` pendant `StReflectionFall`**, et la chute surveillée de 6A **disqualifie** au lieu de lâcher : Lake s'ouvre quand même.
  - **`Restart` et `Reentered` testent un point contre les spawns de départ** (`AtStartSpawn`, à 1 px près) : load avec contrôle, `Saved.SpawnAt`, et rien si `Saved.Moved` ; sans contrôle, l'apparition qui suit ; loader, téléport, 1re room remise à zéro : `Session.RespawnPoint`.
  - **`Saved.Moved` est posé à chaque frame éteinte** : un savestate chargé éteint n'ouvre rien.
  - **Restart Chapter se voit à `Session.Restart()` sans room** ; un restart de golden nomme sa room et n'en est pas un.
  - **Quitter le niveau nourrit `Left`** (`Level.OnExit` dans `orig` ; `Completed` qui monte). Le loader suivant n'est une reprise (`Reentered`) qu'après un Save and Quit, même room et même `Session.Time`, ou sur le spawn carte d'un checkpoint du même chapitre, où la ligne de tier reste. Load, mod éteint, téléport l'oublient.
  - **Fraises** : `Strawberry.OnCollect` nourrit le tracker (moon berry comprise), celles qui suivent passent dans `EndState` ; `RoomMap` lit la carte, **non résolu = jamais enregistrée**. Les gems se comptent (`RegisterSummitGem`).
  - **Sans le SaveLoad de SpeedrunTool, srs n'enregistre rien** (un load n'y serait pas vu).
- `Source/Export/` — **export des temps de session vers la copie personnelle de la sheet**, protocole v2 (`band` renvoyé, `version: 2`). **Un export part par requêtes de 25 lignes** (`ExportBatches`), chacune après la réponse de la précédente : le script verrouille.
  - `SessionBests` garde le meilleur **de chaque ligne** (`RunBook`), par nom, pas par checkpoint jeu (sinon `Hollows Tape RTM` passerait pour `Hollows`), **toute la session, tous chapitres** ; l'écran : côté joué, puis ordre de `SheetRows.All`, ARB sous leur côté.
  - **L'écriture est un compare-and-swap** : chaque ligne porte la cellule *brute* lue (`PendingUpdate.RemoteCell`, jamais reformatée : la sheet écrit `1:36.9` là où `TimeFormat` rend `1:36.900`), le script la refuse si elle a bougé.
  - **`SheetReader` seul lit la sheet** et seul écrit ce que srs en garde (`RemoteBests`, copie `Saves/srs/sheet-times.json` : empreinte de l'URL, pas l'URL). Un fetch à la fois, rejoint s'il vise l'URL courante depuis la dernière écriture. **Le compteur d'écritures monte avant et après un POST** : le script sert les anciennes cellules jusqu'au bout. Décisions, publication, copie, `Forget` : **un verrou**, jamais le réseau.
  - `TextMenu` : thread de jeu seul ; mod éteint : file vidée, écran fermé. `ExportMenu` reçoit le POST par une file vidée en `Level.Update` ; `ExportUrlMenu` (aussi au titre) par des `volatile` lus en `OnUpdate`.
  - `ExportTarget` écrit l'URL par `AtomicFile` (ne lève jamais, efface le `.tmp` : l'URL).
- `AppsScript/SETUP.md` — le déploiement du Web App par le joueur. **Le script n'est pas dans ce dépôt** : le template de la sheet le porte (`srsExport.gs`) et lit toutes les bandes des onglets de saisie. Il apparie les lignes **par libellé normalisé**, jamais par position, et aucun test d'ici ne le fait tourner.
- `TierComparison.cs` — **au-dessus** du timer. Le PB lit `RemoteBests` **une fois** par record, pour qu'un export ne l'ôte pas.
- `Dialog/English.txt` + `French.txt` — pas de placeholders `{0}` (`Dialog.Clean` efface les accolades) : composer en code. **Exceptions** : `SRS_KEYBIND_TIMEOUT` et `…PAGE_COMBO_HINT`, lus bruts (`Dialog.Get`).

## Données de la sheet

**La sheet n'est pas encore annoncée publiquement : ne pas la nommer dans les fichiers du repo.**

- Lue sans compte (partage par lien) par `…/export?format=csv&gid=<gid>` ; les gids des autres onglets sont dans le HTML de la page d'édition.
- **Deux familles d'onglets, jamais confondues** : la lecture prend les six onglets `… Standards`, l'écriture vise les quatre onglets de temps personnels `A Sides` / `B+C Sides` / `Farewell` / `ARB/Full Clear`, et la correspondance n'est pas un suffixe (`B Sides Standards` → `B+C Sides`). Le `Label` de `SheetRows` reproduit le nom brut de la sheet, fautes de frappe comprises ; une différence entre les deux documents est un `Target`, jamais une retouche du `Label`.
- Les colonnes **sont** les noms des paliers (`Hidden, WR, Gold, Pink, Purple 1-3, …, Red 1-3, Unranked`) ⇒ `TierColors` est indexée par nom de colonne **complet**, le suffixe de rang compte. Farewell n'a pas de colonne `Chapter`. Le nom de chapitre n'est que sur la 1re ligne d'un groupe ⇒ reporté au parsing.
- Variantes marquées par emoji (`💙` cœur, `📼` cassette, `💎` gem) à l'**espacement irrégulier** ⇒ `Contains`. **L'emoji ne survit jamais à l'import** : `ActiveFont` saute en silence un caractère absent de son atlas, donc les lignes gardées sont renommées d'après ce qu'elles collectent (`Hollows Tape RTM`, `Shrine Heart Clear`), suffixe ASCII gardé.
- **`RTM`, `RC` et `to Heart` (ARB) sont les seuls suffixes qui arrêtent une run avant la fin de son segment** (`RTM` au collect, `RC` au Restart Chapter, seulement sur `2a Start 💙 RC`). `Clear` sur un checkpoint A-side veut dire « on ramasse et on continue », *pas* « fin du chapitre » (cf. `SegmentRules.EndOf`).
- Les lignes `Wake Up` **ne s'importent pas** (décision du propriétaire, épinglée par `LeavesTheNotYetSupportedRowsOut`). `Hidden` vaut `0:00.000` partout ⇒ ignoré (`threshold > TimeSpan.Zero`). **Palier atteint : temps strictement sous le seuil ; la colonne `WR` ne classe rien** (`Gold` est le meilleur), comme la sheet.

## Repères SpeedrunTool

- Aucun temps ne vient du chrono de SpeedrunTool. Toute nouvelle lecture passe par l'interop.
- **Le format des temps est celui de SpeedrunTool, atteint par réflexion** (`SrsModule.AdoptSpeedrunToolsTimeFormat`, avant tout le reste du `Load`). **srs ne garde aucune copie du format** (sauf dès une heure, que SpeedrunTool n'écrit pas : `TimeFormat.WithHours`) : si la recherche échoue, `Load` lève — un second formateur dériverait en silence et écrirait dans la sheet un temps qui ne correspond plus au timer (décision maintenue : ne pas proposer de formateur local). La doublure des tests (`Tests/TimeFormatStandIn.cs`) **n'est pas une spécification** : n'y adosser aucune assertion sur une chaîne de temps exacte.
- ModInterop `SpeedrunTool.SaveLoad` : **l'état statique qui décrit l'état de jeu et est muté en gameplay doit y être enregistré** (`RegisterStaticTypes`) sous peine de desync ; `RunWatcher` n'y enregistre que `Saved`. **Ce qui décrit la *session* en est exclu, volontairement** (`SessionBests`, `RemoteBests`) : la question est « un retour en arrière du jeu doit-il l'annuler ? ». Ne pas écrire qu'un load restaure `Session.Time` : sous `SaveTimeAndDeaths` off il garde le plus grand des deux, et rien n'en dépend puisqu'aucun temps ne traverse un load.
- **Téléport de room** (PageUp/PageDown) : `LoadLevel(Respawn)` sans loader, `Session.Time` jamais baissé, **puis `level.Update()` direct depuis sa hotkey (dans `MInput.Update`) : tous les hooks `Level.Update` de srs tournent deux fois sur cette frame**, à la suite : `RunWatcher` n'est pas réentrant. Rien d'autre ne signale le téléport, d'où `teleported` dans `RunWatcher`.
- **Pris hors interop, donc à revérifier à chaque release de SpeedrunTool** : `SpeedrunToolSettings.Instance` (`RoomTimerType`, `FreezeAfterLoadStateType`), `PopupMessageUtils`, `DialogIds`, `RoomTimerData.FormatTime` par réflexion, le comportement de `TeleportRoomUtils`, et le refus d'un load pendant la pause (dont `ExportMenu` dépend). Rien ne les enveloppe : un renommage casse srs.

## Checklist « nouvelle fonctionnalité »

1. `Source/<Feature>.cs` statique avec `Load()`/`Unload()`, appelés depuis `SrsModule` (attention à l'ordre des hooks).
2. Option de menu → settings + clés dialog EN **et** FR ; entrées dans `ModMenu`. Sauvegarde par `SrsModule.TrySaveSettings`, fichier écrit sur le thread de jeu par `AtomicFile`.
3. Réseau → jamais sur le thread de jeu, timeout, erreurs loggées jamais levées. **`ExportTarget.Url` n'entre dans aucun log, aucun message, aucun champ pré-rempli : l'URL *est* le credential du Web App.** Elle vit dans `Saves/srs/export-url.txt`, **jamais dans le fichier de settings**, que les joueurs partagent pour demander de l'aide. Une tâche de fond ne construit ni ne détruit d'item de `TextMenu` ; muter le `Label` d'un item existant est sûr.
4. État statique décrivant l'état de jeu et muté en gameplay → interop `SpeedrunTool.SaveLoad`. Ce qui décrit la session s'en exclut.
5. Logique game-free → test dans `Tests/` ; une table hardcodée touchée ⇒ `SheetConsistencyTests` et `SegmentRulesTests` doivent passer. Ne pas écrire de nombre ici.
6. Build, zipper `build/` dans `<Celeste>/Mods/`, tester en jeu.
7. `CHANGELOG.md` (+ bump `Version` dans `everest.yaml` si release).

## Workflow git

Branche `feature/<nom>` ou `fix/<nom>` depuis `dev`, PR vers `dev`, release = merge dans `main` + bump de version mineure.

## Reste à faire

- **Hors import** : les lignes dont la route revient à la carte et cinq lignes chapitre (liste : `LeavesTheNotYetSupportedRowsOut`). **Deux lignes ne sont jamais indiscernables** (`NoTwoRulesAreIndistinguishable`) : une ligne plus large se décrit par ce qu'elle *exige* en plus.
- **Le progrès de session** (portes, clés, blocs cassés) survit à un retour en arrière, à la debug map sans Ctrl et à un téléport : srs ne s'en protège pas (décision du propriétaire, à repenser).

## Écarté définitivement

- **Sheets privées en *lecture* (OAuth / service account)** : un flux d'autorisation Google à la charge du joueur. L'*écriture* passe par le Web App du joueur, dont l'URL `/exec` est le seul credential.
