# Darkwood Multiplayer — Vollständige Synchronisations-Matrix

> **HISTORICAL (v0.6 era).** ActionEvent string tags (`pvp:`, `espawn:`, `clock:`, …)
> are **obsolete**. Live wire = **Ironbark Protocol v2** (`IRONBARK_PROTOCOL.md`,
> `IRONBARK_MESSAGES.md`). Feature coverage = `MERGE_MATRIX.md`. Product = YokWare **0.9**.
> Keep this file as interaction *taxonomy* (S/E/K/P/A categories); do not treat tag names as wire truth.

Stand: v0.6.0 (2026-07-06). Dieses Dokument war die **vollständige Liste aller
Spielerinteraktionen** mit Kategorie, Mechanismus und Log-Tag.
Änderungshistorie: [TODO.md](TODO.md); Spieltypen: [GAME_API.md](GAME_API.md).

---

## 1. Status-Legende

| Symbol | Bedeutung |
|--------|-----------|
| ✅ | Implementiert, gegen Assembly-CSharp per IL/Reflection verifiziert, kompiliert |
| 🔬 | Implementiert; 2-Instanz-Playtest steht aus (Verifikation bisher Code+IL) |
| ⚪ | **Bewusst nicht synchronisiert** — Begründung in Abschnitt 6 |

> **Ehrliche Einordnung des Verifikationsstands:** Jede Interaktion ist gegen
> die realen Spieltypen verifiziert (Patch-Ziele existieren, Aufrufpfade per IL
> geprüft) und übersetzt fehlerfrei. Ein automatisierter 2-Instanz-Integrations-
> test ist mangels Spiel-Headless-Modus nicht möglich; die Live-Verifikation
> läuft über das **Test-Playbook (Abschnitt 7)** anhand der Log-Tags. Die
> Spalten „Host→Client / Client→Host / Multi / Latenz / Verlust / Join" werden
> nicht pro Zeile dupliziert, sondern **über die Kategorie** hergeleitet
> (Abschnitt 3 + 4) — die Kategorie bestimmt das Robustheitsprofil vollständig.

---

## 2. Die fünf Synchronisationskategorien

| # | Kategorie | Definition | Transport | Konvergenz-Garantie |
|---|-----------|------------|-----------|---------------------|
| **S** | Sofortige Zustandsänderung | Diskreter Zustand wird gesetzt (Tür auf, Schalter an, absolute HP/Tankfüllung) | `SendReliable` (Ack/Resend) | Idempotent; letzter Wert / absoluter Wert gewinnt |
| **E** | Ereignisbasierte Aktion | Einmaliges Ereignis wird nachgespielt (Pickup, Wurf, Platzierung, Effekt) | `SendReliable` + Pending/Retry | Idempotenz-Guard oder De-Dup; genau-einmal-Wirkung |
| **K** | Kontinuierliche Zustandsreplikation | Hochfrequenter Strom, jeder Frame ersetzt den vorigen (Position, Anim, Gegnerbewegung) | `Send` (unreliable) | Selbstheilend: nächstes Paket überschreibt verlorenes |
| **P** | Vorhersage & Korrektur | Lokale Simulation, periodischer Abgleich, Korrektiv bei stabiler Abweichung | `Send` (Digest) + `SendReliable` (Korrektiv) | SyncCheck 2-Strike + idempotente Korrektive |
| **A** | Autoritative (Server-)Entscheidung | Eine Maschine entscheidet (Host-Tageszeit, Ownership-Gegner, geseedete Würfe) | Broadcast des Ergebnisses / deterministischer Seed | Nur die autoritative Quelle sendet |

**Transport-Fundament (v0.6):**
- `SendReliable` ist seit v0.6 ein echter **Ack/Resend-Layer** mit Sequenz-
  nummern, De-Dup-Fenster und Hop-für-Hop-Zustellung (Envelope `0xE0`, Ack
  `0xE1`). Reihenfolge wird *nicht* garantiert — jeder S/E-Kanal ist idempotent
  oder absolut, deshalb reicht „exactly-once, unordered". Log-Tag `[Reliable]`.
- `Send` bleibt reines UDP für K-Kanäle (Verlust ist dort belanglos).
- Der Host re-originiert relayte Reliable-Pakete mit eigener Sequenz pro Peer,
  d.h. Zustellung ist auch über den Relay-Hop garantiert.

---

## 3. Robustheitsmodell — die sechs Verifikationsszenarien pro Kategorie

| Szenario | S (sofort) | E (Ereignis) | K (kontinuierlich) | P (Vorhersage) | A (autoritativ) |
|----------|-----------|--------------|--------------------|-----------------|-----------------|
| **Host → Client** | ✅ Reliable | ✅ Reliable | ✅ Strom | ✅ | ✅ Broadcast |
| **Client → Host** | ✅ Reliable | ✅ Reliable | ✅ Strom | ✅ | ✅ (Host entscheidet / Seed) |
| **Mehrere Clients** | ✅ Host-Relay reliable an alle | ✅ dito | ✅ pro Klon | ✅ paarweise Digests | ✅ Seniorität = Totalordnung |
| **Hohe Latenz** | ✅ Ack toleriert RTT; Pending/Retry für ungeladene Areale | ✅ dito | ✅ Interpolation, Alter egal | ✅ 2-Strike verhindert Fehlalarm bei In-Flight | ✅ Seed ist zeitunabhängig |
| **Paketverlust** | ✅ Resend bis Ack | ✅ Resend + Pending | ✅ nächstes Paket heilt | ✅ Digest wiederholt sich alle 20 s | ✅ Seed identisch, kein Paket nötig |
| **Join während Sitzung** | ✅ Join-Snapshot (reliable) | ✅ Session-Historie (Locks/Builds/Placements) | ✅ Live-Strom ab Join | ✅ Digest erkennt Lücke → Nachlieferung | ✅ Seed + Snapshot |

Diese Tabelle ist der Kern: **jede Zeile der Interaktionsmatrix (Abschnitt 5)
erbt ihr Robustheitsprofil aus ihrer Kategorie-Spalte.** Damit ist jede
Interaktion in allen sechs Szenarien abgedeckt, ohne 6 × N Einzelzeilen.

---

## 4. Fehlerkorrektur & Anti-Duplikations-Mechanismen (Querschnitt)

| Mechanismus | Zweck | Ort |
|-------------|-------|-----|
| Reliability-Layer (Ack/Resend/De-Dup) | Verlust & Reihenfolge auf dem Draht | `NetworkLayer` (`[Reliable]`) |
| `RemoteApply.Active`-Guard | Verhindert Echo-Schleifen: nachgespielte Aktionen senden nicht erneut | global, alle Empfänger |
| Pending/Retry-Store | Aktion für noch nicht geladenes Areal zwischenspeichern & nachziehen | Lock/Build/Container/Disarm/Item |
| Idempotenz-Guards | Doppelverarbeitung wirkungslos (`opened`-Check, `locked`-Check, `_deadSent`) | je Modul |
| Refund-Suppression | `addItemTypeToPlayer` global geskippt während Remote-Replay → keine Item-Duplikation | `RemoteApply` + `Inventory_Patch` |
| Absolute Werte statt Deltas | HP, Tankfüllung, Werkbank-Level, Uhr → kein Drift durch verlorene Deltas | Health/Generator/Station/Workbench/Sleep |
| Seniorität (niedrigere Id) | Totalordnung für Ownership & Korrektiv-Absender → kein „beide/niemand" | EnemySync, SyncCheck |
| **SyncCheck (v0.6)** | Erkennt nicht-synchrone Zustände automatisch & korrigiert idempotent | `SyncCheck` (`[SyncCheck]`) |

---

## 5. Vollständige Interaktionsmatrix

Legende Mechanismus: `Patch/Modul → Netzwerk-Tag/Paket`.

### 5.1 Spieler

| Interaktion | Kat. | Mechanismus | Log-Tag | Status |
|-------------|------|-------------|---------|--------|
| Bewegung (Position) | K | `PlayerSync → PositionUpdate` (Send) | `[PlayerSync]` | 🔬 |
| Drehen (Rotation) | K | Rotation im `PositionUpdatePacket` | `[PlayerSync]` | 🔬 |
| Sprinten | K | Geschwindigkeit implizit in Positions-Delta + Anim-Clip | `[PlayerAnimSync]` | 🔬 |
| Animationen | K | `PlayerAnimSync → PlayerAnim` (Send) | `[PlayerAnimSync]` | 🔬 |
| Ausrüstung / gehaltenes Item | K/E | Held-Item-Clip via `PlayerAnimSync`; Lichter via `ItemActive` | `[PlayerAnimSync]`,`[ItemActive]` | 🔬 |
| Inventar (Aufnahme-Notiz) | E | `Inventory_Patch → InventoryUpdate` | `[Inventory]` | ✅ |
| Interaktionen (Hebel/Objekte) | S | `InteractiveItem_Patch → InteractiveState` | `[InteractiveSync]` | ✅ |
| Gegenstand benutzen | E | `ItemActive_Patch` / `Disarm_Patch` | `[ItemActive]`,`[Disarm_Patch]` | ✅ |
| Waffen / Angriffe (Nahkampf) | E+A | `PvpHit_Patch` Sensor-Recast → `pvp:` (Opfer wendet an) | `[PvpHit]`,`[DamageSync]` | 🔬 |
| Waffen / Schuss | E+A | `PvpHit_Patch` Schuss-Recast entlang Blickrichtung → `pvp:` | `[PvpHit]` | 🔬 |
| Nachladen | K | Anim-Clip (visuell); Munition ist Spieler-Inventar | `[PlayerAnimSync]` | 🔬 |
| Heilen | S | `PlayerHealth_Patch → HealthUpdate` (absolute HP) | `[DamageSync]` | ✅ |
| Statuseffekte (Gift-Biss etc.) | E | `PvpHit_Patch → pvpfx:` → `DamageSync.ApplyRemoteEffect` | `[DamageSync]` | 🔬 |
| Tod (Meldung) | E | `Death_Patch → pdied` (Chat-Notiz) | `[Death_Patch]` | 🔬 |
| Tod → Respawn (Uhr) | E+A | `Death_Patch → pdeath:` → `WorldSync` (vorwärts-Uhr) | `[Death_Patch]`,`[WorldSync]` | 🔬 |
| Tod → Loot-Beutel (Leiche) | E | `DeathDrop_Patch → deathdrop:` → `ContainerSync` (Spawn+Inhalt, Loot via Container-Matching) | `[DeathDrop_Patch]`,`[ContainerSync]` | 🔬 |

### 5.2 Inventar & Items

| Interaktion | Kat. | Mechanismus | Log-Tag | Status |
|-------------|------|-------------|---------|--------|
| Aufheben (Welt-Item) | E | `ItemPickup_Patch → PickupState` (reliable) | `[ItemSync]` | ✅ |
| Fallenlassen | E | `PlayerDrop_Patch → PickupState` | `[ItemSync]` | ✅ |
| Werfen (Molotov/Flare/Stein) | E | `Throw_Patch → thrown:` + Spawn an Landeposition | `[Throw_Patch]`,`[ItemSync]` | 🔬 |
| Container/Truhe öffnen & umräumen | S | `Container_Patch`+`ContainerSync` (absolutes Inventar) | `[ContainerSync]` | 🔬 |
| Stapeln / Verschieben (im Container) | S | Absolute Inventar-Replikation über Container-Kanal | `[ContainerSync]` | 🔬 |
| Werkbank-Ablage | S | `Workbench_Patch`+`ContainerSync` (normal+fuel Inventar) | `[ContainerSync]` | 🔬 |
| Handel (Kauf/Verkauf) | S+A | `Trader_Patch` (geseedeter Bestand) + Container-Flush | `[Trader_Patch]`,`[ContainerSync]` | 🔬 |
| Loot (Lure-Kadaver) | A | Geseedeter Loot-Wurf (Seed ^ Pos ^ HP) | `[StationSync]` | 🔬 |
| Questgegenstände / Notizen | E | `StorySync → journalitem/journalref` + `Flags` (De-Dup) | `[StorySync]`,`[Journal_Patch]` | ✅ |
| Journal-Einträge | E | `Journal_Patch → journal:` (idempotent) | `[Journal_Patch]` | ✅ |

### 5.3 Weltobjekte

| Interaktion | Kat. | Mechanismus | Log-Tag | Status |
|-------------|------|-------------|---------|--------|
| Türen öffnen/schließen | S | `Door_Patch → DoorState` (`opened`-Guard) | `[DoorSync]` | ✅ |
| Türen/Objekte abschließen | S | `Lock_Patch → doorlock:` → `LockSync` | `[Lock_Patch]`,`[LockSync]` | 🔬 |
| Aufschließen (Schlüssel/Dietrich) | S | `Lock_Patch → unlock:` (deckt beide) → `LockSync` | `[LockSync]` | 🔬 |
| Vorhängeschloss (Zahlenschloss) | E | `Lock_Patch → padunlock:` (Replay mit `manually:true`) | `[LockSync]` | 🔬 |
| Barrikade bauen/zerstören | E | `Barricade_Patch → BarricadeSync` (+ Join-Snapshot) | `[BarricadeSync]` | 🔬 |
| Barrikade-HP (Gegner-Belagerung) | S+A | `SiegeDamage_Patch → doorbar/winbar` (absolute Rest-HP) | `[BarricadeSync]` | 🔬 |
| Möbel ziehen | K | `ItemDrag_Patch → MovableSync/ObjectMove` (Send) | `[MovableSync]` | 🔬 |
| Schalter / Hebel | S | `ItemSwitch_Patch`/`InteractiveItem_Patch → InteractiveState` | `[InteractiveSync]` | ✅ |
| Weltlampen / Standfackeln | S | `ItemSwitch_Patch → turnOn/turnOff` | `[InteractiveSync]` | 🔬 |
| Gehaltene Lichter (Fackel/Lampe/Flare) | E | `ItemActive_Patch → itemlight:` → `HeldLightSync` | `[HeldLightSync]` | 🔬 |
| Generator an/aus | S | `ItemSwitch_Patch → turnOn/turnOff` (Item→Generator) | `[InteractiveSync]` | 🔬 |
| Generator-Tankfüllung | S | `GeneratorFuel_Patch → genfuel:` (absolut) | `[NetworkManager]` | 🔬 |
| Fallen stellen (Beartrap/Köder) | E | `Build_Patch → placed:` → `BuildSync` (+ Snapshot) | `[Build_Patch]`,`[BuildSync]` | 🔬 |
| Falle auslösen (drauftreten) | E | `Trigger_Patch → trapfire:` (idempotent) | `[Trigger_Patch]` | 🔬 |
| Falle/Objekt aufsammeln | E | `Disarm_Patch → itemdisarm:` (+ Pending) | `[Disarm_Patch]` | ✅ |
| Konstruktion bauen | E | `Build_Patch → construct:` → `BuildSync` (Save-Load-Pfad) | `[BuildSync]` | 🔬 |
| Benzinspur legen & anzünden | E | `Gasoline_Patch → gastrail:/burnliquid:` | `[BuildSync]` | 🔬 |
| Zerstörbare Objekte | S+A | `DamageSync → DamageUpdate` (absolute HP) | `[DamageSync]` | 🔬 |
| Säge / Feeder / Essenz-Maschine | S | `Station_Patch → sawfuel/feeder:` + Container-Kanal | `[StationSync]` | 🔬 |
| Werkbank-Ausbau (Rezeptstufe) | S | `WorkbenchLevel_Patch → wblevel:` (max gewinnt) | `[WorkbenchLevel]` | 🔬 |

### 5.4 Gegner & KI

| Interaktion | Kat. | Mechanismus | Log-Tag | Status |
|-------------|------|-------------|---------|--------|
| Spawn (dynamisch) | A | `EnemySpawn_Patch → espawn:` (Owner-Id, Prefab-Mirror) | `[EnemySpawn]` | 🔬 |
| Spawn (Spawnpunkt-Wurf) | A | `CharacterSpawnPoint` geseedet (Seed ^ Pos-Hash) | `[WorldGenSeed]` | 🔬 |
| Nacht-Spawns (Dedupe) | A | `spawnNightChar` unterdrückt bei Senior <60 m | `[EnemySpawn]` | 🔬 |
| Bewegung / Zustandswechsel | K | `EnemySync → EnemyUpdate` (Send, Clip im State) | `[EnemySync]` | 🔬 |
| Distanz-Ownership (50 m, Hysterese) | A | `EnemySync` claim/freeze, Seniorität | `[EnemySync]` | 🔬 |
| Aggro / Ziel-Umlenkung | K | `EnemySync.NudgeTargets` (best effort, alle 2 s) | `[EnemySync]` | 🔬 |
| Angriff trifft Spieler-Klon | E+A | `EnemySync` Nahkampf→ Schaden an echten Spieler (nur Owner) | `[EnemySync]`,`[DamageSync]` | 🔬 |
| Schaden an fremdem Gegner | A | `CharDamage_Patch → enemyhit:` (nur Owner wendet an) | `[DamageSync]` | 🔬 |
| **Tod (Gegner)** | E | `EnemySync → EnemyUpdate IsAlive=false` **(reliable, v0.6)** | `[EnemySync]` | 🔬 |
| „Gone" (verlässt Radius) | E | `EnemySync → gone` **(reliable, v0.6)**, Freigabe an lokale KI | `[EnemySync]` | 🔬 |
| Beute (Gegner-Tod) | A | Owner-Simulation lässt fallen; Lure-Loot geseedet | `[EnemySync]` | 🔬 |

### 5.5 Spielwelt

| Interaktion | Kat. | Mechanismus | Log-Tag | Status |
|-------------|------|-------------|---------|--------|
| Tageszeit / Tag-Nacht-Wechsel | A | `Controller_Time_Patch → DayNightUpdate` (Host) | `[Controller_Time_Patch]`,`[WorldSync]` | ✅ |
| Uhr nach Schlafen | S | `Sleep_Patch → gametime:` (vorwärts-only) | `[Sleep_Patch]`,`[WorldSync]` | 🔬 |
| Story-Flags / Karma | E | `Flags_Patch → flagb/flagi:` (nur echte Änderungen) | `[StorySync]` | ✅ |
| Träume | A | `Dream_Patch` (Host wählt Preset) | `[Dream_Patch]`,`[StorySync]` | 🔬 |
| Skript-Events (Story-Beats) | E+A | `GameEvents_Patch → gevent:` (Ursprung feuert, Dedup) | `[EventStateSync]` | 🔬 |
| Zufalls-Tagesevents | A | `RandomEvent.fire(true,false)` nur Host + geseedet | `[WorldGenSeed]` | 🔬 |
| Wetter (Regen/Sturm) | A | `WorldGenSeed_Patch` Rain pro Tag geseedet | `[WorldGenSeed]` | 🔬 |
| Zufallsobjekte (Kisten/Kadaver) | A | `WorldGenSeed_Patch` pro Chunk geseedet (v0.5.1) | `[WorldGenSeed]` | 🔬 |
| Kapitel-2-Übergang | E | `Chapter_Patch → chapter2` (Notiz) | `[Chapter_Patch]` | 🔬 |
| NPC-Spawn (Porter, spielergetrieben) | E | `Porter_Patch → locnpc:` (nur `spawnPorter`) | `[Porter_Patch]` | 🔬 |

### 5.6 Multiplayer-spezifisch

| Interaktion | Kat. | Mechanismus | Log-Tag | Status |
|-------------|------|-------------|---------|--------|
| Beitritt während laufendem Spiel | S | Join-Snapshot (World/Movables/Doors/Switches/Lichter/Flares) | `[NetworkManager]` | 🔬 |
| Join-Snapshot: Locks | S | `LockSync.CollectSnapshot` (Session-Historie) | `[LockSync]` | 🔬 |
| Join-Snapshot: Konstruktionen/Placements | S | `BuildSync.CollectSnapshot` (Session-Historie) | `[BuildSync]` | 🔬 |
| Reconnect | S | Handshake-Retry (1 s), Channel-Reset, De-Dup-Reset, Snapshot | `[NetworkLayer]`,`[Reliable]` | 🔬 |
| Zustandssynchronisation (laufend) | alle | alle Module + `Update()`-Pump | diverse | 🔬 |
| **Desync-Erkennung** | P | `SyncCheck → synccheck:` (Digest, 2-Strike) | `[SyncCheck]` | 🔬 |
| **Fehlerkorrektur** | P | Reliability-Layer + SyncCheck-Korrektive + Pending/Retry | `[Reliable]`,`[SyncCheck]` | 🔬 |
| Weltseed-Konsistenzprüfung | P | `worldseed:`-Abgleich beim Join | `[NetworkManager]` | ✅ |
| Zeit-Autorität (Dedicated) | A | `NetworkManager.IsTimeAuthority` (niedrigste Id wählt) | `[NetworkManager]` | 🔬 |
| Dedicated-Server-Relay | S/E | Reliable Relay + relay-by-default (Abschnitt 9) | `[Reliable]` | ✅ |
| Host-Migration | ⚪ | Nicht unterstützt (Abschnitt 6) | — | ⚪ |
| Rollback | ⚪ | Nicht nötig — idempotent/absolut statt Rollback (Abschnitt 6) | — | ⚪ |

---

## 6. Bewusst NICHT synchronisiert (mit Begründung)

Der Grundsatz „im Zweifel synchronisieren" wurde befolgt; die folgenden Punkte
sind nach IL-Analyse **bewusst** lokal, weil ein Sync falsch oder doppelt wäre:

| Punkt | Begründung |
|-------|------------|
| Rein persönliches Feedback aus Events (Hilfetexte, Sounds, Kamera-FX) | Wie im Original an den auslösenden Spieler gerichtet; alles Zustands-Relevante (Flags/Türen/Spawns/Dreams) repliziert separat |
| `Location.enter/leave` | Aktivierungslogik pro Maschine; Inhalt repliziert über Flags/Container/Snapshot |
| Map / MapElement | Eigene Entdeckung pro Spieler (Design) |
| PlayerSkills / Essenz-Kochen | Spieler-Progression (Design); `ExperienceMachine.enable` läuft flag-getrieben auf beiden |
| HidingPlace | KI-Versteck (kein Spieler-Feature); von Gegner-Spiegelung abgedeckt |
| CutsceneManager | Story-Sync läuft über Flags/Dreams |
| Area-Trigger (`EventTriggers`) | Vergleicht gegen `Player.Instance` — Klone können sie nicht auslösen (IL-verifiziert), sonst doppelte Fires |
| Munitionszähler / persönliches Inventar-Detail | Pro Spieler; nur geteilte Container replizieren |
| **Host-Migration** | Enemy-Ownership & host-autoritative Tagesevents brauchen einen stabilen In-Game-Host; Migration würde Autoritäts-Neuwahl + State-Transfer erfordern (Roadmap) |
| **Rollback** | Bewusst durch Design ersetzt: alle S/E-Kanäle sind idempotent oder absolut, K-Kanäle selbstheilend, P-Kanal korrigiert vorwärts — es gibt keinen Zustand, der zurückgerollt werden müsste |
| Dedicated Server als Uhr-Autorität | `DarkwoodMP.Server` ist ein Reliable-fähiges Relay ohne Spiel-Kopplung; In-Game-Host ist der unterstützte Weg |

Restrisiken (dokumentiert, nicht reproduzierbar-kritisch):
- Gegner im 50–60 m-Grenzband werden bewusst beidseitig simuliert (Hysterese).
- Pool-Zustands-Drift bei fehlgeschlagenem Chunk-Spawn (selten; SyncCheck fängt
  stabile Folgen nicht ab, da Zufallsobjekte nicht im Digest sind — nächste
  Stufe wäre Host-Broadcast der Objektliste).

---

## 7. Test-Playbook (Live-Verifikation pro Szenario)

Aufbau: zwei Instanzen, **identischer `WorldSeed`** in beiden Configs, beide
starten ein NEUES Spiel. Ein Fenster hostet, das andere verbindet. Logs sind
MelonLoader-Konsolen; filtere nach den Tags aus Spalte „Log-Tag".

**Generischer Ablauf je Interaktion (Prozessschritt 6 der Aufgabe):**
1. **Host→Client:** Aktion im Host-Fenster auslösen → Ziel-Tag im Client-Log
   erscheint mit „Applied/Adopted/Remote…".
2. **Client→Host:** Aktion im Client-Fenster → Host-Log spiegelt.
3. **Mehrere Clients:** dritte Instanz; Aktion muss bei beiden anderen landen
   (Host-Relay). Ownership-Konflikte lösen sich über Seniorität (niedrigere Id).
4. **Hohe Latenz:** `clumsy`/`tc` mit +300 ms. S/E dürfen verzögert, aber
   vollständig ankommen; `[Reliable] resent>0` ist normal, `dropped=0` erwartet.
5. **Paketverlust:** `clumsy` mit 10–20 % Drop. S/E müssen dennoch konvergieren
   (Resend); K darf ruckeln. Verifikation: Endzustand identisch.
6. **Join während Sitzung:** Client nach einigen Aktionen frisch verbinden →
   Join-Snapshot bringt Türen/Locks/Barrikaden/Builds/Lichter nach.

**Gezielte Regressions-Checks (die v0.6-Neuerungen):**

| Test | Vorgehen | Erwartetes Log / Ergebnis |
|------|----------|---------------------------|
| Reliability greift | Latenz+Verlust an, viele Türen/Locks schalten | `[Reliable] sent/acked` steigen paarweise, `dropped=0` |
| Reliable-Reconnect | Client killen & neu verbinden | `[NetworkLayer] Peer … connected`, Channel-Reset, Snapshot spielt |
| **Gegner-Tod bei Verlust** | 15 % Drop, Gegner an Owner-Standort töten | Kein „Statuen-Kadaver" auf der Mirror-Seite; Gegner verschwindet dort |
| Desync-Erkennung | Flag/Lock künstlich divergieren lassen (z.B. eine Instanz kurz trennen, Aktion, wieder verbinden) | `[SyncCheck] DESYNC <feld>` nach 2 Checks, dann `Corrective: re-broadcasting…`, danach Konvergenz |
| Kein Fehlalarm | Normal spielen, In-Flight-Änderungen | Höchstens `Transient mismatch … watching` (VerboseLogging), **kein** `DESYNC` |
| Keine Item-Dup | Barrikade/Konstruktion remote nachspielen | Keine Planken-Rückerstattung (Refund-Suppression greift) |

**Automatischer Wächter:** `SyncCheck` läuft dauerhaft mit und meldet jede
stabile, nicht aufgelöste Abweichung als `[SyncCheck] DESYNC` in Log **und**
Chat — das ist der von der Aufgabe geforderte „Prüfmechanismus, der automatisch
erkennt, wenn eine Aktion nicht synchronisiert wurde".

---

## 8. Änderungen in v0.6 (diese Iteration)

1. **Echter Reliability-Layer** (`NetworkLayer`, `ServerHostService`):
   Ack/Resend/De-Dup-Envelope für alle `SendReliable`-Kanäle; Handshake-Retry;
   Reconnect-fähiger Channel-Reset. Schließt die dokumentierte Lücke „Kein
   Transport-Reliability-Layer".
2. **Automatische Desync-Erkennung** (`SyncCheck`): periodischer Zustands-Digest
   (Tag, Uhr, Werkbank, Flags-/Lock-/Build-Hash), 2-Strike-Vergleich,
   idempotente Vorwärts-Korrektive. Schließt die Anforderung „Prüfmechanismen …
   automatisch erkennen".
3. **Gegner-Tod & „gone" jetzt reliable**: einmalige Terminal-Ereignisse gingen
   über den verlustbehafteten K-Kanal → bei Paketverlust blieb ein eingefrorener
   Kadaver-Spiegel. Jetzt `SendReliable`, Alive-Strom bleibt unreliable.
4. Join-Snapshot-Auslieferung jetzt reliable (jeder Eintrag genau einmal).
5. **Dedicated Server vollwertig** (Abschnitt 9).

---

## 9. Dedicated Server (DarkwoodMP.Server)

Zwei Betriebsmodi für einen Mitspieler-Server:

| Weg | Autorität | Verwendung |
|-----|-----------|------------|
| **In-Game-Host** | Der Host (Id 0) ist `IsHost` | Ein-Klick, empfohlen |
| **Dedicated Server** | Niedrigster Client (`IsTimeAuthority`) | 24/7-Server ohne offenes Spiel |

**Das Kernproblem (behoben in v0.6):** Im Dedicated-Modus ist NIEMAND `IsHost`.
Alle host-autoritativen Verhalten (Tag/Nacht, Träume, geplante Zufallsevents)
waren an `IsHost` gekoppelt und liefen damit bei niemandem; der Server erfand
eine eigene, vom Spiel entkoppelte Uhr (~48 h/Tag), die mit den lokalen
Spiel-Controllern kollidierte.

**Lösung — zwei Teile:**

1. **Zeit-Autoritäts-Wahl (Mod):** `NetworkManager.IsTimeAuthority` ist wahr
   für den In-Game-Host ODER — auf einem Dedicated Server — für den Client mit
   der niedrigsten Id. Deterministische Totalordnung, automatische Neuwahl beim
   Verlassen. **Beweisbar No-Op für den Host-Pfad:** ein Host-Client sieht immer
   Id 0 in der Spielerliste und kann daher nie höher ranken. Angewandt auf
   `Controller_Time_Patch`, `WorldSync` (Tag/Nacht anwenden), `SyncCheck`
   (Day-Korrektiv), `Dream_Patch`, `GameEvents_Patch` (RandomEvent-Gate).

2. **Reiner Reliable-Relay (Server):**
   - **Ausgehende Reliability:** eigener Sende-Envelope-Layer pro Endpoint
     (Sequenz/Pending/Resend/Ack). Reliable EINGEHENDE Pakete werden reliable
     RE-ORIGINIERT → Zustellung über den Relay garantiert (vorher degradierte
     reliable Traffic auf dem zweiten Hop still zu UDP).
   - **Relay-by-default:** unbekannte Pakettypen werden weitergereicht statt
     verworfen → protokoll-komplett, bleibt automatisch aktuell.
   - **Reliable Control/Snapshot:** PlayerList, PlayerJoined/Left, Chat,
     EventTrigger, GameStateSync und der komplette Join-Snapshot laufen reliable.
   - **Keine erfundene Uhr:** Config-Flag `AuthoritativeWorld` (Default FALSE)
     gatet die Server-Simulation. Im Default beobachtet der Server nur die
     Tag/Nacht-Broadcasts der Autorität für den Join-Snapshot.

**Verifikation (Protokoll-Test, automatisiert):** Envelope `0xE0` → Server-Ack
`0xE1` → reliable Snapshot-Envelopes → Resend bei fehlendem Ack → Dedupe bei
doppelter Sequenz — alles bestätigt. Ein 2-Client-Vollspiel-Playtest über den
Dedicated Server steht noch aus (🔬).

**Bekannte Dedicated-Restpunkte:** keine Gegner-„Host-Sovereignty" (Distanz +
Seniorität übernehmen); Host-Migration weiterhin nicht unterstützt;
`AuthoritativeWorld=true` ist experimentell.

---

## 10. Vollständigkeits-Audit gegen das Spiel (v0.6.1)

Statt die Matrix nur gegen die eigenen Patches zu prüfen (zirkulär), wurde die
**echte Interaktionsfläche des Spiels** aus `Assembly-CSharp.dll` extrahiert und
gegengeprüft. Werkzeug: MetadataLoadContext + Mono.Cecil (liest echte
Methodenkörper). Vorgehen:

1. **Komplette `Player`-Methodenfläche** (≈220 Methoden) gedumpt und jede
   klassifiziert: synchronisiert / bewusst persönlich / Lücke.
2. **Alle ~1948 Typen** aufgelistet und jeder MonoBehaviour-basierte
   interaktive/zustandsbehaftete Typ geprüft.
3. **IL-Trace** der verdächtigen Methoden (Aufrufziele + `Core.AddPrefab`-Pfade)
   zur definitiven Klärung — kein Raten.

### Gefundene Lücke (behoben)

- **Tod-Loot-Beutel** (`Player.dropBody`, per IL im `<onDeath>d__435`-Coroutine
  3× mit DERSELBEN `playerPos` aufgerufen): jeder Aufruf spawnt einen
  `deathDrop`-Beutel an derselben Position und legt eine ZUFÄLLIGE Teilmenge der
  Items hinein. War lokal, ungesynct, ungeseedet → der Partner sah die Leiche
  nie. **Behoben** (`DeathDrop_Patch` + `ContainerSync.OnRemoteDeathDrop`): der
  tatsächliche Inhalt wird gesendet (kein Neu-Würfeln), der Partner spawnt
  identische Beutel; Looten läuft über das bestehende Container-Matching.
  - Nachaudit-Korrektur (drei per IL belegte Fehler des ersten Wurfs): (1)
    `initSlots` braucht `true` (das Spiel ruft `initSlots(true)`) — vorher
    `Invoke(inv, null)` = `TargetParameterCountException` → leerer Beutel; (2)
    die Orts-Idempotenz (≤2 m) kollabierte alle drei ko-lokalen Beutel zu einem
    → 2/3 Beute verloren; (3) `name@x,z` kollidierte für die drei gleichnamigen
    Beutel → Teil-Loot leerte den falschen. Fix: global eindeutiges Drop-Token
    `<clientId>-<seq>` pro Beutel, auf beiden Maschinen an den Objektnamen
    angehängt → exakte Idempotenz + eindeutige Container-ID. Save-sicher
    (`SaveableObject` = `prefabName`+`uniqueId`, nicht `GameObject.name`).
    Format: `deathdrop:<prefab>:<token>:<x,y,z>:<items>`.

### Geprüft & bewusst NICHT gesynct (mit IL-Begründung)

| Interaktion | Warum korrekt lokal / abgedeckt |
|-------------|--------------------------------|
| `getHitByShadow`, `bansheeScreamHit` | Rufen `Player.setHealth` (gepatcht) → Health-Bar synct bereits. Keine Lücke. |
| `tryToSpawnShadow` (`characters/fakechars/shadow`) | Persönliche Nacht-Halluzination; via direktem `Core.AddPrefab`. Als `Character` von der EnemySync-Distanzspiegelung erfasst, falls sichtbar. |
| `special_spawnDreamForestSpirit`, `spawnWolfInCurrentHideout` | Direkter `Core.AddPrefab`/`Location.spawnWolf`; erzeugt `Character` → von EnemySync gespiegelt (nicht deterministisch, aber sichtbar). |
| `Examinable.examine()` | Ruft `Core.sendTriggerInfo` → feuert Event-Trigger; Effekte (Flags/Türen/Spawns) replizieren über ihre Kanäle. Text ist persönlich (korrekt). |
| `die → respawnAllEnemies` / `despawnCharacters` | Gegner sind ohnehin distanz-gespiegelt, kein deterministisch geteilter Zustand; das reliable „gone"-Signal + kontinuierliche EnemySync heilen die Population selbst. |
| `DarkwoodTree` | **Keine Methoden** — Bäume sind Kulisse, nicht abbaubar. Kein Interaktions-Gap. |
| `craft/upgrade/repair/exp/skills/hunger/stamina/FOV/shadows` | Persönliche Progression/Survival-Ressourcen (Design); Werkbank-Stufe separat gesynct. |
| `ObjectStages`, `Vine`, `MagicContainer`, `Resonator`, `TimeSkip` | Zeit-/bedingungsgetriebene Umgebungslogik; konvergiert über gesyncte Flags/Uhr, oder Character-Spawns über EnemySync. |

**Fazit:** Nach dem vollständigen Abgleich der Spiel-Methodenfläche ist die
einzige echte Sync-Lücke (Tod-Loot) geschlossen. Alle übrigen Spieler-
interaktionen sind entweder synchronisiert oder aus Design-Gründen persönlich
(IL-belegt). Restpunkt bleibt der 2-Instanz-Playtest (🔬) — die Abdeckung selbst
ist jetzt gegen das echte Spiel verifiziert, nicht nur gegen die eigenen Docs.
