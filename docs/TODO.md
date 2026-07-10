# TODO / Bugliste

**Product:** YokWare Branch **0.9.2** Path B · Live wire: **Horde protocol 19** (not Ironbark)

## Open (actionable)

- [x] **Host walk/run anim (#6)** — code: unreliable anim resend every 0.75s + pause/fps apply polish (`PlayerAnimSync`). **Live 2-box confirm still recommended.**
- [x] **Audit C1–C4 / H1 / H3 / H5 (0.9.2)** — host-only time, dialog world-only, chapter auto-resume, fail-loud world share, client→host flags, night death world-mutation suppress, NPC talk lock. See CHANGELOG / README known limits.
- [x] **H6 container loot race: precise refund** — HandleContainerTakeDenied now uses pre-take player inventory count to refund only the optimistically-added items instead of blindly removing by type. No more over-removing when player already had items of that type.
- [ ] **Location/landmark placement residual (#5)** — mitigated: `WorldGenSharePatch` blocks client `WorldGenerator.onFinished` while connected (client cannot generate divergent worlds). Full determinism requires a heavy worldgen rewrite (location assignment depends on processing order). Only a real risk if the initial world share fails before either side generates.
- [ ] **Live 2-instance / dedicated campaign playtest** — CI covers protocol + PathB structural/policy tests; full campaign still needs human 2-box.
- [ ] **Credits continuous co-op** — residual by design: credits stop network permanently; chapter mid-campaign auto-resumes.
- [ ] **Dedicated Ironbark ↔ Horde bridge** — `DarkwoodMP.Server` is not a Path B LAN peer.
- [ ] **SyncCheck / full InteractionLock / ItemState upgrades** — deferred.

## Archive — Playtest-Durchgang (2-Instanz) — fixed items & history

Erste echte 2-Spieler-Tests. Ergebnisse und Stand:

- [x] **PvP-Nahkampf „einmal treffen, dann nie wieder"** (`PvpHit_Patch`): der
      Melee-Dedupe verwendete `(sensorInstanceID ^ targetId)` als PERMANENTEN
      Schlüssel. Der `MeleeSensor` ist aber eine dauerhaft wiederverwendete
      Komponente → nach dem ersten Treffer war jeder weitere Schwung auf dasselbe
      Ziel blockiert. Fix: Zeitfenster-Dedupe (0,25 s) statt permanent — deckt den
      Mehrfach-Collider-Treffer eines Schwungs ab, erlaubt aber den nächsten.
- [x] **PvP-Schuss macht keinen Schaden** (`PvpHit_Patch.BulletPostfix`): der
      Recast entlang der Zielrichtung übersprang Trigger-Collider — der
      Remote-Spieler-Klon hat aber genau einen Trigger-Body-Collider (deshalb
      erkennt der Nahkampf-Sensor ihn überhaupt). Fix: Klon-Erkennung ignoriert
      Trigger NICHT mehr; ein solider Blocker (Wand) zwischen Schütze und Klon
      unterbricht den Schuss weiterhin. Zielrichtung `_transform.up` per IL gegen
      `Player.spawnBullet` bestätigt (war korrekt).
- [x] **Barrikaden/Weltzustand beim Join nicht übertragen (dedizierter Server)**
      (`NetworkManager`): der Join-Snapshot (Movables, Türen, Schalter, Builds,
      Barrikaden, Schlösser) war an `IsHost` gekoppelt. Auf einem DEDIZIERTEN
      Server ist NIEMAND `IsHost` → der Snapshot wurde nie gesendet, eine offline
      gebaute Barrikade blieb beim Partner unsichtbar (erst ein späteres
      Live-Dismantle zeigte sie kurz). Fix: Gate auf `IsTimeAuthority` (niedrigste
      Client-ID = der Host, wenn es einen In-Game-Host gibt) → genau eine Maschine
      sendet den Snapshot. Rest-Grenze: bei 3+ Spielern sendet nur die Autorität;
      besitzt ein NICHT-Autoritäts-Client exklusiven Weltzustand, fehlt er noch.
- [x] **Items auf denselben Punkt droppen wurden beim Partner nicht angezeigt**
      (`ItemSync.FindExistingDrop`): die Rejoin-Dedupe adoptierte JEDES
      gleichnamige Item im 0,75-m-Radius — ein zweiter gleichartiger Drop am
      selben Fleck wurde auf den ersten „adoptiert" statt gespawnt. Fix: bereits
      unter einer Sync-ID getrackte Items werden übersprungen.
- [x] **Munition/Haltbarkeit bleibt beim Droppen+Aufheben nicht erhalten** (#1):
      Der Drop übertrug nur `type`+`amount`; der gespiegelte Drop entstand via
      `spawnDroppedInvItemm(bool,string,int)` mit Default-Zustand. Per Decompile
      (ilspycmd) definitiv geklärt: ein gedropptes Welt-`Item` legt seinen
      Zustand in `Item.GetComponent<Inventory>().slots[0].invItem` ab
      (`spawnDroppedInvItem` schreibt dorthin via `invSlot.createItem`,
      `Item.getDroppedItem` liest GENAU diesen Slot zurück in den Spieler).
      Fix: `PickupStatePacket` um `Durability`/`Ammo`/`ModifierQuality`/
      `Modifiers` erweitert (Mod UND Server parallel, gleiche Wire-Reihenfolge —
      Mod ist Wire-Wahrheit, auch der Server-Snapshot-Resend sendet jetzt das
      volle Feldset). Sende-Seite (`PlayerDrop_Patch`) liest den Zustand direkt
      vom gerade erzeugten Welt-Item ab (einheitlich für beide Drop-Pfade),
      Empfangs-Seite (`ItemSync.SpawnDrop`) schreibt ihn auf den Slot des
      Spiegels (`ItemState`-Helfer). `Durability < 0` = „kein Zustand" (Würfe,
      Aufheben-Removals, Alt-Snapshots) → Spiegel bleibt unberührt, kein
      Fehlapply. Der Server persistiert den Zustand jetzt auch in `PickupState`
      (Late-Join-Snapshot über Dedicated Server bleibt vollständig).
      - Bewusste Restgrenze: `upgrades` (List<`ItemUpgrade`>, MonoBehaviour-
        Asset-Referenzen) werden NICHT mitgesendet — das bräuchte eine
        Namens-Registry aller ItemUpgrade-Assets; selten (Waffen-Upgrades) und
        gegen Dupe-Risiko abgewogen zurückgestellt. Modifiers (reine Daten:
        `type`/`strengthType`-Enums + `strength`/`isAttachment`) reisen voll mit.
      - Wie alles hier: Code+Decompile-verifiziert, 2-Instanz-Playtest offen
        (Item-Integrität/Dupe: der Wert wird gesetzt, nicht neu erzeugt — Items
        wurden beim Drop ohnehin aus dem Quell-Inventar entfernt, kein Dupe).
- [x] **Mündungsfeuer bei anderen Spielern** (#3): `WeaponFire_Patch` →
      `FiredWeaponPacket` + `RangedSync.OnRemoteFired` (Muzzle-Prefabs am Klon).
      Impact/Blood: `BulletFX_Patch` → `BulletFxPacket`. Live-Playtest optional.
- [x] **Bein-/Lauf-Animation beim Host** (#6): code hardened (resend + pause/fps);
      live confirm under Open above.
- [x] **Welt out of sync — Bäume & Streuobjekte** (#5, Kern behoben): per IL
      lokalisiert. Der INITIALE Weltinhalt entsteht synchron pro Chunk in
      `WorldChunk.populate` → `createGroundSprites` (Boden + BÄUME) und
      `WorldChunk.spawnMiscObjects` → `distributeObjects` (Streuobjekte). Beide
      waren nur auf WorldGenerator-PHASENebene geankert (EIN Reseed für ALLE
      Chunks). Da die Generierung eine Mehrframe-Coroutine ist, ließ jede
      Per-Chunk-Drift (oder irgendein Nicht-Worldgen-`Random`-Aufruf zwischen den
      Frames) den gemeinsamen Stream ab dem Chunk für ALLE folgenden Chunks
      auseinanderlaufen → andere Bäume/Objekte trotz gleichem Seed. Fix: jeder
      Chunk-Inhalt wird jetzt über seine POSITION geankert (`ChunkGenSeedPrefix`,
      `seed ^ chunkPos ^ salt`) → reine Funktion von (Seed, Chunk-Position),
      unabhängig von Reihenfolge/Timing. Fundament per IL bestätigt:
      Chunk-Positionen sind ein reines Gitter (kein RNG), Biome-Wahl ist
      counter-geankert in einer sauberen Erzeugungsphase → auf beiden Maschinen
      identisch. Dies ist derselbe Per-Chunk-Trick wie v0.5.1 für die TÄGLICHEN
      Spawns, jetzt auf die einmalige Weltgenerierung ausgeweitet.
      - Voraussetzung unverändert: BEIDE Maschinen (a) identischer `WorldSeed`
        in der Config und (b) ein NEUES SPIEL damit (kein Laden eines alten
        Saves, das mit anderem/keinem Seed erzeugt wurde). Log `[WorldGenSeed]
        ... seed N` muss auf beiden gleich sein.
- [x] **Welt „komplett anders" — BIOME grid-geankert** (Kern des Total-Drifts):
      `createNextWorldChunk` wählt pro Chunk das Biome
      (`biomePresets[Random.Range(0,n)]`). Das war counter-geankert; der Counter
      wird von JEDER Phase erhöht, sodass eine abweichende Phasen-Verschränkung
      die Biome-Wahl — und damit die GESAMTE Welt — auseinanderlaufen ließ. Jetzt
      per Chunk-GITTERkoordinaten geankert (`chunkColumn`/`chunkRow` live aus dem
      Generator) → reine Funktion des festen Chunk-Gitters, timing-/reihenfolge-
      unabhängig. Per IL bestätigt: das Spiel selbst ruft NIE `Random.InitState`;
      nur der A*-Pfadfinder nutzt `System.Random` (keine Welt-Layout-Relevanz).
- [x] **Diagnose: Welt-Signatur-Log** (`WorldSignaturePostfix` auf
      `spawnPlayer`): loggt `WORLD SIGNATURE seed=N chunks=C hash=XXXXXXXX`
      (Hash über alle Chunk-Positionen+Biome, deterministisch sortiert). Beide
      Spieler vergleichen EINE Zahl: gleich = identisches Layout; ungleich =
      Layout driftet weiter (dann sind seed/patch/Preset zu prüfen). Ersetzt das
      Raten per Sichtvergleich.
- [x] **In-Game-Zeit driftet (~15 Min)** (`WorldSync`): nur DISKRETE Ereignisse
      (Tag/Nacht, Schlaf, Tod) waren gesynct, die laufende `Controller.gameTime`
      lief pro Maschine frei. Die Zeit-Autorität broadcastet jetzt alle 5 s ihre
      Uhr (`clock:<day>:<gameTime>:<currentTime>`); Nicht-Autoritäten slaven bei
      Drift ≥ 2 Einheiten (nur am selben Tag; Tageswechsel gehört dem
      Tag/Nacht-Sync). Setzt dieselben Felder wie der bereits erprobte
      Tod-/Schlaf-Uhr-Sync.
- [ ] **Welt out of sync — Locations/Landmarks-PLATZIERUNG** (#5, Restgrenze):
      WELCHER Chunk eine Location/ein Landmark bekommt, hängt von
      reihenfolge-abhängiger, teils rekursiver Auswahl ab
      (`getNotPopulatedChunkOfDifficulty`, `getLocationName` rekursiv mit
      Kollisions-Retry, `getLandmarkPos` counter-geankert). Der PRO-CHUNK-INHALT
      (Bäume/Objekte) ist jetzt deterministisch; ob dieselbe Location im selben
      Chunk landet, hängt noch an der Phasen-Reihenfolge. Falls Locations weiter
      driften: Auswahl-Reihenfolge deterministisch machen (schwerer, braucht
      Playtest-Vergleich Chunk für Chunk).

## Status nach dem Bugfix-Durchgang vom 2026-07-04 (alle Fixes kompiliert,
Codepfade gegen Assembly-CSharp verifiziert; In-Game-Test mit 2 Clients steht aus):

- [x] container sync system überarbeiten (ist oft sehr unreliable)
      → Updates für nicht geladene Container werden jetzt gespeichert und beim
        Öffnen/periodisch nachgezogen statt verworfen (ContainerSync: pending store).
- [x] aufheben von beartraps/shrooms und anderen aufhebbaren objekten syncronisieren
      → Item.disarm/switchTriggerState gepatcht (Disarm_Patch), Gegenseite
        spielt switchTriggerState nach (inkl. pending retry für ungeladene Bereiche).
- [x] torches, Flares bzw lighting syncronisieren
      → Weltlampen/-fackeln über Item.turnOn/turnOff (ItemSwitch_Patch);
        gehaltene Lichter (Fackel/Taschenlampe/Flare) über InvItemClass.switchActive
        (ItemActive_Patch + HeldLightSync, spawnt das echte lightEmitter-Prefab
        am Remote-Spieler); geworfene Flares/Molotovs landen über Throw_Patch
        an der richtigen Stelle und zünden dort.
- [x] generator tank und strom an/aus sync
      → An/Aus läuft über Item.turnOn/turnOff (deckt Generator ab, verifiziert:
        Item.turnOn -> Generator.turnOn); Tankfüllung über GeneratorFuel_Patch
        (absoluter Füllstand). Beides im Join-Snapshot enthalten.
- [x] crate war nur beim host sichtbar (position von crate nicht vom seed abhängig ?)
      → Ursache: Controller.startDay -> WorldGenerator.spawnRandomObjects würfelt
        täglich mit ungeseedetem RNG. Jetzt pro Tag deterministisch geseedet
        (seed ^ tag), Tagesnummer ist ohnehin synchron.
- [x] gegner sind absolut out of sync
      → Drei Ursachen behoben: (1) Client fror ALLE Gegner ein (Statuen, sobald
        die Spieler getrennt waren) - jetzt claim-basiert: nur vom Host
        gemeldete Gegner werden übernommen, der Rest simuliert lokal weiter.
        (2) Host meldete "aus Reichweite" als IsAlive=false - Clients haben
        diese Gegner GETÖTET; jetzt eigenes "gone"-Signal + Freigabe zurück an
        die lokale KI. (3) Client-Schaden an host-eigenen Gegnern verpuffte -
        wird jetzt an den Host weitergeleitet (enemyhit), Tod kommt über den
        normalen Sync zurück. Außerdem: Leichen spammen den Enemy-Kanal nicht
        mehr (Tod wird genau einmal gesendet) und Leichen werden nicht mehr
        geclaimt/bewegt.
- [x] friendly fire aktivieren
      → Nahkampf/Kugeln treffen die Remote-Spieler-Klone bereits (Collider +
        CharBase vorhanden); Player.getHit auf Klonen wird jetzt abgefangen,
        als "pvp:<id>:<dmg>" verschickt und beim echten Spieler angewendet
        (CharDamage_Patch + DamageSync.ApplyRemotePlayerHit).

## v0.2 (2026-07-04)

- [x] Molotovs/Würfe gefixt: Player.throwItem wirft das bereits instanzierte
      heldItem (KEIN Drop-Spawn) - der alte Throw-Sync feuerte deshalb nie.
      Neu: Prefix fängt heldItem ab, sendet Spawn (an der Landeposition) +
      Landeziel; Gegenseite spielt die Landung nach (Molotov explodiert via
      Explodes.onActivate, Flare zündet). Wurf wird dem Werfer-Klon zugeordnet.
- [x] Friendly-Fire verfeinert: Klon-Treffer werden nur noch weitergeleitet,
      wenn sie vom Spieler (byPlayer) oder einem Gegner (Character-Angreifer)
      stammen - Umgebungsschaden (Explosionen/Feuer) läuft auf der
      Opfer-Maschine nativ und würde sonst doppelt zählen.
- [x] Von Spielern platzierte Objekte (Beartraps, Köder, ...) werden gesynct
      (Build_Patch/BuildSync, repliziert die Platzierungssequenz des Spiels)
      inkl. Session-Snapshot für Late-Joiner.
- [x] Konstruktionen (Constructible.construct) werden gesynct - Replay über
      den zutatenfreien Save-Load-Pfad (manual=false).
- [x] Join-Snapshot: aktive Haltelichter werden beim Join re-broadcastet;
      brennende Flares der Session werden Late-Joinern nachgeworfen.
- [x] UI/Chat im Darkwood-Look (dunkle Panels, Knochen-Text, blutrote Akzente,
      Spiel-Font wenn auffindbar). Chat: ENTER sendet (fehlte komplett!),
      Auto-Fokus, passives Nachrichten-Log mit Ausblenden.
- [x] Version auf 0.2.0.

## v0.2.5 (2026-07-05) — Playtest-Fixes

- [x] Chat repariert: Das v0.2-GUISkin wurde von Null aufgebaut (kaputter
      Text-Cursor/Selektion, fehlende interne Styles) und der geliehene
      Spiel-Font wurde blind gewählt (Icon-Fonts = unsichtbarer Text). Skin ist
      jetzt eine Kopie des Runtime-Skins, Fonts werden auf Glyphen geprüft,
      ENTER/ESC zusätzlich über Input-Fallback in Update().
- [x] Pilz-Effekt-Leak behoben (Ursache per IL verifiziert):
      Trigger.checkCollision prüft den Collider auf Player, wendet Effekte aber
      auf Player.Instance an - der Klon des Mitspielers hat den BEOBACHTER
      vergiftet. Klone lösen jetzt gar keine Trigger mehr aus.
- [x] Beartraps (drauftreten) synchronisiert: Trigger.switchToTriggered wird
      gebroadcastet und remote idempotent nachgespielt ("trapfire").
- [x] Barrikaden synchronisiert (Tür + Fenster, Bau/Zerstörung + Health,
      inkl. Join-Snapshot). Plank-Refunds beim Remote-Replay unterdrückt
      (addItemTypeToPlayer wird während RemoteApply global geskippt;
      Journal-Questitems/Schlüssel sind explizit ausgenommen).
- [x] PvP-Schaden repariert (Ursache per IL verifiziert): MeleeSensor und
      spawnBullet suchen Ziele via GetComponent<Character> - Spieler-Klone
      waren NIE gültige Ziele, das getHit-Routing konnte nie feuern. Neu:
      Sensor-Kontakt-Postfix für Nahkampf + Schuss-Recast entlang der
      Blickrichtung ("pvp"-Weiterleitung wie gehabt). Brennen überträgt sich
      über die neue Feuer-Synchronisierung.
- [x] Benzinspur synchronisiert (Core.AddPrefab("Items/GasolineTrail")) inkl.
      Anzünden (Liquid.startBurning, Nachbar-Ausbreitung konvergiert lokal).
- [x] Lags/Stutters: Pending-Retries teilen sich jetzt EINEN Szenen-Scan
      (statt einem pro Eintrag), FindLocalPlayer auf 1s gedrosselt,
      Item-Schalter-Broadcasts nur noch für switchable-Items (Events flippten
      hunderte Ambient-Lichter pro Frame), Anim-Library-Negativ-Cache (60s
      Backoff statt Full-Heap-Scan alle 5s), Chat-Log pro Frame gecacht,
      Health-Reflection gecacht, Pickup-Chat-Spam entfernt.
- [x] Haltelichter: Diagnose-Logs für das itemlight-Gate (pro Itemtyp) und
      Fallback-Licht (Klon des lightDot mit InvItem.lightRadius), falls das
      lightEmitter-Prefab fehlt/nicht spawnt.
- [x] Version 0.2.5.

## v0.3.0 (2026-07-05) — Gegner-Synchronisierung komplett

- [x] Symmetrische, distanzbasierte Authority: JEDE Maschine simuliert und
      broadcastet die Gegner, deren Spieler am nächsten dran ist (50m), die
      andere Seite claimt/friert ein und spiegelt. Hysterese gegen Flattern:
      Nicht-Host weicht, sobald ein anderer Spieler <60m am Gegner ist; der
      Host gibt Gegner <55m um sich selbst nie ab. Damit sind Gegner am
      Client-Standort (z.B. Night Defense im eigenen Versteck) erstmals ECHT
      synchronisiert statt nur lokal simuliert.
- [x] Gegner-IDs sind jetzt "spielerId:instanzId" - keine Kollisionen zwischen
      den Maschinen mehr, und Schadensweiterleitung ("enemyhit") erreicht den
      tatsächlichen Besitzer statt pauschal den Host.
- [x] Gespiegelte Gegner zeigen jetzt Gesundheit (Health + updateHealthBar)
      und ANIMATIONEN (Clip-Name reist im State-Feld des EnemyUpdatePackets
      mit - Angriffe/Bewegung sind sichtbar statt eingefrorener Posen).
- [x] Gegner-Nahkampf, der den Klon eines Spielers trifft, wird als Schaden an
      diesen Spieler weitergeleitet (nur von der Besitzer-Maschine - keine
      Doppeltreffer).
- [x] Ziel-Umlenkung (best effort): Besitzer-Gegner werden alle 2s per
      Character.attack(GameObject) auf den NÄCHSTEN Spieler gelenkt, wenn das
      ein Remote-Klon ist - Gegner stürzen sich nicht mehr nur auf den
      Besitzer. (Konstante EnableTargetNudging in EnemySync zum Abschalten.)
- [x] Version 0.3.0.

## v0.4.0 (2026-07-05) — Spawn-/Event-Determinismus + Weltregeln (Roadmap komplett)

Basis war das vollständige IL-Hook-Audit (1948 Typen). Alle Roadmap-Punkte
sind umgesetzt oder mit Begründung als "bewusst nicht gesynct" markiert.

### KRITISCH — Spawn-/Event-Determinismus

- [x] Autoritatives Gegner-Spawnen (EnemySpawn_Patch + EnemySync.OnRemoteSpawn):
      `CharacterSpawner.spawnCharacterAround` (der EINE Chokepoint für Night-
      Scenario-, RandomEvent- und GameEvent-Spawns, per IL verifiziert) broadcastet
      jeden dynamischen Spawn ("espawn" mit Besitzer-Id "spielerId:instanzId");
      die Gegenseite instanziert dasselbe Prefab an derselben Stelle
      (Core.AddPrefab("Characters/"+type) + addToSaveable, wie im Spiel) und
      friert es SOFORT als Spiegel des Besitzers ein - exakte Id-Zuordnung statt
      Name+Distanz-Matching. Populationen sind damit identisch.
      Remote-Replays können nie selbst spawnen (Prefix-Skip bei RemoteApply).
- [x] Nacht-Dedupe: `spawnNightChar` wird unterdrückt, solange ein SENIOR-Spieler
      (Host bzw. niedrigere Id) <60m entfernt ist - dessen Maschine spielt die
      Nacht, die eigenen Kopien kommen über espawn. Getrennte Spieler behalten
      ihre eigene Night Defense (Client-Nächte funktionieren weiter).
- [x] `CharacterSpawnPoint.actuallySpawn`: die spawnChance-Würfe sind jetzt
      deterministisch (Seed = Weltseed ^ Positions-Hash) - beide Maschinen
      würfeln für denselben Spawnpunkt dasselbe Ergebnis, egal wann die
      Location lokal aktiviert wird. Kein Broadcast nötig.
- [x] Skript-Events (GameEvents_Patch + EventStateSync): Events FEUERN nur auf
      der Ursprungsmaschine; die Effekte replizieren über die bestehenden Kanäle
      (Spawns via espawn, Flags, Türen, Items, Barrikaden). Einmalige Event-
      Batches (`GameEvents.fired && !multipleFire`, per IL verifiziert)
      broadcasten "gevent" - die Gegenseite markiert ihr Pendant als gefeuert,
      damit der zweite Spieler denselben Story-Beat nicht erneut auslöst.
      `EventTriggers` braucht KEINEN Patch: OnTriggerEnter vergleicht gegen
      Player.Instance.gameObject (per IL verifiziert) - Klone können keine
      Area-Trigger auslösen.
- [x] Zufalls-Tagesevents sind host-autoritativ: die geplanten Würfe
      (`RandomEvent.onUpdateTime` -> `fire(true, false)`, per IL verifiziert)
      werden auf Clients unterdrückt - "bei mir kam der Wolfsmann, bei dir
      nicht" kann nicht mehr passieren. Skript-/Nacht-Fires
      (checkIfRequirementsMet=false) laufen weiterhin überall.

### HOCH

- [x] NPC-Handel (Trader_Patch): Händler-Bestand deterministisch
      (`randomizeTraderInv` mit Seed = Weltseed ^ Tag ^ NPC-Name) + nach jedem
      `DialogueWindow.acceptTrade` wird das NPC-Inventar über den bestehenden
      Container-Kanal abgeglichen - was einer kauft, verschwindet beim anderen.
- [x] Status-Effekte (PvpHit_Patch + DamageSync.ApplyRemoteEffect): Melee-Treffer
      auf Klone übertragen jetzt auch die Effekte des Sensors (Gift-Biss etc.)
      als "pvpfx" mit Typ/Dauer/Stärke/Intervall. Umgebungs-Effekte (Feuer,
      Explosionen) liefen schon vorher nativ auf der Opfer-Maschine.
- [x] Schlösser (Lock_Patch + LockSync): `Locked.unlock` (deckt Schlüssel UND
      Dietrich, beide rufen unlock, per IL verifiziert), `Door.unlock` (Events
      schreiben Locked.locked direkt), `Door.lockMe` (Events schließen ab) und
      `Padlock.unlock` (Replay mit manually:true, damit die Trigger des
      Zahlenschlosses das bewachte Objekt auch remote öffnen).
- [x] Gegner-Schaden an Barrikaden (SiegeDamage_Patch): `Door.getHit` /
      `Window.getHit` broadcasten die absolute Rest-HP über die bestehenden
      idempotenten doorbar/winbar-Kanäle. Der Bruch selbst läuft weiter über
      destroyBarricade (Barricade_Patch). Eingefrorene Spiegel-Gegner greifen
      nie an - kein Doppelzählen.

### MITTEL

- [x] Säge (Station_Patch): `convert` + `addFuel` -> absoluter Tankstand
      ("sawfuel") + Output-Inventar über den Container-Kanal.
- [x] Feeder: Benutzung repliziert als Zustands-Flip (makeInactive) - der Buff
      bleibt beim Benutzer.
- [x] Lure: Bissschaden als absolute HP, nur von der Maschine gemeldet, die das
      fressende Tier simuliert; Replay über removeHealth-Delta, damit der
      Zerstörungspfad (BodyPartMarker, Map-Element) auch remote läuft.
- [x] Porter-Spawn (Porter_Patch, im Nach-Audit korrigiert): NUR
      Location.spawnPorter wird repliziert - der einzige SPIELER-getriebene
      Pfad (InvItemClass.use, Porter-Ruf-Item). Trader/Wolf spawnen über
      Controller.startAfterNight und despawnen über checkExitEvents - beides
      läuft zeitgesteuert auf JEDER Maschine (spawnTrader ist idempotent,
      per IL verifiziert); ein Replay hätte doppelt gefeuert. Der
      ursprüngliche LocationNpc_Patch (alle 6 Methoden) wurde entfernt.
- [x] Werkbank-Ausbau (WorkbenchLevel_Patch, Audit-Fund): die Rezept-Stufe
      liegt in Controller.workbenchLevel, EINZIGER Gameplay-Schreiber ist
      CraftingRecipes.doCraft (per Field-Writer-Scan verifiziert). Upgrade
      wird als "wblevel" gebroadcastet, Empfänger nimmt max(lokal, remote).
- [x] Schlafen (Sleep_Patch): `Player.onEndSleep` broadcastet die absolute Uhr
      ("gametime"), die Gegenseite übernimmt (nur Vorwärts-Sprünge).
- [x] `HidingPlace`: BEWUSST nicht gesynct - das ist ein KI-Versteckplatz für
      Charaktere (kein Spieler-Feature); von der Gegner-Spiegelung abgedeckt.
- [x] `Location.enter/leave/wolfmanStealEvent/transportAllItemsToCurrentHideout`:
      bewusst lokal - enter/leave sind Aktivierungslogik pro Maschine, der
      Steal-Event läuft über Flags/Container, der Umzug über den World-Snapshot.

### NIEDRIG

- [x] Wetter deterministisch: `Rain.onNewDay` / `setUpNextRain` werden pro Tag
      geseedet (WorldGenSeed_Patch) - Regen/Sturm fällt für beide gleich aus.
- [x] `Map`/`MapElement`: bewusst pro Spieler (eigene Entdeckung).
- [x] `CutsceneManager`: bewusst lokal (Story-Sync läuft über Flags/Dreams).
- [x] `PlayerSkills`: bewusst pro Spieler.
- [x] Version 0.4.0.

## v0.5.0 (2026-07-05) — alle dokumentierten Lücken geschlossen

- [x] **Koop-Spieler-Tod definiert** (Death_Patch): Recherche-Korrektur - der
      Tod lädt KEINEN Save; Player.die spielt den "playerDeath"-Traum und
      Dreams.endDreaming teleportiert (Respawn) + setzt die Uhr auf den Morgen
      (per IL verifiziert). Regel: Tod ist eine GETEILTE Konsequenz wie im
      Original. Die Sterbe-Maschine meldet den Tod sofort ("pdied",
      Chat-Nachricht beim Partner) und broadcastet nach dem Todestraum die
      neue absolute Uhr ("pdeath:tag:gameTime:currentTime"). Alle übernehmen
      vorwärts-gerichtet; die Tages-Kette (refreshTime -> startBeforeDay ->
      startDay) läuft aus der neuen Uhr natürlich an und die Host-Broadcasts
      richten alle nach.
- [x] Dream_Patch-Bug behoben (beim Tod-Umbau gefunden): der "playerDeath"-
      Traum wurde wie jeder Traum gebroadcastet/überschrieben - ein Host-Tod
      hätte den nächsten Client-Traum mit dem TODESTRAUM vergiftet, und ein
      anhängiger Host-Traum hätte den Todestraum des Clients ERSETZT. Der
      Todestraum ist jetzt strikt persönlich.
- [x] Kapitel-Übergang sichtbar gemacht (Chapter_Patch): loadChapterSave hat
      genau EINEN Aufrufer (WorldGenerator.onFinished) - der Übergang selbst
      ist durch gesyncte Flags/Dreams + Seed bereits koordiniert; jetzt gibt
      es die "chapter2"-Meldung an den Partner statt eines unerklärten
      eingefrorenen Klons.
- [x] Nacht-Szenario-Kosmetik-Divergenz beseitigt: CustomEvent.fire wird auf
      der Junior-Maschine unterdrückt, solange der Senior <60m steht (gleiche
      Regel wie Spawn-Dedupe) - vorher liefen Nicht-Spawn-Effekte eines ggf.
      ANDEREN Client-Szenarios kosmetisch mit.
- [x] 3+-Spieler-Authority-Loch geschlossen: Nicht-Hosts weichen nur noch
      SENIOR-Klonen (niedrigere Id) - vorher wichen zwei Clients einander
      gegenseitig aus und NIEMAND sendete den Gegner. Seniorität macht die
      Ownership zur Totalordnung.
- [x] Marker-Fallback aufgewertet: Gegner-Updates ohne lokales Gegenstück
      (z.B. Roamer-Drift) spawnen jetzt das ECHTE Prefab als Spiegel
      ("Characters/" + Typname ohne "(Clone)"); die Raute bleibt nur für
      nicht auflösbare Prefabs. Damit ist auch die WorldChunk-Roamer-Lücke
      praktisch zu.
- [x] Schloss-Join-Snapshot: LockSync führt Session-Historie (unlock/padlock/
      doorlock, lokal + remote, mit Supersede-Logik) und der Host spielt sie
      Late-Joinern nach.
- [x] Offline-Konstruktions-Lücke (Session) geschlossen: BuildSync merkt sich
      fertige Konstruktionen (id+option, lokal + remote) und re-broadcastet
      sie im Join-Snapshot - die Constructible-Komponente zerstört sich nach
      dem Bau, die Historie ist der einzige Weg.
- [x] Händler-Matching robust: eindeutiger NPC-Namens-Fallback in
      ContainerSync.FindContainer (Trader, die >10m von ihrer Position auf
      der anderen Maschine stehen, werden jetzt trotzdem gematcht, solange
      der Name eindeutig ist).
- [x] Lure-Loot-Würfe deterministisch geseedet (Weltseed ^ Position ^ HP -
      Vor-Biss-HP ist gesynct, beide Seiten würfeln gleich).
- [x] ExperienceMachine geprüft: enable() läuft über Start/setExperienceMachine
      (Flag-getrieben auf beiden Maschinen), Essenz-Kochen ist
      Spieler-Progression → bewusst lokal, KEINE Lücke.
- [x] Immersiver Chat: Chat-Nachrichten erscheinen zusätzlich als In-Game-
      Sprechtext über dem Sprecher - exakt der NPC-Pfad
      (Core.displayMessage(text, transform, 1f, false), per IL verifiziert;
      CharacterMessage tippt den Text aus und folgt dem Sprecher). Eigene
      Nachrichten über dem eigenen Kopf, fremde über dem Klon; das
      Chat-Overlay loggt weiterhin alles (Bubble ist ein Bonus, gekappt bei
      160 Zeichen, Fehler brechen den Chat nie).
- [x] Version 0.5.0.

## v0.5.1 (2026-07-05) — Playtest-Fixes

- [x] Zufallsobjekte (Leichen/Beartraps/Kisten) desyncten: Ursache per IL -
      WorldGenerator.spawnRandomObjects iteriert die Chunks und JEDER Chunk
      zieht aus EINEM geteilten RNG-Strom + globalem "unspawned"-Pool
      (RandomWorldObjects.getUnspawnedObject). EINE Abweichung (z.B. ein
      physik-abhängiger Platzierungs-Retry neben verschobenen Möbeln)
      korrumpierte alle folgenden Chunks - deshalb wirkten die Objekte
      "überall zufällig anders". Fix: Re-Seed PRO CHUNK
      (Seed ^ Tag ^ Chunk-Position) für spawnRandomObjects und
      spawnNightObjects - Drift bleibt jetzt auf einzelne Chunks begrenzt.
      Restrisiko: Pool-Zustands-Drift bei fehlgeschlagenen Spawns innerhalb
      eines Chunks (deutlich seltener; nächste Stufe wäre Host-Broadcast der
      Objektliste).
- [x] Glitchy Partner-FOV: Der Spieler-Klon kopierte die eigenen
      Light2D-Objekte des Spielers - Darkwoods Sichtkegel IST ein Light2D mit
      zur Laufzeit generiertem Schatten-Mesh. Das Skript wurde auf dem Klon
      zwar deaktiviert, aber das stehengebliebene Mesh RENDERTE weiter -> der
      Beobachter sah den FOV des Partners als glitchige Überlagerung am Klon
      kleben (nur bei dem Spieler, bei dem das Mesh zum Klon-Zeitpunkt gefüllt
      war - daher asymmetrisch). Fix: alle Light2D-GameObjects (+
      fov/vision/lightmesh-Helfer ohne tk2d-Sprite) werden beim Klonen
      zerstört; Remote-Lichter verwaltet ohnehin HeldLightSync.
- [x] Version 0.5.1.

## v0.6.0 (2026-07-06) — Transport-Reliability, Desync-Wächter, Sync-Matrix

Fokus dieser Iteration: die zwei ausdrücklich geforderten Infrastruktur-Lücken
schließen (garantierte Zustellung + automatische Desync-Erkennung) und die
vollständige Interaktions-Matrix als Abschlussdokument erstellen. Neue
Referenz: [SYNC_MATRIX.md](SYNC_MATRIX.md) listet JEDE Spielerinteraktion mit
Kategorie, Mechanismus, Log-Tag, Status und Test-Playbook.

- [x] **Echter Ack/Resend-Reliability-Layer** (`NetworkLayer` +
      `ServerHostService`): `SendReliable` war bisher ein Alias für einfaches
      UDP. Jetzt reist jedes Reliable-Paket in einem sequenzierten Envelope
      (`0xE0`), die empfangende Station acked (`0xE1`), dedupliziert über ein
      Fenster und entpackt. Zustellung ist Hop-für-Hop garantiert (exactly-once
      an das Spiel), OHNE Reihenfolge-Garantie - jeder S/E-Kanal ist idempotent
      oder absolut, deshalb genügt das. Resend alle 300 ms, Aufgabe nach ~12 s
      (dann greifen Pending/Snapshot). Der Host re-originiert relayte Reliable-
      Pakete mit eigener Sequenz pro Peer. Statistik-Log `[Reliable]`.
- [x] **Handshake robust + Reconnect**: Der ConnectRequest wird bis zur Antwort
      jede Sekunde wiederholt (ein verlorenes Handshake-Paket bedeutete vorher
      garantierten 10-s-Timeout). Ein Reconnect vom selben Endpoint setzt den
      Reliable-Channel und das De-Dup-Fenster zurück (die Sequenz startet neu
      bei 1) und spielt den vollen Willkommens-/Snapshot-Satz erneut.
- [x] **Automatische Desync-Erkennung** (`SyncCheck`, `[SyncCheck]`): jede
      Maschine broadcastet alle 20 s einen Digest des konvergenten geteilten
      Zustands (Tag, Uhr, Werkbank-Level, Flags-/Lock-/Build-Hash). Der
      Empfänger vergleicht feldweise; eine Abweichung zählt erst nach dem
      **2-Strike-Prinzip** (dieselben lokalen UND remoten Werte zwei Checks in
      Folge) als Desync - In-Flight-Änderungen (gerade gesyncte Flags, Tür im
      Replay) lösen keinen Fehlalarm aus. Bestätigte Desyncs landen als
      `[SyncCheck] DESYNC` in Log und Chat und lösen **idempotente Vorwärts-
      Korrektive** aus (Uhr vorwärts, Werkbank-max, Flag-/Lock-/Build-Snapshot
      erneut senden). Das ist der geforderte Prüfmechanismus, der automatisch
      erkennt, wenn eine Aktion nicht synchronisiert wurde. Konvergenz-Stores
      dafür in `Flags_Patch`/`LockSync`/`BuildSync` (lokale + remote Aktionen
      landen im selben Digest → identisch, wenn Sync funktioniert).
      WICHTIG: die Historien-Korrektive senden auf BEIDEN Seiten (nicht nur
      Senior) - ein Hash-Mismatch sagt nicht, WER etwas vermisst; jede Maschine
      re-broadcastet ihre eigene idempotente Historie, die fehlende Seite füllt
      auf, die andere dedupliziert.
- [x] **Gegner-Tod & „gone" jetzt zuverlässig** (`EnemySync`): der Tod eines
      Gegners wurde genau EINMAL über den verlustbehafteten Kontinuierlich-Kanal
      gemeldet (`_deadSent`-Guard + `_sent.Remove`). Ging dieses eine Paket
      verloren, blieb auf der Spiegel-Seite für immer ein eingefrorener
      Kadaver (Statue), weil der Owner nie erneut sendet. Tod und „gone"
      (Radius verlassen) laufen jetzt über `SendReliable`; der laufende
      Alive-Strom bleibt bewusst unreliable (nächstes Paket heilt Verluste).
- [x] Join-Snapshot wird reliable ausgeliefert (jeder Eintrag genau einmal -
      ein verlorenes Datagramm ließ Late-Joiner den Eintrag dauerhaft verpassen).

### Dedicated Server auf Stand gebracht (voll funktionsfähig)

- [x] **Ausgehende Reliability** (ServerHostService): der Server hat
      SendReliable-Envelopes bisher nur ANGENOMMEN, aber über plain UDP
      weitergereicht - reliable Pakete degradierten auf dem Relay-Hop still zu
      unreliable. Jetzt hat der Server einen eigenen Sende-Reliability-Layer
      (Sequenz + Pending + Resend-Loop im Tick + Ack-Verarbeitung, pro
      Endpoint). Reliable EINGEHENDE Pakete werden reliable RE-ORIGINIERT -
      Zustellung ist über den Relay garantiert. Protokoll-Test bestätigt:
      Envelope→Ack→reliable Snapshot→Resend bei fehlendem Ack→Dedupe.
- [x] **Relay-by-default**: unbekannte/nicht modellierte Paket-Typen wurden im
      `default`-Zweig VERWORFEN (der Server musste manuell mit jedem neuen
      Mod-Pakettyp Schritt halten). Jetzt werden sie an die anderen Peers
      weitergereicht (mit Reliable-Flag) - der Server ist protokoll-komplett
      und bleibt automatisch „up to date".
- [x] **Reliable Control-/Snapshot-Auslieferung**: PlayerList, PlayerJoined/
      Left, Chat, EventTrigger, GameStateSync und der komplette Join-Snapshot
      laufen jetzt reliable (ConnectResponse bleibt unreliable - der Client
      wiederholt den Handshake). Reconnect vom selben Endpoint setzt Send- und
      Recv-Kanäle zurück.
- [x] **Zeit-Autorität statt erfundener Server-Uhr**: der Kernfehler war, dass
      im Dedicated-Modus NIEMAND `IsHost` ist - Tag/Nacht, Träume und geplante
      Zufallsevents (alle an `IsHost` gekoppelt) liefen bei NIEMANDEM, und der
      Server erfand eine eigene, vom Spiel entkoppelte ~48h-Uhr, die mit den
      Clients kollidierte. Neu: `NetworkManager.IsTimeAuthority` wählt den
      Client mit der NIEDRIGSTEN Id zur Zeit-Autorität (deterministische
      Totalordnung, automatische Neuwahl beim Verlassen). Beweisbar No-Op für
      den In-Game-Host-Pfad (ein Host-Client sieht immer Id 0 in der Liste,
      kann also nie höher ranken). Angewandt auf Controller_Time_Patch,
      WorldSync (Tag/Nacht), SyncCheck.CorrectDay, Dream_Patch und die
      RandomEvent-Unterdrückung. Server-seitig: die erfundene Uhr + der
      World-State-Rebroadcast liegen jetzt hinter dem Config-Flag
      `AuthoritativeWorld` (Default FALSE = reiner Reliable-Relay); der Server
      beobachtet die Tag/Nacht-Broadcasts der Autorität für den Join-Snapshot.
- [x] EnemySync bleibt unverändert: Distanz + Seniorität (niedrigste Id) regeln
      die Gegner-Ownership auch OHNE Host; die host-spezifische
      „Sovereignty"-Optimierung entfällt im Dedicated-Modus einfach.
- [x] Version 0.6.0.

## v0.6.1 (2026-07-06) — Vollständigkeits-Audit gegen das echte Spiel

Auf Wunsch „nichts darf übersehen werden" wurde die Sync-Matrix NICHT nur gegen
die eigenen Patches geprüft (zirkulär), sondern gegen die **echte
Interaktionsfläche aus Assembly-CSharp.dll**. Werkzeug: MetadataLoadContext +
Mono.Cecil (echte Methodenkörper). Geprüft: komplette `Player`-Methodenfläche
(≈220 Methoden) + alle ~1948 Typen + IL-Trace der Verdächtigen. Details in
[SYNC_MATRIX.md](SYNC_MATRIX.md) Abschnitt 10.

- [x] **EINZIGE gefundene Lücke geschlossen — Tod-Loot-Beutel** (`DeathDrop_Patch`
      + `ContainerSync.OnRemoteDeathDrop`): per IL verifiziert ruft die
      `<onDeath>d__435`-Coroutine `Player.dropBody` 3× auf; jeder Aufruf spawnt
      einen `deathDrop`-Beutel und legt eine ZUFÄLLIGE (ungeseedete) Teilmenge
      der Items der sterbenden Figur hinein - lokal, ungesynct. Der Partner sah
      die Leiche nie und hätte sie nicht mit-looten können. Fix: die sterbende
      Maschine broadcastet den TATSÄCHLICHEN Inhalt (kein Neu-Würfeln) +
      Prefab + Position; der Partner spawnt einen identischen Beutel und füllt
      ihn. Ab da ist es ein normaler gesyncter Container - Looten reconciled
      über das bestehende Name+Position-Matching, KEINE Duplikation (die Items
      wurden beim Tod ohnehin aus dem Spieler-Inventar entfernt).
- [x] **Geprüft & als korrekt lokal/abgedeckt bestätigt (IL-belegt):**
      `getHitByShadow`/`bansheeScreamHit` (rufen gepatchtes `setHealth`),
      `Examinable.examine` (feuert Event-Trigger → Kanäle replizieren),
      `special_spawnDreamForestSpirit`/`spawnWolfInCurrentHideout`/`tryToSpawnShadow`
      (direkter `Core.AddPrefab`, aber `Character` → EnemySync-Spiegelung),
      `die → respawnAllEnemies`/`despawnCharacters` (Gegner sind distanz-
      gespiegelt, selbstheilend über „gone" + EnemySync), `DarkwoodTree`
      (keine Methoden - Kulisse), Crafting/Upgrade/Repair/EXP/Skills/Hunger/
      Stamina/FOV/Shadows (persönliche Progression/Survival, Design).
- [x] Audit-Werkzeug (`apidump`, MetadataLoadContext+Cecil) im Dev-Scratchpad;
      Wiederherstellung dokumentiert in GAME_API.md.
- [x] Version 0.6.1.

### v0.6.1 Nachaudit-Korrektur — Tod-Loot-Beutel (per IL nachverifiziert)

Der erste Wurf von `DeathDrop_Patch`/`OnRemoteDeathDrop` hatte drei echte, per
IL belegte Fehler (die `deathDrop`-Beutel entstehen ALLE an DERSELBEN
Spielerposition — `<onDeath>d__435` ruft `dropBody(playerPos)` 3× mit demselben
Vektor auf):

- [x] **Leerer Partner-Beutel (Crash).** `OnRemoteDeathDrop` rief
      `Inventory.initSlots` mit `Invoke(inv, null)` auf — die Methode hat aber
      einen `bool`-Parameter (`TargetParameterCountException`). Das Spiel selbst
      ruft im `dropBody` `initSlots(true)` (IL: `ldc.i4.1`). Fix: `initSlots(true)`
      → die Slots werden angelegt, `addItemType` findet freie Slots, der Beutel
      wird tatsächlich befüllt.
- [x] **2/3 der Beute gingen verloren.** Die Idempotenz-Prüfung verwarf jeden
      neuen Beutel, der ≤2 m von einem schon vorhandenen lag — da alle drei
      Beutel an derselben Position spawnen, wurden Beutel #2 und #3 als
      „bereits vorhanden" übersprungen. Fix: statt Ortsnähe ein global
      eindeutiges Drop-Token `<clientId>-<seq>` pro Beutel; Idempotenz vergleicht
      exakt dieses Token.
- [x] **Container-ID-Kollision.** Container werden über `name@x,z` reconciled —
      drei gleichnamige Beutel an einer Position teilten sich EINE ID, sodass
      Teil-Looten den falschen Beutel leerte (Item-Dup/Desync). Fix: das
      Drop-Token wird auf BEIDEN Maschinen an den Objektnamen angehängt
      (`deathDrop(Clone)#<token>`), womit jeder Beutel eine eigene, auf beiden
      Seiten identische Container-ID bekommt. Rename ist save-sicher:
      `SaveableObject` identifiziert über `prefabName`+`uniqueId`, nicht über
      `GameObject.name` (IL-belegt).
- Nachrichtenformat jetzt `deathdrop:<prefab>:<token>:<x,y,z>:<items>`.
- Bekannte Restgrenze: der Beutel-Inhalt bleibt UNGESEEDET (die sterbende
  Maschine würfelt die Aufteilung), wird aber als tatsächlicher Inhalt
  übertragen — beide Seiten sehen also dieselben Beutel. Weiterhin nur per
  2-Instanz-Playtest final zu bestätigen (Spawn + Loot-Pfad).

### v0.6.1 Nachaudit-Korrektur — Dedizierter Server: HealthUpdate-Absturz beim Join

- [x] **`HealthUpdatePacket`-Feldreihenfolge im Server driftete vom Mod ab.**
      Mod-Wire-Layout: `PlayerId, TargetEntityId(string), Health, IsDead`; der
      Server las `PlayerId, Health, IsDead, TargetEntityId` und interpretierte
      dadurch die String-Längenangabe aus den Float-Bytes → beim ersten
      Health-Update eines Spielers `InvalidOperationException: Not enough data
      to read 65 byte(s)`. Fix: Server-Layout exakt an den Mod angeglichen (der
      Mod ist die Wire-Wahrheit; Clients parsen sich gegenseitig damit). Ein
      Diff ALLER Packet-Definitionen bestätigte: das war die EINZIGE Abweichung.
- [x] **Relay-Härtung gegen künftige Format-Drift.** Der Server deserialisiert
      modellierte Pakete nur fürs Logging/kleine Weltzustände, RELAYT aber die
      Rohbytes — und zwar erst NACH dem Parsen. Ein Parse-Fehler ließ das Paket
      also stillschweigend fallen (statt weiterzuleiten). Neuer Fallback im
      `HandlePacket`-Catch: bei Parse-Fehler eines Game-Data-Typs werden die
      Original-Bytes des Clients trotzdem an die Peers weitergereicht
      (`IsRelayableGameType`), damit ein einzelnes Layout-Delta nie den ganzen
      Relay-Fluss unterbricht.

## Bekannte Einschränkungen / offene Punkte (Stand v0.6)

- Gegner-Ziel-Umlenkung ist ein Nudge (attack()-Aufruf alle 2s), kein echter
  KI-Umbau - die Aktivitäten-KI kann dazwischen aufs lokale Ziel zurückfallen.
- Gegner im 50-60m-Grenzband zwischen zwei Spielern werden bewusst auf beiden
  Seiten unabhängig simuliert (Hysterese gegen Authority-Flattern).
- Rein persönliches Spieler-Feedback aus Events (Hilfetexte, Sounds,
  Kamera-Effekte) bleibt BEWUSST auf der Ursprungsmaschine - wie im
  Original ist es an den auslösenden Spieler gerichtet. Alles
  Zustands-relevante (Flags, Journal, Türen, Spawns, Dreams, Schlösser,
  Barrikaden, Inventare) repliziert.
- Session-Historien (Schlösser, Konstruktionen, Platzierungen) decken
  Late-Joiner DERSELBEN Session; wer eine komplett andere Session verpasst
  hat, gleicht über Save-Kompatibilität + Live-Sync wieder an.
- ~~Kein Transport-Reliability-Layer~~ → **erledigt in v0.6** (Ack/Resend/De-Dup
  auf `SendReliable`, `[Reliable]`). Restpunkt: der Layer garantiert Zustellung,
  aber keine Reihenfolge - das ist Absicht (alle S/E-Kanäle sind idempotent
  oder absolut). Ein optionaler geordneter Kanal wäre nur für künftige,
  reihenfolge-abhängige Features nötig.
- Host-Migration wird nicht unterstützt: Enemy-Ownership und die host-
  autoritativen Tagesevents brauchen einen stabilen In-Game-Host. Fällt der
  Host aus, endet die Sitzung; eine Migration erfordert Autoritäts-Neuwahl +
  vollständigen State-Transfer (Roadmap).
- Desync-Wächter (`SyncCheck`) deckt den konvergenten geteilten Zustand ab
  (Tag/Uhr/Werkbank/Flags/Locks/Builds). NICHT im Digest: Zufallsobjekt-
  Populationen (legitimerweise chunk-abhängig) und per-Spieler-Zustand
  (Inventar/Map/Skills) - dort wäre ein Hash-Vergleich ein Fehlalarm.
- Dedicated Server (DarkwoodMP.Server) ist jetzt ein VOLLWERTIGER Reliable-
  Relay: garantierte Zustellung in beide Richtungen, relay-by-default, und die
  Zeit-/Event-Autorität wird auf den niedrigsten Client (Id) gewählt statt von
  einer erfundenen Server-Uhr getrieben. In-Game-Host bleibt der einfachste
  Weg (ein Klick), aber der Dedicated Server ist kein bloßes Relay mit kaputter
  Uhr mehr. Restpunkte für den Dedicated-Modus (dokumentiert, nicht kritisch):
  - Verifikation ist Kompilierung + Protokoll-Test (Envelope/Ack/Resend/Dedupe
    end-to-end bestätigt); ein 2-Client-Vollspiel-Playtest über den Dedicated
    Server steht noch aus.
  - Gegner-„Host-Sovereignty" (Gegner am Host nie abgeben) entfällt mangels
    Host; Distanz+Seniorität übernehmen - in der Praxis ausreichend, aber
    minimal mehr Flattern im Grenzband als mit In-Game-Host möglich.
  - `AuthoritativeWorld=true` (server-simulierte Welt) ist ein experimenteller
    Modus und NICHT der empfohlene Pfad.
